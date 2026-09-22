using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Input;
using Avalonia.Interactivity;
using BrainFuel.Services;
using BrainFuel.ViewModels;

namespace BrainFuel;

public partial class MainWindow : Window
{
    private AppSettings? _settings;
    private MainViewModel? _vm;

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
        RefreshAboutMenuText();
        if (settings.WindowX is int x && settings.WindowY is int y)
        {
            var saved = new PixelPoint(x, y);
            Position = IsOnAnyScreen(saved) ? saved : EnsureOnPrimary(saved);
        }

        Screens.Changed += OnScreensChanged;

        vm.OnNotify = (title, msg) => Dispatcher.UIThread.Post(() =>
            new NotificationWindow().ShowNotification(title, msg));

        vm.Start();
    }

    private async void OnScreensChanged(object? sender, EventArgs e)
    {
        foreach (var delay in new[] { 100, 1000, 3000 })
        {
            await System.Threading.Tasks.Task.Delay(delay);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!IsOnAnyScreen(Position))
                    Position = EnsureOnPrimary(Position);
            });
        }
    }

    private bool IsOnAnyScreen(PixelPoint p)
    {
        foreach (var s in Screens.All)
            if (s.Bounds.Contains(p)) return true;
        return false;
    }

    private PixelPoint EnsureOnPrimary(PixelPoint p)
    {
        var primary = Screens.Primary;
        if (primary is null) return p;
        var wa = primary.WorkingArea;
        const int margin = 16;
        int x = Math.Clamp(p.X, wa.X + margin, wa.Right - margin - 100);
        int y = Math.Clamp(p.Y, wa.Y + margin, wa.Bottom - margin - 40);
        return new PixelPoint(x, y);
    }

    private void Card_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private async void Refresh_Click(object? sender, RoutedEventArgs e)
    {
        // This action is intentionally quota-only. Software update lives in the
        // app menu/settings so users never have to guess what "refresh" means.
        if (_settings is { IsValid: false })
        {
            OpenSettings();
            return;
        }
        await (_vm?.RefreshAsync() ?? System.Threading.Tasks.Task.CompletedTask);
    }

    private void OpenMenu_Click(object? sender, RoutedEventArgs e)
        => CardMenu.Open(MenuButton);

    private void Settings_Click(object? sender, RoutedEventArgs e) => OpenSettings();

    private void About_Click(object? sender, RoutedEventArgs e) => OpenAbout();

    private void RefreshAboutMenuText()
    {
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "dev";
        var label = Strings.Current == AppLanguage.Zh ? "关于 BrainFuel…" : "About BrainFuel…";
        AboutMenuItem.Header = $"{label} · v{version}";
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
        if (!IsOnAnyScreen(Position))
            Position = EnsureOnPrimary(Position);
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
            _vm?.OnSettingsChanged();
            RefreshAboutMenuText();
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
            var pos = IsOnAnyScreen(Position) ? Position : EnsureOnPrimary(Position);
            _settings.WindowX = pos.X;
            _settings.WindowY = pos.Y;
            SettingsService.Save(_settings);
        }
        Screens.Changed -= OnScreensChanged;
        _vm?.Dispose();
        base.OnClosing(e);
    }
}
