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

        // No Mica/Acrylic backdrop, and extend the DWM "sheet of glass" across the whole client area
        // (-1 margins) so the borderless window composites with transparency.
        nativeWindow.SystemBackdrop = null;
        var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(_hwnd, ref margins);

        // Tear the tray icon down when the window closes so it doesn't linger in the notification area.
        nativeWindow.Closed += (_, _) => _trayIcon?.Dispose();

        // The MAUI content tree isn't attached yet at OnWindowCreated time, so its default white
        // background would still paint. Apply transparent backgrounds once the window first activates.
        nativeWindow.Activated += OnFirstActivated;
    }

    private void OnFirstActivated(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs args)
    {
        var nativeWindow = (Microsoft.UI.Xaml.Window)sender;
        nativeWindow.Activated -= OnFirstActivated;
        MakeContentTransparent(nativeWindow.Content);
    }

    /// <summary>
    /// Walks the WinUI content tree and clears the background of the MAUI window/page containers that
    /// paint the default white (panels, templated controls such as WindowRootView, borders, content
    /// presenters). The action buttons are skipped entirely so they keep their own styling.
    /// </summary>
    private static void MakeContentTransparent(Microsoft.UI.Xaml.DependencyObject? element)
    {
        if (element is null)
            return;

        // Leave the action buttons (and their templated subtree) untouched.
        if (element is Microsoft.UI.Xaml.Controls.Button)
            return;

        var transparent = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        switch (element)
        {
            case Microsoft.UI.Xaml.Controls.Panel panel:
                panel.Background = transparent;
                break;
            case Microsoft.UI.Xaml.Controls.Border border:
                border.Background = transparent;
                break;
            case Microsoft.UI.Xaml.Controls.ContentPresenter presenter:
                presenter.Background = transparent;
                break;
            case Microsoft.UI.Xaml.Controls.Control control:
                control.Background = transparent;
                break;
        }

        var childCount = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(element);
        for (var i = 0; i < childCount; i++)
        {
            MakeContentTransparent(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(element, i));
        }
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

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref MARGINS margins);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
