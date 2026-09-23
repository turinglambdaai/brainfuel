using System.IO;
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
    private static TrayIcon? _trayIcon;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Settings = SettingsService.Load();
        ApplyTheme(Settings.ThemeMode);
        Strings.ApplyLanguage(Settings.Language);
        AppLog.Info($"BrainFuel {typeof(App).Assembly.GetName().Version?.ToString(3)} starting" +
                    $" (data dir: {SettingsService.AppDirectory}, base domain: {Settings.BaseDomain})");

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

            InitTrayIcon(main);
            desktop.ShutdownRequested += (_, _) => DisposeTray();

            // Listen for "show" pokes from second-launch attempts, and bring this
            // window to the foreground when they arrive.
            _instanceServerCts = new CancellationTokenSource();
            _ = SingleInstanceActivation.RunServerAsync(
                onShow: () => Dispatcher.UIThread.Post(() =>
                    (desktop.MainWindow as MainWindow)?.EnsureVisible()),
                _instanceServerCts.Token);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Keeps a tray presence so the widget can be hidden (polling keeps running)
    /// and brought back later. The tray menu's Quit is the only real exit path
    /// besides the card's context-menu Quit.
    /// </summary>
    private void InitTrayIcon(MainWindow main)
    {
        var asm = typeof(App).Assembly;
        using var stream = asm.GetManifestResourceStream("BrainFuel.Assets.tray.ico")
            ?? throw new System.InvalidOperationException("embedded tray.ico not found");

        var menu = new NativeMenu();

        var showItem = new NativeMenuItem { Header = Strings.Get("TrayShow") };
        showItem.Click += (_, _) => main.EnsureVisible();
        menu.Add(showItem);

        var settingsItem = new NativeMenuItem { Header = Strings.Get("TraySettings") };
        settingsItem.Click += (_, _) => Dispatcher.UIThread.Post(main.OpenSettings);
        menu.Add(settingsItem);

        menu.Add(new NativeMenuItemSeparator());

        var quitItem = new NativeMenuItem { Header = Strings.Get("TrayQuit") };
        quitItem.Click += (_, _) => main.Quit();
        menu.Add(quitItem);

        _trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(stream),
            ToolTipText = Strings.Get("TrayTooltip"),
            Menu = menu,
        };
        // Left-click / double-click the tray icon: show the widget.
        _trayIcon.Clicked += (_, _) => main.EnsureVisible();
    }

    private void DisposeTray()
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
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
