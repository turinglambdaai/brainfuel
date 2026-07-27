using Avalonia;
using System;
using System.Threading;
using BrainFuel.Services;

namespace BrainFuel;

class Program
{
    private static Mutex? _singleInstanceMutex;

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't
    // initialized yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Single-instance guard: a second launch silently exits (and on Windows
        // brings the running window to the foreground). The mutex name is global
        // (Global\ prefix) so it also covers different install paths of the same exe.
        _singleInstanceMutex = new Mutex(initiallyOwned: true, name: @"Global\BrainFuel.App.SingleInstance", out bool createdNew);
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

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
