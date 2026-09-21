using Avalonia;
using System;
using System.Threading;
using BrainFuel.Services;
using Velopack;

namespace BrainFuel;

class Program
{
    private static Mutex? _singleInstanceMutex;

    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack must run before any normal application startup code. During
        // install/update/uninstall it may execute a fast lifecycle hook and exit.
        VelopackApp.Build().Run();

        // Single-instance guard: a second launch exits after asking the running
        // instance to come to the foreground.
        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: @"Global\BrainFuel.App.SingleInstance",
            out bool createdNew);

        if (!createdNew)
        {
            SingleInstanceActivation.ActivateRunningInstance();
            return;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            _singleInstanceMutex.ReleaseMutex();
            _singleInstanceMutex.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
