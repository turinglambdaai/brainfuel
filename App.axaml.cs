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

            InitTrayIcon(main);
            desktop.ShutdownRequested += (_, _) => StopBackgroundServicesAndDisposeTray();

            // Listen for "show" pokes from second-launch attempts, and bring this
            // window to the foreground when they arrive.
            _instanceServerCts = new CancellationTokenSource();
            _ = SingleInstanceActivation.RunServerAsync(
                onShow: () => Dispatcher.UIThread.Post(() =>
                    (desktop.MainWindow as MainWindow)?.EnsureVisible()),
                _instanceServerCts.Token);

            // Installed builds quietly check for updates. Development and legacy
            // portable builds are detected by UpdateService and simply skip this.
            _updateCts = new CancellationTokenSource();
            _ = UpdateService.RunAutomaticUpdateLoopAsync(_updateCts.Token);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Keeps a tray presence so the widget can be hidden (polling keeps running)
    /// and brought back later. The tray also exposes explicit update and version
    /// information paths for installed builds.
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

        var updateItem = new NativeMenuItem { Header = UpdateText("检查更新…", "Check for updates…") };
        updateItem.Click += async (_, _) => await RunManualUpdateAsync(updateItem);
        menu.Add(updateItem);

        var version = asm.GetName().Version?.ToString(3) ?? "dev";
        var aboutItem = new NativeMenuItem
        {
            Header = $"{UpdateText("关于 BrainFuel…", "About BrainFuel…")} · v{version}",
        };
        aboutItem.Click += (_, _) => Dispatcher.UIThread.Post(main.OpenAbout);
        menu.Add(aboutItem);

        menu.Add(new NativeMenuItemSeparator());

        var quitItem = new NativeMenuItem { Header = Strings.Get("TrayQuit") };
        quitItem.Click += (_, _) => main.Close();
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

    private static async Task RunManualUpdateAsync(NativeMenuItem item)
    {
        if (!item.IsEnabled)
            return;

        item.IsEnabled = false;
        try
        {
            var result = await UpdateService.CheckDownloadAndRestartAsync(stage =>
                Dispatcher.UIThread.Post(() => item.Header = stage switch
                {
                    ManualUpdateStage.Checking => UpdateText("正在检查更新…", "Checking for updates…"),
                    ManualUpdateStage.Downloading => UpdateText("正在下载更新…", "Downloading update…"),
                    ManualUpdateStage.Restarting => UpdateText("正在安装并重启…", "Installing and restarting…"),
                    _ => UpdateText("检查更新…", "Check for updates…"),
                }));

            item.Header = result switch
            {
                ManualUpdateResult.UpToDate => UpdateText("已是最新版本", "You're up to date"),
                ManualUpdateResult.NotInstalled => UpdateText("在线更新仅支持安装版", "Online update requires the installed build"),
                ManualUpdateResult.Restarting => UpdateText("正在安装并重启…", "Installing and restarting…"),
                _ => UpdateText("更新检查失败", "Update check failed"),
            };

            // If Velopack did not terminate immediately, leave enough time for
            // the user to see the outcome before restoring the menu label.
            await Task.Delay(3000);
        }
        finally
        {
            item.Header = UpdateText("检查更新…", "Check for updates…");
            item.IsEnabled = true;
        }
    }

    private static string UpdateText(string zh, string en)
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
