using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Threading;
using BrainFuel.Controls;
using BrainFuel.Services;

namespace BrainFuel.ViewModels;

public class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _relativeTimer;

    // Transient failures (network blip, throttling) heal on their own — retry
    // well before the configured interval instead of leaving the card stale.
    private static readonly TimeSpan TransientRetryInterval = TimeSpan.FromSeconds(45);

    private GlmUsageClient? _client;
    private UsageSnapshot? _last;
    private UsageFailureKind? _failureKind;
    private bool _inError;
    private bool _hourlyAlerted;
    private bool _weeklyAlerted;
    private readonly QuotaBurnTracker _hourlyBurn = new();
    private readonly QuotaBurnTracker _weeklyBurn = new();
    private readonly UsageHistoryStore _history;

    // Graph ranges shown in the detail panel.
    private static readonly TimeSpan HourlyGraphRange = TimeSpan.FromHours(24);
    private static readonly TimeSpan WeeklyGraphRange = TimeSpan.FromDays(7);

    public Action<string, string>? OnNotify { get; set; }

    public MainViewModel(AppSettings settings)
    {
        _settings = settings;
        _client = CreateClient();
        _history = UsageHistoryStore.Load(UsageHistoryStore.DefaultPath);
        RebuildGraphs();
        CardOpacity = settings.CardOpacity;
        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(Math.Max(1, _settings.RefreshIntervalMinutes)),
        };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        _relativeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _relativeTimer.Tick += (_, _) => UpdateTexts();
    }

    public void Start()
    {
        _ = RefreshAsync();
        _refreshTimer.Start();
        _relativeTimer.Start();
    }

    public async Task RefreshAsync()
    {
        // Manual clicks and the periodic timer can arrive close together. Treat a
        // refresh as a single-flight operation so the same key never creates a
        // burst of duplicate quota requests.
        if (IsRefreshing)
            return;

        IsRefreshing = true;
        try
        {
            // If a protected credential was temporarily unavailable at startup,
            // every normal quota refresh is also a chance to recover it. No restart
            // or manual re-entry is needed once the OS credential service returns.
            if (string.IsNullOrWhiteSpace(_settings.ApiKey) && _settings.ApiKeyConfigured == true)
            {
                if (SettingsService.TryRefreshProtectedApiKey(_settings))
                {
                    _client?.Dispose();
                    _client = CreateClient();
                }
            }

            if (string.IsNullOrWhiteSpace(_settings.ApiKey))
            {
                _last = null;
                _failureKind = null;
                _inError = false;
                return;
            }

            try
            {
                if (_client is null) _client = CreateClient();
                var snap = await _client.GetUsageAsync();
                _last = snap;
                _failureKind = null;
                _inError = false;

                // Feed the burn-rate estimators only from real observations;
                // a reset inside the tracker drops stale-window samples.
                if (snap.HasHourly) _hourlyBurn.AddSample(snap.FetchedAt, snap.HourlyUsedPct);
                if (snap.HasWeekly) _weeklyBurn.AddSample(snap.FetchedAt, snap.WeeklyUsedPct);

                _history.Append(new UsageSample(
                    snap.FetchedAt,
                    snap.HasHourly ? snap.HourlyUsedPct : double.NaN,
                    snap.HasWeekly ? snap.WeeklyUsedPct : double.NaN));
                _history.Prune(snap.FetchedAt);
                _history.Save();
            }
            catch (UsageRequestException ex)
            {
                _failureKind = ex.Kind;
                _inError = true;
                AppLog.Error($"refresh failed ({ex.Kind}): {ex.Message}");
            }
            catch (Exception ex)
            {
                _failureKind = UsageFailureKind.Unknown;
                _inError = true;
                AppLog.Error($"refresh failed (Unknown): {ex.Message}");
            }
        }
        finally
        {
            IsRefreshing = false;
            ApplySnapshot();
            ApplyRetryInterval();
        }
    }

    /// <summary>
    /// Shortens the wait after a transient failure (network, throttling, 5xx)
    /// so recovery needs at most ~45 s; permanent causes (bad key, no plan)
    /// keep the configured interval because only the user can fix them.
    /// </summary>
    private void ApplyRetryInterval()
    {
        _refreshTimer.Interval = _inError && _failureKind is { } kind && UsageFailureText.IsTransient(kind)
            ? TransientRetryInterval
            : ConfiguredInterval;
    }

    private TimeSpan ConfiguredInterval =>
        TimeSpan.FromMinutes(Math.Max(1, _settings.RefreshIntervalMinutes));

    public void OnSettingsChanged()
    {
        _client?.Dispose();
        _client = CreateClient();
        _failureKind = null;
        _refreshTimer.Interval = ConfiguredInterval;
        CardOpacity = _settings.CardOpacity;
        UpdateTexts();
        _ = RefreshAsync();
    }

    private GlmUsageClient CreateClient() =>
        new(_settings.BaseDomain, _settings.ApiKey ?? string.Empty, SettingsService.DebugPath);

    private void ApplySnapshot()
    {
        var snap = _last;
        double weeklyUsed = snap?.WeeklyUsedPct ?? 0;
        WeeklyProgress = (_settings.WeeklyDisplayStyle == DisplayStyle.Remaining ? 100 - weeklyUsed : weeklyUsed) / 100.0;

        double hourlyUsed = snap?.HourlyUsedPct ?? 0;
        HourlyProgress = (_settings.HourlyDisplayStyle == DisplayStyle.Remaining ? 100 - hourlyUsed : hourlyUsed) / 100.0;

        UpdateTexts();
        RebuildGraphs();
        CheckAlerts();
    }

    /// <summary>Rebuilds the detail-panel series from persisted history.</summary>
    private void RebuildGraphs()
    {
        var end = DateTimeOffset.Now;
        HourlyGraph = BuildSeries(end - HourlyGraphRange, end, s => s.HourlyPct);
        WeeklyGraph = BuildSeries(end - WeeklyGraphRange, end, s => s.WeeklyPct);
    }

    private IReadOnlyList<GraphPoint> BuildSeries(DateTimeOffset start, DateTimeOffset end, Func<UsageSample, double> pick)
    {
        var points = new List<GraphPoint>();
        double spanMinutes = (end - start).TotalMinutes;
        if (spanMinutes <= 0) return points;

        foreach (var s in _history.Samples)
        {
            if (s.At < start) continue;
            var v = pick(s);
            if (double.IsNaN(v)) continue;
            points.Add(new GraphPoint(
                Math.Clamp((s.At - start).TotalMinutes / spanMinutes, 0, 1),
                Math.Clamp(v, 0, 100)));
        }
        return points;
    }

    private void CheckAlerts()
    {
        if (!_settings.NotifyEnabled || OnNotify is null) return;
        var snap = _last;
        double h = snap?.HourlyUsedPct ?? 0;
        double w = snap?.WeeklyUsedPct ?? 0;
        int thr = Math.Clamp(_settings.NotifyThreshold, 1, 99);

        if (snap?.HasHourly == true && h >= thr && !_hourlyAlerted)
        {
            _hourlyAlerted = true;
            OnNotify(Strings.Get("NotifyHourlyTitle"), FunBody("NotifyHourlyBody", h, _hourlyBurn, h));
        }
        if (h < thr - 5) _hourlyAlerted = false;

        if (snap?.HasWeekly == true && w >= thr && !_weeklyAlerted)
        {
            _weeklyAlerted = true;
            OnNotify(Strings.Get("NotifyWeeklyTitle"), FunBody("NotifyWeeklyBody", w, _weeklyBurn, w));
        }
        if (w < thr - 5) _weeklyAlerted = false;
    }

    /// <summary>One of three flavor lines, plus a burn-rate projection when available.</summary>
    private static string FunBody(string keyPrefix, double usedPct, QuotaBurnTracker burn, double usedForProjection)
    {
        var body = Strings.Get($"{keyPrefix}{Random.Shared.Next(1, 4)}", Math.Round(usedPct));
        if (burn.ProjectHoursToExhaustion(usedForProjection, DateTimeOffset.Now) is { } hours)
            body += Strings.Get("NotifyBurnSuffix", FormatSpan(hours));
        return body;
    }

    private static string FormatSpan(double hours)
    {
        if (hours < 1) return Strings.Get("MinutesLater", (int)Math.Ceiling(hours * 60));
        if (hours < 48) return Strings.Get("HoursLater", Math.Round(hours));
        return Strings.Get("DaysLater", Math.Round(hours / 24));
    }

    private void UpdateTexts()
    {
        var snap = _last;

        double weeklyUsed = snap?.WeeklyUsedPct ?? 0;
        double weeklyShown = _settings.WeeklyDisplayStyle == DisplayStyle.Remaining ? 100 - weeklyUsed : weeklyUsed;
        WeeklyPercentText = FormatPct(weeklyShown, snap?.HasWeekly);

        double hourlyUsed = snap?.HourlyUsedPct ?? 0;
        double hourlyShown = _settings.HourlyDisplayStyle == DisplayStyle.Remaining ? 100 - hourlyUsed : hourlyUsed;
        HourlyPercentText = FormatPct(hourlyShown, snap?.HasHourly);

        WeeklySubText = snap?.WeeklyResetAt is { } wr ? FutureWords(wr) : Strings.Get("None");
        HourlySubText = snap?.HourlyResetAt is { } hr ? FutureWords(hr) : Strings.Get("None");

        // Detail-panel variants carry a "resets …" prefix.
        WeeklyResetText = snap?.WeeklyResetAt is { } wr2 ? Strings.Get("DetailResets", FutureWords(wr2)) : Strings.Get("None");
        HourlyResetText = snap?.HourlyResetAt is { } hr2 ? Strings.Get("DetailResets", FutureWords(hr2)) : Strings.Get("None");

        HourlySeverity = snap?.HasHourly == true ? Severity.FromUsedPct(hourlyUsed) : SeverityLevel.Calm;
        WeeklySeverity = snap?.HasWeekly == true ? Severity.FromUsedPct(weeklyUsed) : SeverityLevel.Calm;
        UpdateMini(snap, hourlyUsed, weeklyUsed);
        UpdateBurnTexts(snap);
        PlanLevelText = string.IsNullOrWhiteSpace(snap?.PlanLevel) ? "—" : snap!.PlanLevel!;

        if (IsRefreshing)
            RefreshAgoText = Strings.Get("Refreshing");
        else if (string.IsNullOrWhiteSpace(_settings.ApiKey) && _settings.ApiKeyConfigured == true)
            RefreshAgoText = Strings.Get("CredentialUnavailableCard");
        else if (!_settings.IsValid)
            RefreshAgoText = Strings.Get("NotConfigured");
        else if (_inError)
            RefreshAgoText = UsageFailureText.Card(_failureKind ?? UsageFailureKind.Unknown);
        else if (snap is null)
            RefreshAgoText = Strings.Get("Refreshing");
        else
            RefreshAgoText = PastWords(snap.FetchedAt);

        // Hover detail: failures show the long-form explanation (same text the
        // settings dialog shows) plus the log path; success shows per-window
        // numbers with burn-rate projections.
        StatusTooltip = _inError
            ? UsageFailureText.Validation(_failureKind ?? UsageFailureKind.Unknown)
                + "\n" + Strings.Get("ErrLogAt", AppLog.LogPath)
            : snap is null ? null : BuildSuccessTooltip(snap);

        IsError = _inError;
    }

    /// <summary>Mini card tracks whichever window is closer to exhaustion.</summary>
    private void UpdateMini(UsageSnapshot? snap, double hourlyUsed, double weeklyUsed)
    {
        bool hasH = snap?.HasHourly == true;
        bool hasW = snap?.HasWeekly == true;
        if (!hasH && !hasW)
        {
            MiniPercentText = "--";
            MiniProgress = 0;
            MiniSeverity = SeverityLevel.Calm;
            MiniLabelText = Strings.Get("LblHourly");
            return;
        }

        bool pickHourly = hasH && (!hasW || hourlyUsed >= weeklyUsed);
        double used = pickHourly ? hourlyUsed : weeklyUsed;
        bool remaining = pickHourly
            ? _settings.HourlyDisplayStyle == DisplayStyle.Remaining
            : _settings.WeeklyDisplayStyle == DisplayStyle.Remaining;

        MiniPercentText = FormatPct(remaining ? 100 - used : used, true);
        MiniProgress = used / 100.0;
        MiniSeverity = Severity.FromUsedPct(used);
        MiniLabelText = Strings.Get(pickHourly ? "LblHourly" : "LblWeekly");
    }

    /// <summary>Burn-rate lines shared by the card tooltip and the detail panel.</summary>
    private void UpdateBurnTexts(UsageSnapshot? snap)
    {
        HourlyBurnText = BurnLine(snap?.HasHourly == true, _hourlyBurn, snap?.HourlyUsedPct ?? 0);
        WeeklyBurnText = BurnLine(snap?.HasWeekly == true, _weeklyBurn, snap?.WeeklyUsedPct ?? 0);
    }

    private static string BurnLine(bool has, QuotaBurnTracker burn, double usedPct) =>
        has && burn.ProjectHoursToExhaustion(usedPct, DateTimeOffset.Now) is { } hours
            && burn.RatePctPerHour is { } rate
            ? Strings.Get("TipBurn", Math.Round(rate), FormatSpan(hours))
            : string.Empty;

    private string BuildSuccessTooltip(UsageSnapshot snap)
    {
        var lines = new List<string>();
        if (snap.HasHourly)
        {
            var line = Strings.Get("TipHourly", Math.Round(snap.HourlyUsedPct));
            if (snap.HourlyResetAt is { } r) line += " · " + FutureWords(r);
            lines.Add(line);
            if (HourlyBurnText.Length > 0) lines.Add(HourlyBurnText);
        }
        if (snap.HasWeekly)
        {
            var line = Strings.Get("TipWeekly", Math.Round(snap.WeeklyUsedPct));
            if (snap.WeeklyResetAt is { } r) line += " · " + FutureWords(r);
            lines.Add(line);
            if (WeeklyBurnText.Length > 0) lines.Add(WeeklyBurnText);
        }
        lines.Add(Strings.Get("TipSizeHint"));
        return string.Join("\n", lines);
    }

    private static string FormatPct(double value, bool? has)
        => has == false ? "--" : $"{Math.Round(value):0}%";

    private static string PastWords(DateTimeOffset t)
    {
        var d = DateTimeOffset.Now - t;
        if (d.TotalMinutes < 1) return Strings.Get("JustNow");
        if (d.TotalHours < 1) return Strings.Get("MinutesAgo", (int)d.TotalMinutes);
        if (d.TotalDays < 1) return Strings.Get("HoursAgo", (int)d.TotalHours);
        return Strings.Get("DaysAgo", (int)d.TotalDays);
    }

    private static string FutureWords(DateTimeOffset t)
    {
        var d = t - DateTimeOffset.Now;
        if (d.TotalMinutes <= 0) return Strings.Get("ResettingSoon");
        if (d.TotalHours < 1) return Strings.Get("MinutesLater", (int)Math.Ceiling(d.TotalMinutes));
        if (d.TotalDays < 1) return Strings.Get("HoursLater", (int)Math.Round(d.TotalHours));
        return Strings.Get("DaysLater", (int)Math.Round(d.TotalDays));
    }

    public double WeeklyProgress { get => _weeklyProgress; set => Set(ref _weeklyProgress, value); }
    private double _weeklyProgress;
    public double HourlyProgress { get => _hourlyProgress; set => Set(ref _hourlyProgress, value); }
    private double _hourlyProgress;

    public string WeeklyPercentText { get => _weeklyPercentText; set => Set(ref _weeklyPercentText, value); }
    private string _weeklyPercentText = "--";
    public string HourlyPercentText { get => _hourlyPercentText; set => Set(ref _hourlyPercentText, value); }
    private string _hourlyPercentText = "--";
    public string WeeklySubText { get => _weeklySubText; set => Set(ref _weeklySubText, value); }
    private string _weeklySubText = "周用量";
    public string HourlySubText { get => _hourlySubText; set => Set(ref _hourlySubText, value); }
    private string _hourlySubText = "5 小时";
    public string WeeklyResetText { get => _weeklyResetText; set => Set(ref _weeklyResetText, value); }
    private string _weeklyResetText = "—";
    public string HourlyResetText { get => _hourlyResetText; set => Set(ref _hourlyResetText, value); }
    private string _hourlyResetText = "—";
    public string RefreshAgoText { get => _refreshAgoText; set => Set(ref _refreshAgoText, value); }
    private string _refreshAgoText = "刷新中…";
    public string? StatusTooltip { get => _statusTooltip; set => Set(ref _statusTooltip, value); }
    private string? _statusTooltip;

    // Severity drives the code-behind recolor (ring, dots, percent text).
    public SeverityLevel HourlySeverity { get => _hourlySeverity; set => Set(ref _hourlySeverity, value); }
    private SeverityLevel _hourlySeverity;
    public SeverityLevel WeeklySeverity { get => _weeklySeverity; set => Set(ref _weeklySeverity, value); }
    private SeverityLevel _weeklySeverity;

    // Mini card (compact mode): one ring, the more urgent window.
    public string MiniPercentText { get => _miniPercentText; set => Set(ref _miniPercentText, value); }
    private string _miniPercentText = "--";
    public double MiniProgress { get => _miniProgress; set => Set(ref _miniProgress, value); }
    private double _miniProgress;
    public SeverityLevel MiniSeverity { get => _miniSeverity; set => Set(ref _miniSeverity, value); }
    private SeverityLevel _miniSeverity;
    public string MiniLabelText { get => _miniLabelText; set => Set(ref _miniLabelText, value); }
    private string _miniLabelText = "5 小时";

    // Detail panel (double-click): burn lines and plan level beside the rings.
    public string HourlyBurnText { get => _hourlyBurnText; set => Set(ref _hourlyBurnText, value); }
    private string _hourlyBurnText = "";
    public string WeeklyBurnText { get => _weeklyBurnText; set => Set(ref _weeklyBurnText, value); }
    private string _weeklyBurnText = "";
    public string PlanLevelText { get => _planLevelText; set => Set(ref _planLevelText, value); }
    private string _planLevelText = "—";

    // Detail-panel history series (refresh with every successful fetch).
    public IReadOnlyList<GraphPoint> HourlyGraph { get => _hourlyGraph; private set { _hourlyGraph = value; NotifyGraphChanged(nameof(HourlyGraph)); } }
    private IReadOnlyList<GraphPoint> _hourlyGraph = Array.Empty<GraphPoint>();
    public IReadOnlyList<GraphPoint> WeeklyGraph { get => _weeklyGraph; private set { _weeklyGraph = value; NotifyGraphChanged(nameof(WeeklyGraph)); } }
    private IReadOnlyList<GraphPoint> _weeklyGraph = Array.Empty<GraphPoint>();
    public bool HasHourlyGraph => HourlyGraph.Count > 1;
    public bool HasWeeklyGraph => WeeklyGraph.Count > 1;

    private void NotifyGraphChanged(string name)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name == nameof(HourlyGraph) ? nameof(HasHourlyGraph) : nameof(HasWeeklyGraph)));
    }
    public double CardOpacity { get => _cardOpacity; set => Set(ref _cardOpacity, value); }
    private double _cardOpacity = 1.0;
    public bool IsError { get => _isError; set => Set(ref _isError, value); }
    private bool _isError;

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (_isRefreshing == value) return;
            _isRefreshing = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRefreshing)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanRefresh)));
            UpdateTexts();
        }
    }
    private bool _isRefreshing;
    public bool CanRefresh => !IsRefreshing;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (!Equals(field, value))
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public void Dispose()
    {
        _refreshTimer.Stop();
        _relativeTimer.Stop();
        _client?.Dispose();
    }
}
