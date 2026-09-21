using System;
using System.Diagnostics;
using System.Net.Http;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using BrainFuel.Services;

namespace BrainFuel;

public partial class SettingsWindow : Window
{
    // API-key console per platform entry (index matches the PlatformBox combo).
    private static readonly string[] ConsoleUrls =
    {
        "https://open.bigmodel.cn/usercenter/apikeys", // Zhipu (China)
        "https://z.ai/manage-apikey/apikey-list",      // Z.ai (intl)
    };

    private readonly AppSettings _settings;
    private readonly bool _installedBuild;
    private bool _validated;    // key confirmed working in this dialog
    private bool _saveAnyway;   // validation failed, user chose to save regardless

    public SettingsWindow() : this(new AppSettings()) { }

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        _installedBuild = UpdateService.IsInstalledBuild();
        Title = Strings.Get("WinTitle");

        KeyBox.Text = settings.ApiKey;
        PlatformBox.SelectedIndex =
            settings.BaseDomain.Contains("z.ai", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        IntervalBox.Value = Math.Max(1, settings.RefreshIntervalMinutes);
        WeeklyRemaining.IsChecked = settings.WeeklyDisplayStyle == DisplayStyle.Remaining;
        HourlyRemaining.IsChecked = settings.HourlyDisplayStyle == DisplayStyle.Remaining;
        AutoStartBox.IsChecked = AutoStartService.IsEnabled();
        NotifyBox.IsChecked = settings.NotifyEnabled;
        ThresholdBox.Value = Math.Clamp(settings.NotifyThreshold, 10, 99);
        ThemeBox.SelectedIndex = (int)settings.ThemeMode;
        OpacitySlider.Value = settings.CardOpacity;
        LangBox.SelectedIndex = (int)settings.Language;

        var version = typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "dev";
        var buildKind = Strings.Get(_installedBuild ? "UpdateInstalledBuild" : "UpdatePortableBuild");
        CurrentVersionText.Text = $"{string.Format(Strings.Get("UpdateCurrentVersion"), version)} · {buildKind}";
        OpenReleasesBtn.IsVisible = !_installedBuild;
        if (!_installedBuild)
            UpdateStatusText.Text = Strings.Get("UpdatePortableHint");

        // First run (or key cleared): show the quick-start note and grow the
        // window so the whole form, including Save, stays visible without scrolling.
        FirstRunPanel.IsVisible = !settings.IsValid;
        if (!settings.IsValid) Height = 680;

        // Re-validate whenever the key or platform changes after a failed check.
        KeyBox.TextChanged += ResetValidation;
        PlatformBox.SelectionChanged += ResetValidation;
    }

    private void ConsoleLink_Click(object? sender, RoutedEventArgs e)
    {
        var url = ConsoleUrls[Math.Clamp(PlatformBox.SelectedIndex, 0, ConsoleUrls.Length - 1)];
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private void OpenReleases_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(UpdateService.ReleasesUrl) { UseShellExecute = true });
        }
        catch
        {
            UpdateStatusText.Text = Strings.Get("UpdateFailed");
        }
    }

    private async void CheckUpdate_Click(object? sender, RoutedEventArgs e)
    {
        if (!_installedBuild)
        {
            UpdateStatusText.Text = Strings.Get("UpdatePortableHint");
            OpenReleasesBtn.IsVisible = true;
            return;
        }

        CheckUpdateBtn.IsEnabled = false;
        UpdateStatusText.Text = Strings.Get("UpdateChecking");

        try
        {
            var result = await UpdateService.CheckDownloadAndRestartAsync(stage =>
                Dispatcher.UIThread.Post(() => UpdateStatusText.Text = stage switch
                {
                    ManualUpdateStage.Checking => Strings.Get("UpdateChecking"),
                    ManualUpdateStage.Downloading => Strings.Get("UpdateDownloading"),
                    ManualUpdateStage.Restarting => Strings.Get("UpdateRestarting"),
                    _ => Strings.Get("UpdateChecking"),
                }));

            UpdateStatusText.Text = result switch
            {
                ManualUpdateResult.UpToDate => Strings.Get("UpdateUpToDate"),
                ManualUpdateResult.NotInstalled => Strings.Get("UpdateInstalledOnly"),
                ManualUpdateResult.Restarting => Strings.Get("UpdateRestarting"),
                _ => Strings.Get("UpdateFailed"),
            };
        }
        finally
        {
            CheckUpdateBtn.IsEnabled = true;
        }
    }

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        CollectForm();
        var key = _settings.ApiKey;

        // Validate the key on save so a typo or wrong platform is caught here,
        // not as an anonymous "refresh failed" on the card. One more click on
        // the relabeled button saves anyway (offline / restricted networks).
        if (!string.IsNullOrWhiteSpace(key) && !_validated && !_saveAnyway)
        {
            ValidateMsg.Text = "";
            SaveBtn.IsEnabled = false;
            SaveBtn.Content = Strings.Get("ValidateTesting");
            bool ok = false;
            try
            {
                using var probe = new GlmUsageClient(_settings.BaseDomain, key, SettingsService.DebugPath);
                await probe.GetUsageAsync();
                ok = true;
            }
            catch (HttpRequestException ex) when (ex.StatusCode is not null)
            {
                _saveAnyway = true;
                ValidateMsg.Text = string.Format(Strings.Get("ValidateBadKey"), (int)ex.StatusCode);
                ok = false;
            }
            catch
            {
                _saveAnyway = true;
                ValidateMsg.Text = Strings.Get("ValidateNetwork");
                ok = false;
            }
            finally
            {
                SaveBtn.IsEnabled = true;
                SaveBtn.Content = Strings.Get(ok ? "BtnSave" : "ValidateSaveAnyway");
            }
            if (!ok) return;
            _validated = true;
        }

        PersistAndClose();
    }

    private void CollectForm()
    {
        _settings.ApiKey = KeyBox.Text?.Trim();
        _settings.BaseDomain = PlatformBox.SelectedIndex == 1 ? "https://api.z.ai" : "https://open.bigmodel.cn";
        _settings.RefreshIntervalMinutes = (int)(IntervalBox.Value ?? 5);
        _settings.WeeklyDisplayStyle = (WeeklyRemaining.IsChecked ?? false) ? DisplayStyle.Remaining : DisplayStyle.Used;
        _settings.HourlyDisplayStyle = (HourlyRemaining.IsChecked ?? false) ? DisplayStyle.Remaining : DisplayStyle.Used;
        _settings.AutoStart = AutoStartBox.IsChecked ?? false;
        _settings.NotifyEnabled = NotifyBox.IsChecked ?? true;
        _settings.NotifyThreshold = (int)(ThresholdBox.Value ?? 80);
        _settings.ThemeMode = (AppTheme)ThemeBox.SelectedIndex;
        _settings.CardOpacity = OpacitySlider.Value;
        _settings.Language = (AppLanguage)LangBox.SelectedIndex;
    }

    private void PersistAndClose()
    {
        SettingsService.Save(_settings);
        App.ApplyTheme(_settings.ThemeMode);
        Strings.ApplyLanguage(_settings.Language);
        try { AutoStartService.SetEnabled(_settings.AutoStart); } catch { }
        Close();
    }

    private void ResetValidation(object? sender, EventArgs e)
    {
        _validated = false;
        _saveAnyway = false;
        ValidateMsg.Text = "";
        SaveBtn.Content = Strings.Get("BtnSave");
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
