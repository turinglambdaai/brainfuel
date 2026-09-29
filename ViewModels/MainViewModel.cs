using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
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

    // Graph ranges shown in the detail panel.
    private static readonly TimeSpan HourlyGraphRange = TimeSpan.FromHours(24);
    private static readonly TimeSpan WeeklyGraphRange = TimeSpan.FromDays(7);

    /// <summary>Per-account runtime state. Every configured account is polled
    /// so histories and alerts keep working for all of them; the card shows
    /// the active one.</summary>
    private sealed class AccountState
    {
        public AccountConfig Config;
        public IQuotaClient? Client;
        public UsageSnapshot? Last;
        public UsageFailureKind? FailureKind;
        public bool InError;
        public bool HourlyAlerted;
        public bool WeeklyAlerted;
        public readonly QuotaBurnTracker HourlyBurn = new();
        public readonly QuotaBurnTracker WeeklyBurn = new();
        public readonly UsageHistoryStore History;

        public AccountState(AccountConfig config)
        {
            Config = config;
            History = UsageHistoryStore.Load(UsageHistoryStore.PathForAccount(config.Id));
        }
    }

    private readonly List<AccountState> _accounts = new();

    private AccountState? Active =>
        _accounts.FirstOrDefault(a => a.Config.Id == _settings.ActiveAccountId) ?? _accounts.FirstOrDefault();

    public Action<string, string>? OnNotify { get; set; }

    public MainViewModel(AppSettings settings)
    {
        _settings = settings;
        BuildAccounts();
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

    /// <summary>Aligns runtime state with the account list in settings,
    /// preserving existing snapshots/trackers/histories across edits.</summary>
    private void BuildAccounts()
    {
        foreach (var state in _accounts.Where(s => _settings.Accounts.All(a => a.Id != s.Config.Id)))
            state.Client?.Dispose();
        _accounts.RemoveAll(s => _settings.Accounts.All(a => a.Id != s.Config.Id));

        foreach (var config in _settings.Accounts)
            if (_accounts.All(s => s.Config.Id != config.Id))
                _accounts.Add(new AccountState(config));

        // Keep config objects in sync (name/domain may have been edited).
        foreach (var state in _accounts)
        {
            var config = _settings.Accounts.First(a => a.Id == state.Config.Id);
            state.Config = config;
            if (state.Client is not null && state.InError is false)
                state.Client = CreateClient(config); // domain may have changed
        }
        ActiveAccountName = ComputeAccountLabel();
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
            // If protected credentials were temporarily unavailable at startup,
            // every refresh is a chance to recover them — no restart needed.
            if (ApiKeyStorageUnavailable())
                SettingsService.TryRefreshProtectedKeys(_settings);

            foreach (var state in _accounts)
                await RefreshAccount(state);
        }
        finally
        {
            IsRefreshing = false;
            ApplySnapshot();
            ApplyRetryInterval();
        }
    }

    private static bool ApiKeyStorageUnavailable() =>
        SettingsService.ApiKeyStorageState == ApiKeyStorageState.ProtectedUnavailable;

    private async Task RefreshAccount(AccountState state)
    {
        // Key-based providers resolve from the keyring; CLI-login providers
        // (Codex/Claude) read their OAuth token fresh from the local install.
        bool keyBased = state.Config.Provider is not ("codex" or "claude");
        var key = keyBased
            ? SettingsService.GetKey(state.Config.Id)
            : LocalCliCredentials.ReadToken(state.Config.Provider);
        if (string.IsNullOrWhiteSpace(key))
        {
            state.Last = null;
            state.FailureKind = null;
            state.InError = false;
            return;
        }

        try
        {
            state.Client ??= CreateClient(state.Config);
            var snap = await state.Client.GetUsageAsync();
            state.Last = snap;
            state.FailureKind = null;
            state.InError = false;

            // Feed the burn-rate estimators only from real observations; a
            // reset inside the tracker drops stale-window samples.
            if (snap.HasHourly) state.HourlyBurn.AddSample(snap.FetchedAt, snap.HourlyUsedPct);
            if (snap.HasWeekly) state.WeeklyBurn.AddSample(snap.FetchedAt, snap.WeeklyUsedPct);

            state.History.Append(new UsageSample(
                snap.FetchedAt,
                snap.HasHourly ? snap.HourlyUsedPct : double.NaN,
                snap.HasWeekly ? snap.WeeklyUsedPct : double.NaN));
            state.History.Prune(snap.FetchedAt);
            state.History.Save();
        }
        catch (UsageRequestException ex)
        {
            state.FailureKind = ex.Kind;
            state.InError = true;
            AppLog.Error($"refresh failed [{AccountLabel(state.Config)}] ({ex.Kind}): {ex.Message}");
        }
        catch (Exception ex)
        {
            state.FailureKind = UsageFailureKind.Unknown;
            state.InError = true;
            AppLog.Error($"refresh failed [{AccountLabel(state.Config)}] (Unknown): {ex.Message}");
        }
    }

    private static string AccountLabel(AccountConfig config) =>
        string.IsNullOrWhiteSpace(config.Name) ? config.Id : config.Name;

    /// <summary>
    /// Shortens the wait after a transient failure (network, throttling, 5xx)
    /// so recovery needs at most ~45 s; permanent causes (bad key, no plan)
    /// keep the configured interval because only the user can fix them.
    /// </summary>
    private void ApplyRetryInterval()
    {
        var active = Active;
        _refreshTimer.Interval = active is { InError: true, FailureKind: { } kind } && UsageFailureText.IsTransient(kind)
            ? TransientRetryInterval
            : ConfiguredInterval;
    }

    private TimeSpan ConfiguredInterval =>
        TimeSpan.FromMinutes(Math.Max(1, _settings.RefreshIntervalMinutes));

    /// <summary>Switches the card to another account (instant — all accounts are polled already).</summary>
    public void SwitchAccount(string accountId)
    {
        if (_settings.ActiveAccountId == accountId) return;
        if (_accounts.All(a => a.Config.Id != accountId)) return;

        _settings.ActiveAccountId = accountId;
        SettingsService.Save(_settings);
        ApplySnapshot();
    }

    public void OnSettingsChanged()
    {
        BuildAccounts();
        _refreshTimer.Interval = ConfiguredInterval;
        CardOpacity = _settings.CardOpacity;
        UpdateTexts();
        _ = RefreshAsync();
    }

    private IQuotaClient CreateClient(AccountConfig config) =>
        QuotaProviders.Create(config.Provider, config.BaseDomain, SettingsService.GetKey(config.Id) ?? string.Empty, SettingsService.DebugPath);

    private void ApplySnapshot()
    {
        var state = Active;
        var snap = state?.Last;
        double weeklyUsed = snap?.WeeklyUsedPct ?? 0;
        WeeklyProgress = (_settings.WeeklyDisplayStyle == DisplayStyle.Remaining ? 100 - weeklyUsed : weeklyUsed) / 100.0;

        double hourlyUsed = snap?.HourlyUsedPct ?? 0;
        HourlyProgress = (_settings.HourlyDisplayStyle == DisplayStyle.Remaining ? 100 - hourlyUsed : hourlyUsed) / 100.0;

        UpdateTexts();
        RebuildGraphs();
        CheckAlerts();
    }

    /// <summary>Rebuilds the detail-panel series from the active account's
    /// history, plus the shifted "previous period" overlay series.</summary>
    private void RebuildGraphs()
    {
        var end = DateTimeOffset.Now;
        var history = Active?.History;

        HourlyGraph = BuildSeries(history, end - HourlyGraphRange, end, s => s.HourlyPct);
        WeeklyGraph = BuildSeries(history, end - WeeklyGraphRange, end, s => s.WeeklyPct);
        PreviousHourlyGraph = BuildSeries(history, end - HourlyGraphRange - TimeSpan.FromHours(24), end - TimeSpan.FromHours(24), s => s.HourlyPct);
        PreviousWeeklyGraph = BuildSeries(history, end - WeeklyGraphRange - TimeSpan.FromDays(7), end - TimeSpan.FromDays(7), s => s.WeeklyPct);
    }

    private IReadOnlyList<GraphPoint> BuildSeries(UsageHistoryStore? history, DateTimeOffset start, DateTimeOffset end, Func<UsageSample, double> pick)
    {
        var points = new List<GraphPoint>();
        if (history is null) return points;

        double spanMinutes = (end - start).TotalMinutes;
        if (spanMinutes <= 0) return points;

        foreach (var s in history.Samples)
        {
            if (s.At < start || s.At > end) continue;
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
        int thr = Math.Clamp(_settings.NotifyThreshold, 1, 99);
        bool multi = _accounts.Count(s => s.Config.Configured) > 1;

        foreach (var state in _accounts)
        {
            var snap = state.Last;
            double h = snap?.HourlyUsedPct ?? 0;
            double w = snap?.WeeklyUsedPct ?? 0;
            var titlePrefix = multi ? $"{AccountLabel(state.Config)} · " : "";

            if (snap?.HasHourly == true && h >= thr && !state.HourlyAlerted)
            {
                state.HourlyAlerted = true;
                OnNotify(titlePrefix + Strings.Get("NotifyHourlyTitle"),
                    FunBody("NotifyHourlyBody", h, state.HourlyBurn, h));
            }
            if (h < thr - 5) state.HourlyAlerted = false;

            if (snap?.HasWeekly == true && w >= thr && !state.WeeklyAlerted)
            {
                state.WeeklyAlerted = true;
                OnNotify(titlePrefix + Strings.Get("NotifyWeeklyTitle"),
                    FunBody("NotifyWeeklyBody", w, state.WeeklyBurn, w));
            }
            if (w < thr - 5) state.WeeklyAlerted = false;
        }
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
        var state = Active;
        var snap = state?.Last;

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
        UpdateBurnTexts(state, snap);
        var level = string.IsNullOrWhiteSpace(snap?.PlanLevel) ? "—" : snap!.PlanLevel!;
        var label = ComputeAccountLabel();
        PlanLevelText = label.Length > 0 ? $"{label} · {level}" : level;
        ActiveAccountName = label;

        if (IsRefreshing)
            RefreshAgoText = Strings.Get("Refreshing");
        else if (state is { Config.Configured: true } && SettingsService.ApiKeyStorageState == ApiKeyStorageState.ProtectedUnavailable)
            RefreshAgoText = Strings.Get("CredentialUnavailableCard");
        else if (!_settings.IsValid)
            RefreshAgoText = Strings.Get("NotConfigured");
        else if (state?.InError == true)
            RefreshAgoText = UsageFailureText.Card(state.FailureKind ?? UsageFailureKind.Unknown);
        else if (snap is null)
            RefreshAgoText = Strings.Get("Refreshing");
        else
            RefreshAgoText = PastWords(snap.FetchedAt);

        // Hover detail: failures show the long-form explanation (same text the
        // settings dialog shows) plus the log path; success shows per-window
        // numbers with burn-rate projections.
        StatusTooltip = state?.InError == true
            ? UsageFailureText.Validation(state.FailureKind ?? UsageFailureKind.Unknown)
                + "\n" + Strings.Get("ErrLogAt", AppLog.LogPath)
            : snap is null ? null : BuildSuccessTooltip(snap);

        IsError = state?.InError == true;
    }

    private string ComputeAccountLabel()
    {
        // Only worth showing when there is something to distinguish.
        if (_settings.Accounts.Count <= 1) return "";
        var active = Active;
        return active is null ? "" : AccountLabel(active.Config);
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

    /// <summary>Burn-rate lines shared by the card tooltip and the detail panel.
    /// The 5-hour line carries a comparison against the 24h average when there
    /// is enough history; the weekly window has no meaningful daily baseline.</summary>
    private void UpdateBurnTexts(AccountState? state, UsageSnapshot? snap)
    {
        var avg = UsageHistoryStore.AverageBurnRateLast24h(state?.History.Samples ?? Array.Empty<UsageSample>(), DateTimeOffset.Now);
        HourlyBurnText = BurnLine(snap?.HasHourly == true, state?.HourlyBurn, snap?.HourlyUsedPct ?? 0, avg);
        WeeklyBurnText = BurnLine(snap?.HasWeekly == true, state?.WeeklyBurn, snap?.WeeklyUsedPct ?? 0, null);
    }

    private static string BurnLine(bool has, QuotaBurnTracker? burn, double usedPct, double? avgRate)
    {
        if (!has || burn is null ||
            burn.ProjectHoursToExhaustion(usedPct, DateTimeOffset.Now) is not { } hours ||
            burn.RatePctPerHour is not { } rate)
            return string.Empty;

        var line = Strings.Get("TipBurn", Math.Round(rate), FormatSpan(hours));

        // Context: is the momentary burn above or below the daily norm?
        // Tiny averages (<0.5 %/h) would produce absurd ratios — skip them.
        if (avgRate is > 0.5)
        {
            var key = rate > avgRate.Value * 1.15 ? "BurnFaster"
                    : rate < avgRate.Value * 0.85 ? "BurnSlower"
                    : "BurnTypical";
            int delta = Math.Max(1, (int)Math.Round(Math.Abs(rate - avgRate.Value) / avgRate.Value * 100));
            line += Strings.Get(key, delta);
        }
        return line;
    }

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
        if (d.TotalHours < 1) return Strings.Get("HoursLater", (int)Math.Round(d.TotalHours));
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

    // Card subtitle: empty for the single unnamed account, account name otherwise.
    public string ActiveAccountName { get => _activeAccountName; private set => Set(ref _activeAccountName, value); }
    private string _activeAccountName = "";

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

    // Detail-panel history series (refresh with every successful fetch) plus
    // the previous-period overlays (same range shifted 24h / 7d back).
    public IReadOnlyList<GraphPoint> HourlyGraph { get => _hourlyGraph; private set { _hourlyGraph = value; NotifyGraphChanged(nameof(HourlyGraph)); } }
    private IReadOnlyList<GraphPoint> _hourlyGraph = Array.Empty<GraphPoint>();
    public IReadOnlyList<GraphPoint> WeeklyGraph { get => _weeklyGraph; private set { _weeklyGraph = value; NotifyGraphChanged(nameof(WeeklyGraph)); } }
    private IReadOnlyList<GraphPoint> _weeklyGraph = Array.Empty<GraphPoint>();
    public IReadOnlyList<GraphPoint> PreviousHourlyGraph { get => _prevHourlyGraph; private set { _prevHourlyGraph = value; NotifyGraphChanged(nameof(PreviousHourlyGraph)); } }
    private IReadOnlyList<GraphPoint> _prevHourlyGraph = Array.Empty<GraphPoint>();
    public IReadOnlyList<GraphPoint> PreviousWeeklyGraph { get => _prevWeeklyGraph; private set { _prevWeeklyGraph = value; NotifyGraphChanged(nameof(PreviousWeeklyGraph)); } }
    private IReadOnlyList<GraphPoint> _prevWeeklyGraph = Array.Empty<GraphPoint>();
    public bool HasHourlyGraph => HourlyGraph.Count > 1;
    public bool HasWeeklyGraph => WeeklyGraph.Count > 1;
    public bool HasPrevHourly => PreviousHourlyGraph.Count > 1;
    public bool HasPrevWeekly => PreviousWeeklyGraph.Count > 1;

    private void NotifyGraphChanged(string name)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(
            name == nameof(HourlyGraph) ? nameof(HasHourlyGraph)
            : name == nameof(WeeklyGraph) ? nameof(HasWeeklyGraph)
            : name == nameof(PreviousHourlyGraph) ? nameof(HasPrevHourly)
            : nameof(HasPrevWeekly)));
    }
    public double CardOpacity { get => _cardOpacity; set => Set(ref _cardOpacity, value); }
    private double _cardOpacity = 1.0;

    /// <summary>Live preview from the settings dialog's opacity slider.</summary>
    public void SetCardOpacityPreview(double value) => CardOpacity = value;
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
        foreach (var state in _accounts)
            state.Client?.Dispose();
        _accounts.Clear();
    }
}
