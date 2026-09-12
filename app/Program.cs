using Avalonia;
using Avalonia.Controls;

namespace Floaty;

internal static class Program
{
    /// <summary>
    /// The first authored code that runs. Velopack's install/update/uninstall hooks must precede any
    /// UI - they can exit the process outright during those lifecycle events - so this stays at the
    /// very top, exactly where it sat in the old WinUI <c>App</c> constructor.
    /// </summary>
    [STAThread]
    public static void Main(string[] args)
    {
        Velopack.VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => Platforms.Windows.WindowsAutostartService.RemoveRunValue())
            .Run();

#if WINDOWS
        // Before any window exists: ties the process to the same AppUserModelID the Start Menu
        // shortcut carries, so toast attribution, taskbar grouping and pinning all agree. Setting it
        // after the first HWND would be too late for grouping. See WindowsNotificationService for why
        // that id matters at all.
        Platforms.Windows.WindowsNotificationService.SetProcessAumid();
#endif

        // OnExplicitShutdown is load-bearing: the overlay hides rather than closes (tray + summon
        // bring it back), and closing the Settings window must not take the process with it.
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    /// <summary>Also used by the Avalonia XAML previewer, which requires this exact signature.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
