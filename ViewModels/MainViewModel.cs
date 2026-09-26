using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Threading;
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

    public Action<string, string>? OnNotify { get; set; }

    public MainViewModel(AppSettings settings)
    {
        _settings = settings;
        _client = CreateClient();
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
        CheckAlerts();
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
            OnNotify(Strings.Get("NotifyHourlyTitle"), Strings.Get("NotifyUsed", Math.Round(h)));
        }
        if (h < thr - 5) _hourlyAlerted = false;

        if (snap?.HasWeekly == true && w >= thr && !_weeklyAlerted)
        {
            _weeklyAlerted = true;
            OnNotify(Strings.Get("NotifyWeeklyTitle"), Strings.Get("NotifyUsed", Math.Round(w)));
        }
        if (w < thr - 5) _weeklyAlerted = false;
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

        // Hover detail: the long-form explanation (same text the settings
        // dialog shows on validation) plus where the rolling log lives.
        StatusTooltip = _inError
            ? UsageFailureText.Validation(_failureKind ?? UsageFailureKind.Unknown)
                + "\n" + Strings.Get("ErrLogAt", AppLog.LogPath)
            : null;

        IsError = _inError;
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
    public string RefreshAgoText { get => _refreshAgoText; set => Set(ref _refreshAgoText, value); }
    private string _refreshAgoText = "刷新中…";
    public string? StatusTooltip { get => _statusTooltip; set => Set(ref _statusTooltip, value); }
    private string? _statusTooltip;
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
