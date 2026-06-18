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

        // Remove the 1px Windows 11 border that remains on a borderless window. Applied after the
        // window is shown (the attribute doesn't take during OnWindowCreated).
        nativeWindow.Activated += OnActivatedRemoveBorder;

        // Tear the tray icon down when the window closes so it doesn't linger in the notification area.
        nativeWindow.Closed += (_, _) => _trayIcon?.Dispose();
    }

    private void OnActivatedRemoveBorder(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs args)
    {
        ((Microsoft.UI.Xaml.Window)sender).Activated -= OnActivatedRemoveBorder;

        // Belt-and-braces border color removal (Win11).
        var borderColorNone = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref borderColorNone, sizeof(uint));

        // Force a frameless popup style — strip every frame/caption/sizing-border style the presenter
        // leaves on the window, then apply with SWP_FRAMECHANGED. This removes the 1px white frame.
        var style = GetWindowLongPtr(_hwnd, GWL_STYLE).ToInt64();
        style &= ~(WS_CAPTION | WS_THICKFRAME | WS_BORDER | WS_DLGFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
        style |= WS_POPUP;
        SetWindowLongPtr(_hwnd, GWL_STYLE, new nint(style));
        SetWindowPos(_hwnd, nint.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
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

    // DWMWA_BORDER_COLOR (Windows 11 22000+); DWMWA_COLOR_NONE removes the border entirely.
    private const int DWMWA_BORDER_COLOR = 34;
    private const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref uint value, int size);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);

    // Window-style stripping to force a truly frameless window.
    private const int GWL_STYLE = -16;
    private const long WS_POPUP = 0x80000000L;
    private const long WS_CAPTION = 0x00C00000L;
    private const long WS_THICKFRAME = 0x00040000L;
    private const long WS_BORDER = 0x00800000L;
    private const long WS_DLGFRAME = 0x00400000L;
    private const long WS_SYSMENU = 0x00080000L;
    private const long WS_MINIMIZEBOX = 0x00020000L;
    private const long WS_MAXIMIZEBOX = 0x00010000L;

    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    [DllImport("user32.dll")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint newLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hwnd, nint hwndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
