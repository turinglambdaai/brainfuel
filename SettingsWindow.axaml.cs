using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input.Platform;
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
    private double _originalOpacity;
    private string _originalPalette = RingPalette.DefaultId;
    private string _previewPalette = RingPalette.DefaultId;
    private bool _initializingAppearance = true;

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
        HotkeyEnabledBox.IsChecked = settings.HotkeyEnabled;
        HotkeyBox.Text = settings.HotkeyCombo;
        AutoStartBox.IsChecked = AutoStartService.IsEnabled();
        NotifyBox.IsChecked = settings.NotifyEnabled;
        ThresholdBox.Value = Math.Clamp(settings.NotifyThreshold, 10, 99);
        ThemeBox.SelectedIndex = (int)settings.ThemeMode;
        OpacitySlider.Value = settings.CardOpacity;
        LangBox.SelectedIndex = (int)settings.Language;

        _initializingInterval = false;
        RefreshIntervalStatus();
        RefreshCredentialStatus();

        // Live-preview anchors: revert on Cancel, persist on Save.
        _originalOpacity = settings.CardOpacity;
        _originalPalette = string.IsNullOrWhiteSpace(settings.RingPalette)
            ? RingPalette.DefaultId
            : settings.RingPalette;
        _previewPalette = _originalPalette;
        BuildPaletteRow();
        OpacitySlider.ValueChanged += OpacitySlider_ValueChanged;

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
        PlatformBox.SelectedIndex = account.Provider switch
        {
            "codex" => 2,
            "claude" => 3,
            _ => account.BaseDomain.Contains("z.ai", StringComparison.OrdinalIgnoreCase) ? 1 : 0,
        };
        ApplyProviderVisibility(account.Provider);
        KeyBox.Text = account.Provider is "codex" or "claude"
            ? string.Empty
            : SettingsService.GetKey(account.Id) ?? string.Empty;
        _forgetKeyRequested = false;
        ForgetKeyBtn.IsVisible = account.Configured && account.Provider is not ("codex" or "claude");
        RemoveAccountBtn.IsEnabled = _settings.Accounts.Count > 1;
        SetActiveBtn.IsEnabled = account.Id != _settings.ActiveAccountId;
        RefreshCredentialStatus();
    }

    /// <summary>Codex/Claude accounts need no pasted key — show the local-login note.</summary>
    private void ApplyProviderVisibility(string provider)
    {
        bool cliLogin = provider is "codex" or "claude";
        KeySection.IsVisible = !cliLogin;
        CliLoginHint.IsVisible = cliLogin;
    }

    private static string ProviderForIndex(int index) => index switch
    {
        2 => "codex",
        3 => "claude",
        _ => QuotaProviders.DefaultProvider,
    };

    private void PlatformBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initializingAccount) return;
        var provider = ProviderForIndex(PlatformBox.SelectedIndex);
        ApplyProviderVisibility(provider);
        ResetValidation(sender, e);
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

    // ---- live preview: opacity + ring palette -------------------------------

    /// <summary>One round swatch per curated palette; click previews instantly.</summary>
    private void BuildPaletteRow()
    {
        PaletteRow.Children.Clear();
        foreach (var palette in RingPalette.All)
        {
            var id = palette.Id;
            var border = new Avalonia.Controls.Border
            {
                Width = 30,
                Height = 30,
                CornerRadius = new Avalonia.CornerRadius(15),
                BorderThickness = new Avalonia.Thickness(id == _previewPalette ? 2.5 : 1),
                BorderBrush = id == _previewPalette
                    ? new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#D97757"))
                    : new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#66807872")),
                Background = BuildSwatchBrush(palette),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            };
            border.Tag = Strings.Current == AppLanguage.Zh ? palette.NameZh : palette.NameEn;
            border.PointerReleased += (_, _) =>
            {
                _previewPalette = id;
                RingPalette.Apply(id);
                App.MainView?.ApplySeverityColorsPublic();
                BuildPaletteRow();
            };
            PaletteRow.Children.Add(border);
        }
    }

    private static Avalonia.Media.LinearGradientBrush BuildSwatchBrush(RingPalette.Palette palette)
    {
        var brush = new Avalonia.Media.LinearGradientBrush
        {
            StartPoint = new Avalonia.RelativePoint(0, 0.5, Avalonia.RelativeUnit.Relative),
            EndPoint = new Avalonia.RelativePoint(1, 0.5, Avalonia.RelativeUnit.Relative),
        };
        brush.GradientStops.Add(new Avalonia.Media.GradientStop(Avalonia.Media.Color.Parse(palette.Weekly), 0));
        brush.GradientStops.Add(new Avalonia.Media.GradientStop(Avalonia.Media.Color.Parse(palette.Hourly), 1));
        return brush;
    }

    private void OpacitySlider_ValueChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_initializingAppearance) return;
        // Live preview on the real card; Cancel/Save settles the value.
        App.ViewModel?.SetCardOpacityPreview(e.NewValue);
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

    private async void CopyDiagnostics_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var bundle = DiagnosticsBuilder.Build(_settings);
            await Clipboard!.SetTextAsync(bundle);
            DiagnosticsStatusText.Text = Strings.Get("DiagnosticsCopied");
        }
        catch
        {
            DiagnosticsStatusText.Text = Strings.Get("DiagnosticsCopyFailed");
        }
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
                    using var probe = QuotaProviders.Create(account.Provider, account.BaseDomain, key, SettingsService.DebugPath);
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
            var provider = ProviderForIndex(PlatformBox.SelectedIndex);
            account.Provider = provider;
            account.BaseDomain = provider switch
            {
                "codex" => "https://chatgpt.com",
                "claude" => "https://api.anthropic.com",
                _ => PlatformBox.SelectedIndex == 1 ? "https://api.z.ai" : "https://open.bigmodel.cn",
            };

            if (provider is "codex" or "claude")
            {
                // CLI-login providers: configured = local login present.
                // Skip keyring entirely (no pasted key by design).
                account.Configured = LocalCliCredentials.Exists(provider);
                _pendingKeyAccounts.RemoveAll(a => a.Id == account.Id);
                if (!account.Configured)
                    ValidateMsg.Text = string.Format(Strings.Get("CliLoginMissing"), provider is "codex" ? "Codex CLI" : "Claude Code");
            }
            else
            {
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
        }

        if (_settings.ActiveAccountId is null || _settings.Accounts.All(a => a.Id != _settings.ActiveAccountId))
            _settings.ActiveAccountId = _settings.Accounts.FirstOrDefault()?.Id;

        _settings.HotkeyEnabled = HotkeyEnabledBox.IsChecked ?? false;
        if (_settings.HotkeyEnabled && !HotkeyParse.TryParse(HotkeyBox.Text, out _, out _))
        {
            // Invalid combo: keep the previous one and say so instead of
            // silently registering nothing.
            HotkeyBox.Text = _settings.HotkeyCombo;
            ValidateMsg.Text = Strings.Get("HotkeyInvalid");
        }
        else if (!string.IsNullOrWhiteSpace(HotkeyBox.Text))
        {
            _settings.HotkeyCombo = HotkeyBox.Text.Trim();
        }

        _settings.RefreshIntervalMinutes = Math.Clamp((int)(IntervalBox.Value ?? 5), 1, 60);
        _settings.WeeklyDisplayStyle = (WeeklyRemaining.IsChecked ?? false) ? DisplayStyle.Remaining : DisplayStyle.Used;
        _settings.HourlyDisplayStyle = (HourlyRemaining.IsChecked ?? false) ? DisplayStyle.Remaining : DisplayStyle.Used;
        _settings.AlwaysOnTop = AlwaysOnTopBox.IsChecked ?? false;
        _settings.AutoStart = AutoStartBox.IsChecked ?? false;
        _settings.NotifyEnabled = NotifyBox.IsChecked ?? true;
        _settings.NotifyThreshold = (int)(ThresholdBox.Value ?? 80);
        _settings.ThemeMode = (AppTheme)ThemeBox.SelectedIndex;
        _settings.CardOpacity = OpacitySlider.Value;
        _settings.RingPalette = _previewPalette;
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

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        // Undo live previews so the card returns to the state the user saved.
        App.ViewModel?.SetCardOpacityPreview(_originalOpacity);
        RingPalette.Apply(_originalPalette);
        App.MainView?.ApplySeverityColorsPublic();
        Close();
    }
}
