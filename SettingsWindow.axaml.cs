using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
    private bool _initializingAccount = true;
    private bool _validated;
    private bool _saveAnyway;
    private bool _forgetKeyRequested;

    // Accounts whose key was typed anew in this dialog; each needs validation on save.
    private readonly List<AccountConfig> _pendingKeyAccounts = new();

    public SettingsWindow() : this(new AppSettings()) { }

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        _installedBuild = UpdateService.IsInstalledBuild();

        var version = typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "dev";
        Title = $"{Strings.Get("WinTitle")} · v{version}";

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

        RebuildAccountList(selectId: settings.ActiveAccountId);

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

    // ---- multi-account management -------------------------------------------

    private AccountConfig? SelectedAccount() =>
        AccountBox.SelectedItem as AccountConfig;

    private void RebuildAccountList(string? selectId)
    {
        _initializingAccount = true;
        var keep = selectId ?? SelectedAccount()?.Id;
        AccountBox.ItemsSource = null;
        AccountBox.ItemsSource = _settings.Accounts; // display text = AccountConfig.ToString()
        AccountBox.SelectedItem = _settings.Accounts.FirstOrDefault(a => a.Id == keep)
                                 ?? _settings.Accounts.FirstOrDefault();
        _initializingAccount = false;
        LoadSelectedAccountEditor();
    }

    private void LoadSelectedAccountEditor()
    {
        var account = SelectedAccount();
        if (account is null) return;

        AccountNameBox.Text = account.Name;
        PlatformBox.SelectedIndex =
            account.BaseDomain.Contains("z.ai", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        KeyBox.Text = SettingsService.GetKey(account.Id) ?? string.Empty;
        _forgetKeyRequested = false;
        ForgetKeyBtn.IsVisible = account.Configured;
        RemoveAccountBtn.IsEnabled = _settings.Accounts.Count > 1;
        SetActiveBtn.IsEnabled = account.Id != _settings.ActiveAccountId;
        RefreshCredentialStatus();
    }

    private void AccountBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initializingAccount) return;
        // Flush the previous editor content before switching? No: fields are
        // written back on save; switching just loads the other account.
        LoadSelectedAccountEditor();
        _validated = false;
        _saveAnyway = false;
        ValidateMsg.Text = "";
        SaveBtn.Content = Strings.Get("BtnSave");
    }

    private void SetActive_Click(object? sender, RoutedEventArgs e)
    {
        var account = SelectedAccount();
        if (account is null) return;
        _settings.ActiveAccountId = account.Id;
        RebuildAccountList(account.Id);
    }

    private void AddAccount_Click(object? sender, RoutedEventArgs e)
    {
        var account = new AccountConfig
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Name = "",
            BaseDomain = "https://open.bigmodel.cn",
        };
        _settings.Accounts.Add(account);
        RebuildAccountList(account.Id);
        _validated = false;
    }

    private void RemoveAccount_Click(object? sender, RoutedEventArgs e)
    {
        if (_settings.Accounts.Count <= 1) return;
        var account = SelectedAccount();
        if (account is null) return;

        SettingsService.SetKey(account.Id, null);
        _settings.Accounts.Remove(account);
        _pendingKeyAccounts.Remove(account);
        if (_settings.ActiveAccountId == account.Id)
            _settings.ActiveAccountId = _settings.Accounts[0].Id;
        RebuildAccountList(_settings.ActiveAccountId);
        _validated = false;
    }

    // ---- credential status ---------------------------------------------------

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
        var account = SelectedAccount();
        if (account is null) return;
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

        // Validate only accounts whose key was (re)typed in this dialog.
        if (!_validated && !_saveAnyway && _pendingKeyAccounts.Count > 0)
        {
            ValidateMsg.Text = "";
            SaveBtn.IsEnabled = false;
            SaveBtn.Content = Strings.Get("ValidateTesting");
            bool ok = true;
            try
            {
                foreach (var account in _pendingKeyAccounts)
                {
                    var key = SettingsService.GetKey(account.Id);
                    if (string.IsNullOrWhiteSpace(key)) continue;
                    using var probe = new GlmUsageClient(account.BaseDomain, key, SettingsService.DebugPath);
                    await probe.GetUsageAsync();
                }
                ok = true;
            }
            catch (UsageRequestException ex)
            {
                _saveAnyway = UsageFailureText.AllowsSaveAnyway(ex.Kind);
                ValidateMsg.Text = UsageFailureText.Validation(ex.Kind);
                ok = false;
            }
            catch
            {
                _saveAnyway = true;
                ValidateMsg.Text = UsageFailureText.Validation(UsageFailureKind.Unknown);
                ok = false;
            }
            finally
            {
                SaveBtn.IsEnabled = true;
                SaveBtn.Content = Strings.Get(ok
                    ? "BtnSave"
                    : _saveAnyway ? "ValidateSaveAnyway" : "BtnSave");
            }

            if (!ok)
                return;

            _validated = true;
        }

        PersistAndClose();
    }

    private void CollectForm()
    {
        if (SelectedAccount() is { } account)
        {
            account.Name = AccountNameBox.Text?.Trim() ?? "";
            account.BaseDomain = PlatformBox.SelectedIndex == 1 ? "https://api.z.ai" : "https://open.bigmodel.cn";

            var typedKey = KeyBox.Text?.Trim();
            if (_forgetKeyRequested)
            {
                SettingsService.SetKey(account.Id, null);
                account.Configured = false;
                _pendingKeyAccounts.RemoveAll(a => a.Id == account.Id);
            }
            else if (!string.IsNullOrWhiteSpace(typedKey) && typedKey != SettingsService.GetKey(account.Id))
            {
                SettingsService.SetKey(account.Id, typedKey);
                account.Configured = true;
                if (!_pendingKeyAccounts.Any(a => a.Id == account.Id))
                    _pendingKeyAccounts.Add(account);
            }
        }

        if (_settings.ActiveAccountId is null || _settings.Accounts.All(a => a.Id != _settings.ActiveAccountId))
            _settings.ActiveAccountId = _settings.Accounts.FirstOrDefault()?.Id;

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
