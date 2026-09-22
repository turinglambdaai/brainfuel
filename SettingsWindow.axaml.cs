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
    private static readonly string[] ConsoleUrls =
    {
        "https://open.bigmodel.cn/usercenter/apikeys",
        "https://z.ai/manage-apikey/apikey-list",
    };

    private readonly AppSettings _settings;
    private readonly bool _installedBuild;
    private int _savedRefreshInterval;
    private bool _initializingInterval = true;
    private bool _validated;
    private bool _saveAnyway;
    private bool _forgetKeyRequested;

    public SettingsWindow() : this(new AppSettings()) { }

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        _installedBuild = UpdateService.IsInstalledBuild();

        var version = typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "dev";
        Title = $"{Strings.Get("WinTitle")} · v{version}";
        HeaderVersionText.Text = $"v{version}";

        KeyBox.Text = settings.ApiKey;
        PlatformBox.SelectedIndex =
            settings.BaseDomain.Contains("z.ai", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        _savedRefreshInterval = Math.Clamp(settings.RefreshIntervalMinutes, 1, 60);
        IntervalBox.Value = _savedRefreshInterval;
        WeeklyRemaining.IsChecked = settings.WeeklyDisplayStyle == DisplayStyle.Remaining;
        HourlyRemaining.IsChecked = settings.HourlyDisplayStyle == DisplayStyle.Remaining;
        AlwaysOnTopBox.IsChecked = settings.AlwaysOnTop;
        AutoStartBox.IsChecked = AutoStartService.IsEnabled();
        NotifyBox.IsChecked = settings.NotifyEnabled;
        ThresholdBox.Value = Math.Clamp(settings.NotifyThreshold, 10, 99);
        ThemeBox.SelectedIndex = (int)settings.ThemeMode;
        OpacitySlider.Value = settings.CardOpacity;
        LangBox.SelectedIndex = (int)settings.Language;

        _initializingInterval = false;
        RefreshIntervalStatus();
        RefreshCredentialStatus();
        ForgetKeyBtn.IsVisible = settings.HasConfiguredCredential;

        var buildKind = Strings.Get(_installedBuild ? "UpdateInstalledBuild" : "UpdatePortableBuild");
        CurrentVersionText.Text = $"{string.Format(Strings.Get("UpdateCurrentVersion"), version)} · {buildKind}";
        OpenReleasesBtn.IsVisible = true;
        if (!_installedBuild)
            UpdateStatusText.Text = Strings.Get("UpdatePortableHint");

        FirstRunPanel.IsVisible = !settings.HasConfiguredCredential;
        SettingsTabs.SelectedIndex = settings.HasConfiguredCredential ? 0 : 1;

        _validated = settings.IsValid;
        KeyBox.TextChanged += ResetValidation;
        PlatformBox.SelectionChanged += ResetValidation;
    }

    private void RefreshCredentialStatus()
    {
        CredentialStatusText.Text = SettingsService.ApiKeyStorageState switch
        {
            ApiKeyStorageState.Protected => string.Format(
                Strings.Get("CredentialProtected"),
                SettingsService.ApiKeyStorageName),
            ApiKeyStorageState.ProtectedUnavailable => string.Format(
                Strings.Get("CredentialUnavailable"),
                SettingsService.ApiKeyStorageName),
            ApiKeyStorageState.PlaintextFallback => Strings.Get("CredentialFallback"),
            _ => string.Format(
                Strings.Get("CredentialEmpty"),
                CredentialStore.BackendDisplayName),
        };
    }

    private void ForgetKey_Click(object? sender, RoutedEventArgs e)
    {
        _forgetKeyRequested = true;
        KeyBox.Text = string.Empty;
        ForgetKeyBtn.IsVisible = false;
        CredentialStatusText.Text = Strings.Get("CredentialPendingClear");
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

    private void Interval_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (!_initializingInterval)
            RefreshIntervalStatus();
    }

    private void IntervalPreset_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || !int.TryParse(button.Content?.ToString(), out var minutes))
            return;

        IntervalBox.Value = Math.Clamp(minutes, 1, 60);
        RefreshIntervalStatus();
    }

    private void RefreshIntervalStatus()
    {
        var minutes = Math.Clamp((int)(IntervalBox.Value ?? _savedRefreshInterval), 1, 60);
        var key = minutes == _savedRefreshInterval ? "IntervalActiveStatus" : "IntervalPendingStatus";
        IntervalStatusText.Text = string.Format(Strings.Get(key), minutes);
    }

    private async void CheckUpdate_Click(object? sender, RoutedEventArgs e)
    {
        if (!_installedBuild)
        {
            UpdateStatusText.Text = Strings.Get("UpdatePortableHint");
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
            }
            catch
            {
                _saveAnyway = true;
                ValidateMsg.Text = Strings.Get("ValidateNetwork");
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
        if (_forgetKeyRequested && string.IsNullOrWhiteSpace(_settings.ApiKey))
            _settings.ApiKeyConfigured = false;

        _settings.BaseDomain = PlatformBox.SelectedIndex == 1 ? "https://api.z.ai" : "https://open.bigmodel.cn";
        _settings.RefreshIntervalMinutes = Math.Clamp((int)(IntervalBox.Value ?? 5), 1, 60);
        _settings.WeeklyDisplayStyle = (WeeklyRemaining.IsChecked ?? false) ? DisplayStyle.Remaining : DisplayStyle.Used;
        _settings.HourlyDisplayStyle = (HourlyRemaining.IsChecked ?? false) ? DisplayStyle.Remaining : DisplayStyle.Used;
        _settings.AlwaysOnTop = AlwaysOnTopBox.IsChecked ?? false;
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
        _savedRefreshInterval = _settings.RefreshIntervalMinutes;
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
