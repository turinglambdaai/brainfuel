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
    private static CancellationTokenSource? _updateCts;
    private static TrayIcon? _trayIcon;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

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

            // Only genuinely unconfigured installs get the first-run dialog.
            // A known protected key that is temporarily unavailable is retried by
            // normal quota refreshes and must not force the user to re-enter it.
            if (!Settings.HasConfiguredCredential)
                main.OpenSettings();

            InitTrayIcon(main);
            desktop.ShutdownRequested += (_, _) => StopBackgroundServicesAndDisposeTray();

            _instanceServerCts = new CancellationTokenSource();
            _ = SingleInstanceActivation.RunServerAsync(
                onShow: () => Dispatcher.UIThread.Post(() =>
                    (desktop.MainWindow as MainWindow)?.EnsureVisible()),
                _instanceServerCts.Token);

            _updateCts = new CancellationTokenSource();
            _ = UpdateService.RunAutomaticUpdateLoopAsync(_updateCts.Token);
        }

        base.OnFrameworkInitializationCompleted();
    }

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

        var updateItem = new NativeMenuItem { Header = Strings.Get("MenuCheckUpdates") };
        updateItem.Click += async (_, _) => await RunManualUpdateAsync(updateItem);
        menu.Add(updateItem);

        var version = asm.GetName().Version?.ToString(3) ?? "dev";
        var aboutItem = new NativeMenuItem
        {
            Header = $"{Localized("关于 BrainFuel…", "About BrainFuel…")} · v{version}",
        };
        aboutItem.Click += (_, _) => Dispatcher.UIThread.Post(main.OpenAbout);
        menu.Add(aboutItem);

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
        _trayIcon.Clicked += (_, _) => main.EnsureVisible();
    }

    private static async Task RunManualUpdateAsync(NativeMenuItem item)
    {
        if (!item.IsEnabled) return;

        item.IsEnabled = false;
        try
        {
            var result = await UpdateService.CheckDownloadAndRestartAsync(stage =>
                Dispatcher.UIThread.Post(() => item.Header = stage switch
                {
                    ManualUpdateStage.Checking => Strings.Get("UpdateChecking"),
                    ManualUpdateStage.Downloading => Strings.Get("UpdateDownloading"),
                    ManualUpdateStage.Restarting => Strings.Get("UpdateRestarting"),
                    _ => Strings.Get("MenuCheckUpdates"),
                }));

            item.Header = result switch
            {
                ManualUpdateResult.UpToDate => Strings.Get("UpdateUpToDate"),
                ManualUpdateResult.NotInstalled => Strings.Get("UpdateInstalledOnly"),
                ManualUpdateResult.Restarting => Strings.Get("UpdateRestarting"),
                _ => Strings.Get("UpdateFailed"),
            };

            await Task.Delay(3000);
        }
        finally
        {
            item.Header = Strings.Get("MenuCheckUpdates");
            item.IsEnabled = true;
        }
    }

    private static string Localized(string zh, string en)
        => Strings.Current == AppLanguage.Zh ? zh : en;

    private static void StopBackgroundServicesAndDisposeTray()
    {
        _instanceServerCts?.Cancel();
        _instanceServerCts?.Dispose();
        _instanceServerCts = null;

        _updateCts?.Cancel();
        _updateCts?.Dispose();
        _updateCts = null;

        DisposeTray();
    }

    private static void DisposeTray()
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
    }

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
