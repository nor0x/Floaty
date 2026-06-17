using System.Runtime.InteropServices;
using Floaty.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using WinRT.Interop;

namespace Floaty.Platforms.Windows;

/// <summary>
/// Windows (WinUI 3 / Windows App SDK) implementation of the overlay window controller.
/// Strips the window chrome, keeps it always-on-top, makes it transparent, and moves it
/// in response to drag gestures.
/// </summary>
public sealed class WindowsOverlayWindowController : IOverlayWindowController
{
    private AppWindow? _appWindow;
    private nint _hwnd;
    private WindowsTrayIcon? _trayIcon;

    /// <summary>
    /// Called once from the WinUI <c>OnWindowCreated</c> lifecycle hook. Resolves the
    /// <see cref="AppWindow"/> for the freshly created native window and applies the overlay styling.
    /// </summary>
    public void Initialize(Microsoft.UI.Xaml.Window nativeWindow)
    {
        _hwnd = WindowNative.GetWindowHandle(nativeWindow);
        var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        if (_appWindow is null)
            return;

        // Borderless, fixed-size, always-on-top.
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        // The overlay lives in the notification area, not the taskbar / Alt+Tab switcher.
        _appWindow.IsShownInSwitchers = false;
        _trayIcon = new WindowsTrayIcon(_hwnd, _appWindow);
        _trayIcon.Show();

        // Transparent window background. WinUI 3 has no AllowsTransparency (the content island
        // composites opaque, and the Win32 window paints a white GDI background), so a borderless
        // window otherwise shows up white. WinUIEx's TransparentTintBackdrop wires up the correct
        // compositor, configures DWM, and clears the GDI background to make the window see-through.
        nativeWindow.SystemBackdrop = new WinUIEx.TransparentTintBackdrop();

        // MAUI hosts its content inside a NavigationView whose content grid paints the semi-transparent
        // "NavigationViewContentBackground" brush on top of the backdrop. Override it to transparent or
        // the window still washes out white. (See dotMorten/WinUIEx discussion #155.)
        Microsoft.UI.Xaml.Application.Current.Resources["NavigationViewContentBackground"] =
            new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);

        // Tear the tray icon down when the window closes so it doesn't linger in the notification area.
        nativeWindow.Closed += (_, _) => _trayIcon?.Dispose();
    }

    public void MoveBy(double dxDip, double dyDip)
    {
        if (_appWindow is null)
            return;

        // AppWindow positions are in physical pixels; gesture deltas are device-independent.
        var scale = GetDpiForWindow(_hwnd) / 96.0;
        var current = _appWindow.Position;
        _appWindow.Move(new PointInt32(
            current.X + (int)Math.Round(dxDip * scale),
            current.Y + (int)Math.Round(dyDip * scale)));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
