using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using BrainFuel.Services;
using BrainFuel.ViewModels;

namespace BrainFuel;

public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = new();
    public static MainViewModel? ViewModel { get; private set; }

    private static CancellationTokenSource? _instanceServerCts;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Settings = SettingsService.Load();
        ApplyTheme(Settings.ThemeMode);
        Strings.ApplyLanguage(Settings.Language);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            ViewModel = new MainViewModel(Settings);
            var main = new MainWindow();
            desktop.MainWindow = main;
            main.Initialize(Settings, ViewModel);
            main.Show();

            // First run (or key cleared): open settings so the user can configure it.
            if (!Settings.IsValid)
                main.OpenSettings();

            // Listen for "show" pokes from second-launch attempts, and bring this
            // window to the foreground when they arrive.
            _instanceServerCts = new CancellationTokenSource();
            _ = SingleInstanceActivation.RunServerAsync(
                onShow: () => Dispatcher.UIThread.Post(() =>
                {
                    var w = desktop.MainWindow;
                    if (w is null) return;
                    w.Show();
                    w.WindowState = WindowState.Normal;
                    w.Activate();
                }),
                _instanceServerCts.Token);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Switches the app theme (Fluent + the custom palette in App.axaml).</summary>
    public static void ApplyTheme(AppTheme theme)
    {
        if (Application.Current is null) return;
        Application.Current.RequestedThemeVariant = theme switch
        {
            AppTheme.Light => ThemeVariant.Light,
            AppTheme.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }
}
