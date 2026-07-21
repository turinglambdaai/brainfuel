using Avalonia;
using Avalonia.Controls;
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
            Position = new PixelPoint(x, y);
        vm.Start();
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
            _settings.WindowX = Position.X;
            _settings.WindowY = Position.Y;
            SettingsService.Save(_settings);
        }
        _vm?.Dispose();
        base.OnClosing(e);
    }
}
