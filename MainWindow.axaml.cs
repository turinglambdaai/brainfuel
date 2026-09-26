using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BrainFuel.Services;
using BrainFuel.ViewModels;

namespace BrainFuel;

public partial class MainWindow : Window
{
    // Quota-urgency accents, applied over the theme palette in code-behind.
    private static readonly ImmutableSolidColorBrush AmberBrush = new(Color.Parse("#F5A623"));
    private static readonly ImmutableSolidColorBrush RedBrush = new(Color.Parse("#E5484D"));

    private AppSettings? _settings;
    private MainViewModel? _vm;
    private bool _userMoveInProgress;
    private bool _quitRequested;
    private Flyout? _detailFlyout;

    private Flyout DetailFlyout => _detailFlyout ??= (Flyout)Resources["DetailFlyout"];

    public MainWindow()
    {
        InitializeComponent();
    }

    public void Initialize(AppSettings settings, MainViewModel vm)
    {
        _settings = settings;
        _vm = vm;
        DataContext = vm;

        Topmost = settings.AlwaysOnTop;
        Position = WindowPlacementService.Restore(this, settings);
        WindowPlacementService.Capture(this, settings);

        RefreshMenuState();
        ApplySizeMode();
        ApplySeverityColors();

        Screens.Changed += OnScreensChanged;
        ScalingChanged += OnScalingChanged;
        vm.PropertyChanged += OnVmPropertyChanged;

        vm.OnNotify = (title, msg) => Dispatcher.UIThread.Post(() =>
            new NotificationWindow().ShowNotification(title, msg));

        vm.Start();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.HourlySeverity)
            or nameof(MainViewModel.WeeklySeverity) or nameof(MainViewModel.MiniSeverity))
        {
            ApplySeverityColors();
        }
    }

    private void ApplySeverityColors()
    {
        if (_vm is null) return;
        HourlyDot.Fill = SeverityBrush(_vm.HourlySeverity, "RingHourly");
        HourlyPctText.Foreground = SeverityBrush(_vm.HourlySeverity, "TextPrimary");
        WeeklyDot.Fill = SeverityBrush(_vm.WeeklySeverity, "RingWeekly");
        WeeklyPctText.Foreground = SeverityBrush(_vm.WeeklySeverity, "TextPrimary");
        StandardRing.HourlyBrush = SeverityBrush(_vm.HourlySeverity, "RingHourly");
        StandardRing.WeeklyBrush = SeverityBrush(_vm.WeeklySeverity, "RingWeekly");
        var miniBrush = SeverityBrush(_vm.MiniSeverity, "RingWeekly");
        MiniRing.WeeklyBrush = miniBrush;
        MiniPctText.Foreground = _vm.MiniSeverity == SeverityLevel.Calm
            ? SeverityBrush(SeverityLevel.Calm, "TextPrimary")
            : miniBrush;
        DetailHourlyPct.Foreground = SeverityBrush(_vm.HourlySeverity, "TextPrimary");
        DetailWeeklyPct.Foreground = SeverityBrush(_vm.WeeklySeverity, "TextPrimary");
    }

    private IBrush SeverityBrush(SeverityLevel level, string calmResourceKey) => level switch
    {
        SeverityLevel.Red => RedBrush,
        SeverityLevel.Amber => AmberBrush,
        _ => this.TryGetResource(calmResourceKey, ActualThemeVariant, out var value) && value is IBrush brush
            ? brush
            : AmberBrush, // unreachable in practice; keeps the switch exhaustive
    };

    private void OnScalingChanged(object? sender, EventArgs e)
    {
        if (_settings is null) return;
        Dispatcher.UIThread.Post(() => WindowPlacementService.RepairAfterScreenChange(this, _settings));
    }

    private async void OnScreensChanged(object? sender, EventArgs e)
    {
        foreach (var delay in new[] { 100, 1000, 3000 })
        {
            await System.Threading.Tasks.Task.Delay(delay);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_settings is null) return;
                WindowPlacementService.RepairAfterScreenChange(this, _settings);
                RefreshDisplayMenuState();
            });
        }
    }

    private void Card_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        // PointerPressed bubbles from child controls. Never start a window drag
        // when the user is actually pressing one of the card's buttons.
        if (e.Source is Visual source &&
            source.GetSelfAndVisualAncestors().Any(visual => visual is Button))
            return;

        // Double-click opens the detail panel — the frequent "go deeper" action.
        if (e.ClickCount >= 2)
        {
            ToggleDetailPanel();
            return;
        }

        // A click while the panel is open just dismisses it (light-dismiss
        // already closed it by now); don't start a drag underneath.
        if (DetailFlyout.IsOpen)
            return;

        _userMoveInProgress = true;
        BeginMoveDrag(e);
    }

    private void Card_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_userMoveInProgress || _settings is null)
            return;

        _userMoveInProgress = false;
        WindowPlacementService.Capture(this, _settings);
        SettingsService.Save(_settings);
    }

    private async void Refresh_Click(object? sender, RoutedEventArgs e)
    {
        // A known protected credential may only be temporarily unavailable. Let
        // the view-model retry the system credential store instead of treating
        // this as first-run configuration.
        if (_settings is not null && !_settings.HasConfiguredCredential)
        {
            OpenSettings();
            return;
        }

        await (_vm?.RefreshAsync() ?? System.Threading.Tasks.Task.CompletedTask);
    }

    private void OpenMenu_Click(object? sender, RoutedEventArgs e)
    {
        RefreshMenuState();

        // The application menu belongs only to the explicit three-dot button
        // (standard card or mini card). The card itself deliberately has no
        // ContextMenu, so there is only one discoverable app menu.
        if (sender is Control target)
        {
            CardMenu.PlacementTarget = target;
            CardMenu.Placement = PlacementMode.BottomEdgeAlignedRight;
            CardMenu.Open(target);
        }
        e.Handled = true;
    }

    /// <summary>Toggles the L1 detail panel (double-click / ⋯ menu).</summary>
    public void ToggleDetailPanel()
    {
        if (DetailFlyout.IsOpen)
        {
            DetailFlyout.Hide();
            return;
        }
        DetailFlyout.ShowAt(CardBorder);
    }

    private void Detail_Click(object? sender, RoutedEventArgs e) => ToggleDetailPanel();

    /// <summary>Swaps the card between the full layout and the mini ring.</summary>
    public void ApplySizeMode()
    {
        bool mini = _settings?.SizeMode == CardSizeMode.Compact;
        StandardLayout.IsVisible = !mini;
        MiniLayout.IsVisible = mini;
        Width = mini ? 118 : 368;
        Height = mini ? 118 : 226;
        MiniMenuItem.IsChecked = mini;
    }

    private void ToggleSizeMode()
    {
        if (_settings is null) return;
        _settings.SizeMode = _settings.SizeMode == CardSizeMode.Compact
            ? CardSizeMode.Standard
            : CardSizeMode.Compact;
        ApplySizeMode();
        SettingsService.Save(_settings);
    }

    private void ToggleMini_Click(object? sender, RoutedEventArgs e) => ToggleSizeMode();

    private void Settings_Click(object? sender, RoutedEventArgs e) => OpenSettings();
    private void About_Click(object? sender, RoutedEventArgs e) => OpenAbout();

    private void RefreshMenuState()
    {
        RefreshAboutMenuText();
        RefreshTopmostMenuText();
        RefreshDisplayMenuState();
    }

    private void RefreshAboutMenuText()
    {
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "dev";
        var label = Strings.Current == AppLanguage.Zh ? "关于 BrainFuel…" : "About BrainFuel…";
        AboutMenuItem.Header = $"{label} · v{version}";
    }

    private void RefreshTopmostMenuText()
    {
        if (_settings is null) return;
        TopmostMenuItem.Header = Strings.Get(_settings.AlwaysOnTop ? "MenuTopmostOn" : "MenuTopmostOff");
    }

    private void RefreshDisplayMenuState()
    {
        MoveNextDisplayMenuItem.IsEnabled = Screens.All.Count > 1;
    }

    private void ToggleTopmost_Click(object? sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        _settings.AlwaysOnTop = !_settings.AlwaysOnTop;
        Topmost = _settings.AlwaysOnTop;
        RefreshTopmostMenuText();
        SettingsService.Save(_settings);
    }

    private void MovePrimary_Click(object? sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        WindowPlacementService.MoveToPrimary(this, _settings);
        SettingsService.Save(_settings);
    }

    private void MoveNextDisplay_Click(object? sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        WindowPlacementService.MoveToNextScreen(this, _settings);
        SettingsService.Save(_settings);
    }

    private void SetUpdateMenu(bool enabled, string text)
    {
        UpdateMenuItem.IsEnabled = enabled;
        UpdateMenuItem.Header = text;
    }

    private async void Update_Click(object? sender, RoutedEventArgs e)
    {
        SetUpdateMenu(false, Strings.Get("UpdateChecking"));
        try
        {
            var result = await UpdateService.CheckDownloadAndRestartAsync(stage =>
                Dispatcher.UIThread.Post(() => SetUpdateMenu(false, stage switch
                {
                    ManualUpdateStage.Checking => Strings.Get("UpdateChecking"),
                    ManualUpdateStage.Downloading => Strings.Get("UpdateDownloading"),
                    ManualUpdateStage.Restarting => Strings.Get("UpdateRestarting"),
                    _ => Strings.Get("MenuCheckUpdates"),
                })));

            SetUpdateMenu(false, result switch
            {
                ManualUpdateResult.UpToDate => Strings.Get("UpdateUpToDate"),
                ManualUpdateResult.NotInstalled => Strings.Get("UpdateInstalledOnly"),
                ManualUpdateResult.Restarting => Strings.Get("UpdateRestarting"),
                _ => Strings.Get("UpdateFailed"),
            });
            await System.Threading.Tasks.Task.Delay(3000);
        }
        finally
        {
            SetUpdateMenu(true, Strings.Get("MenuCheckUpdates"));
        }
    }

    /// <summary>The one real exit path: closes the window past the close-interception below.</summary>
    public void Quit()
    {
        _quitRequested = true;
        Close();
    }

    private void Quit_Click(object? sender, RoutedEventArgs e) => Quit();
    public void Hide_Click(object? sender, RoutedEventArgs e) => Hide();

    public void EnsureVisible()
    {
        if (_settings is not null)
            WindowPlacementService.RepairAfterScreenChange(this, _settings);
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    public void OpenSettings()
    {
        if (_settings is null) return;
        var win = new SettingsWindow(_settings);
        win.ShowDialog(this);
        win.Closed += (_, _) =>
        {
            Topmost = _settings.AlwaysOnTop;
            _vm?.OnSettingsChanged();
            WindowPlacementService.RepairAfterScreenChange(this, _settings);
            RefreshMenuState();
        };
    }

    public void OpenAbout()
    {
        var win = new AboutWindow();
        win.ShowDialog(this);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_settings is not null)
        {
            WindowPlacementService.Capture(this, _settings);
            SettingsService.Save(_settings);
        }

        // Lifecycle contract: the card is a *view* onto a background monitor,
        // so a close request (X button / Alt+F4 / system menu) means "collapse
        // to tray" — polling, alerts and updates keep running. The process only
        // ends through an explicit Quit (tray menu / card menu). OS shutdown and
        // application shutdown must never be intercepted.
        if (!_quitRequested &&
            e.CloseReason is WindowCloseReason.WindowClosing or WindowCloseReason.Undefined)
        {
            e.Cancel = true;
            if (_detailFlyout?.IsOpen == true) _detailFlyout.Hide();
            Hide();
            // The app has no taskbar button; without a hint, "close" reads as
            // "the program is gone". Point at the tray once, briefly.
            _vm?.OnNotify?.Invoke(Strings.Get("HiddenTitle"), Strings.Get("HiddenBody"));
            return;
        }

        Screens.Changed -= OnScreensChanged;
        ScalingChanged -= OnScalingChanged;
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm?.Dispose();
        base.OnClosing(e);
    }
}
