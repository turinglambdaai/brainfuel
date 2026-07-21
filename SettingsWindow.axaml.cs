using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using BrainFuel.Services;

namespace BrainFuel;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public SettingsWindow() : this(new AppSettings()) { }

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        KeyBox.Text = settings.ApiKey;
        PlatformBox.SelectedIndex =
            settings.BaseDomain.Contains("z.ai", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        IntervalBox.Value = Math.Max(1, settings.RefreshIntervalMinutes);
        WeeklyRemaining.IsChecked = settings.WeeklyDisplayStyle == DisplayStyle.Remaining;
        HourlyRemaining.IsChecked = settings.HourlyDisplayStyle == DisplayStyle.Remaining;
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        _settings.ApiKey = KeyBox.Text?.Trim();
        _settings.BaseDomain = PlatformBox.SelectedIndex == 1 ? "https://api.z.ai" : "https://open.bigmodel.cn";
        _settings.RefreshIntervalMinutes = (int)(IntervalBox.Value ?? 5);
        _settings.WeeklyDisplayStyle = (WeeklyRemaining.IsChecked ?? false) ? DisplayStyle.Remaining : DisplayStyle.Used;
        _settings.HourlyDisplayStyle = (HourlyRemaining.IsChecked ?? false) ? DisplayStyle.Remaining : DisplayStyle.Used;
        SettingsService.Save(_settings);
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
