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
        if (settings.WindowX is int x && settings.WindowY is int y)
        {
            var saved = new PixelPoint(x, y);
            Position = IsOnAnyScreen(saved) ? saved : EnsureOnPrimary(saved);
        }

        // A monitor got plugged/unplugged (or resolution changed): if the window
        // is now stranded off-screen, pull it back onto a visible display.
        Screens.Changed += OnScreensChanged;

        vm.OnNotify = (title, msg) => Dispatcher.UIThread.Post(() =>
            new NotificationWindow().ShowNotification(title, msg));

        vm.Start();
    }

    private void OnScreensChanged(object? sender, EventArgs e)
    {
        // Defer so the Screens list reflects the new topology before we test it.
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsOnAnyScreen(Position))
                Position = EnsureOnPrimary(Position);
        });
    }

    /// <summary>True if a point lies inside any currently connected screen's bounds.</summary>
    private bool IsOnAnyScreen(PixelPoint p)
    {
        foreach (var s in Screens.All)
            if (s.Bounds.Contains(p)) return true;
        return false;
    }

    /// <summary>
    /// Clamps a point into the primary screen's working area so the window is
    /// always reachable. Keeps the relative corner when possible.
    /// </summary>
    private PixelPoint EnsureOnPrimary(PixelPoint p)
    {
        var primary = Screens.Primary;
        if (primary is null) return p;
        var wa = primary.WorkingArea;
        int margin = 16;
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
        => await (_vm?.RefreshAsync() ?? System.Threading.Tasks.Task.CompletedTask);

    private void Settings_Click(object? sender, RoutedEventArgs e) => OpenSettings();

    private void Quit_Click(object? sender, RoutedEventArgs e) => Close();

    public void OpenSettings()
    {
        if (_settings is null) return;
        var win = new SettingsWindow(_settings);
        win.ShowDialog(this);
        win.Closed += (_, _) => _vm?.OnSettingsChanged();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_settings is not null)
        {
            // Persist the current position only if it is still on a visible
            // screen; otherwise keep whatever (valid) value was there before so
            // a stranded window doesn't lock itself off-screen across restarts.
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
