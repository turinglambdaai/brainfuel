using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using BrainFuel.Services;
using BrainFuel.ViewModels;

namespace BrainFuel;

public partial class MainWindow : Window
{
    private AppSettings? _settings;
    private MainViewModel? _vm;
    private bool _placementReady;

    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>Wires up the view model and starts polling. Called once on startup.</summary>
    public void Initialize(AppSettings settings, MainViewModel vm)
    {
        _settings = settings;
        _vm = vm;
        DataContext = vm;

        // Existing users retain the old topmost behavior through settings
        // migration. New users start non-topmost to avoid covering active work.
        Topmost = settings.AlwaysOnTop;
        Position = WindowPlacementService.Restore(this, settings);
        _placementReady = true;
        WindowPlacementService.Capture(this, settings);

        RefreshMenuState();
        Screens.Changed += OnScreensChanged;
        PositionChanged += OnPositionChanged;

        vm.OnNotify = (title, msg) => Dispatcher.UIThread.Post(() =>
            new NotificationWindow().ShowNotification(title, msg));

        vm.Start();
    }

    private void OnPositionChanged(object? sender, PixelPointEventArgs e)
    {
        if (_placementReady && _settings is not null)
            WindowPlacementService.Capture(this, _settings);
    }

    private async void OnScreensChanged(object? sender, EventArgs e)
    {
        // Display enumeration and WorkingArea can settle asynchronously on Windows
        // after unplug/replug or a DPI/taskbar change. Re-evaluate a few times.
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
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private async void Refresh_Click(object? sender, RoutedEventArgs e)
    {
        if (_settings is { IsValid: false })
        {
            OpenSettings();
            return;
        }
        await (_vm?.RefreshAsync() ?? System.Threading.Tasks.Task.CompletedTask);
    }

    private void OpenMenu_Click(object? sender, RoutedEventArgs e)
    {
        RefreshMenuState();
        CardMenu.Open(MenuButton);
    }

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

    private void Quit_Click(object? sender, RoutedEventArgs e) => Close();
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
        Screens.Changed -= OnScreensChanged;
        PositionChanged -= OnPositionChanged;
        _vm?.Dispose();
        base.OnClosing(e);
    }
}
