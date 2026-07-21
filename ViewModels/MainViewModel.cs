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
    private GlmUsageClient? _client;
    private UsageSnapshot? _last;
    private bool _inError;
    private bool _hourlyAlerted;
    private bool _weeklyAlerted;

    /// <summary>UI hook to surface a desktop notification (title, message).</summary>
    public Action<string, string>? OnNotify { get; set; }

    public MainViewModel(AppSettings settings)
    {
        _settings = settings;
        _client = CreateClient();
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
        try
        {
            if (_client is null) _client = CreateClient();
            var snap = await _client.GetUsageAsync();
            _last = snap;
            _inError = false;
        }
        catch
        {
            _inError = true;
        }
        ApplySnapshot();
    }

    public void OnSettingsChanged()
    {
        _client?.Dispose();
        _client = CreateClient();
        _refreshTimer.Interval = TimeSpan.FromMinutes(Math.Max(1, _settings.RefreshIntervalMinutes));
        _ = RefreshAsync();
    }

    private GlmUsageClient CreateClient() =>
        new(_settings.BaseDomain, _settings.ApiKey ?? string.Empty, SettingsService.DebugPath);

    private void ApplySnapshot()
    {
        var snap = _last;
        // Ring follows the chosen display style so the ring and the number always agree.
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
            OnNotify("5 小时额度即将耗尽", $"已用 {Math.Round(h):0}%");
        }
        if (h < thr - 5) _hourlyAlerted = false;

        if (snap?.HasWeekly == true && w >= thr && !_weeklyAlerted)
        {
            _weeklyAlerted = true;
            OnNotify("周额度即将耗尽", $"已用 {Math.Round(w):0}%");
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

        WeeklySubText = snap?.WeeklyResetAt is { } wr ? FutureWords(wr) : "—";
        HourlySubText = snap?.HourlyResetAt is { } hr ? FutureWords(hr) : "—";

        if (_inError)
            RefreshAgoText = snap is null ? "刷新失败" : $"刷新失败 · 上次 {snap.FetchedAt.LocalDateTime:HH:mm}";
        else if (snap is null)
            RefreshAgoText = "刷新中…";
        else
            RefreshAgoText = PastWords(snap.FetchedAt);

        IsError = _inError;
    }

    private static string FormatPct(double value, bool? has)
        => has == false ? "--" : $"{Math.Round(value):0}%";

    private static string PastWords(DateTimeOffset t)
    {
        var d = DateTimeOffset.Now - t;
        if (d.TotalMinutes < 1) return "刚刚";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} 分钟前";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours} 小时前";
        return $"{(int)d.TotalDays} 天前";
    }

    private static string FutureWords(DateTimeOffset t)
    {
        var d = t - DateTimeOffset.Now;
        if (d.TotalMinutes <= 0) return "即将重置";
        if (d.TotalHours < 1) return $"{(int)Math.Ceiling(d.TotalMinutes)} 分钟后";
        if (d.TotalDays < 1) return $"{(int)Math.Round(d.TotalHours)} 小时后";
        return $"{(int)Math.Round(d.TotalDays)} 天后";
    }

    // ---- bindable properties ----
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
    public bool IsError { get => _isError; set => Set(ref _isError, value); }
    private bool _isError;

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
