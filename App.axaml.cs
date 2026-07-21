using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using BrainFuel.Services;
using BrainFuel.ViewModels;

namespace BrainFuel;

public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = new();
    public static MainViewModel? ViewModel { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Settings = SettingsService.Load();

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
        }

        base.OnFrameworkInitializationCompleted();
    }
}
