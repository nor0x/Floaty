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
