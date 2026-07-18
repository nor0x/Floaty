using System.Runtime.InteropServices;
using Floaty.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
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
    private DispatcherQueueTimer? _floatHideTimer;

    // Click-through state: the overlay page supplies a hit-test over its interactive elements;
    // a polling timer flips WS_EX_TRANSPARENT so transparent regions pass mouse input to the
    // windows behind while drawn content stays clickable.
    private Func<double, double, bool>? _hitTest;
    private bool _forceInteractive;
    private bool _clickThroughActive;
    private bool _layeredApplied;
    private DispatcherQueueTimer? _hitTestTimer;

    // Keep the subclass delegate alive for the window's lifetime so the GC can't collect the callback.
    private SUBCLASSPROC? _hotkeyProc;

    public event Action<int, int>? SummonRequested;

    /// <summary>
    /// Called once from the WinUI <c>OnWindowCreated</c> lifecycle hook. Resolves the
    /// <see cref="AppWindow"/> for the freshly created native window and applies the overlay styling.
    /// </summary>
    public void Initialize(Microsoft.UI.Xaml.Window nativeWindow)
    {
        // OnWindowCreated fires for every native window (overlay, settings, ...). Only the first
        // one — the floating overlay — gets the borderless/transparent/always-on-top treatment;
        // later windows (e.g. Settings) keep their normal chrome.
        if (_appWindow is not null)
            return;

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

        // Global summon hotkey (Alt+F): subclass the window to receive WM_HOTKEY, then register it.
        _hotkeyProc = HotkeyWndProc;
        SetWindowSubclass(_hwnd, _hotkeyProc, HotkeySubclassId, 0);
        if (!RegisterHotKey(_hwnd, HotkeyId, MOD_ALT | MOD_NOREPEAT, VK_F))
            System.Diagnostics.Debug.WriteLine("[Floaty] Alt+F hotkey registration failed (already in use?).");

        // Poll the cursor and toggle click-through: the window is a normal input-opaque HWND, so
        // without this its transparent regions would swallow clicks meant for the apps behind it.
        // A WS_EX_TRANSPARENT window receives no mouse messages, so polling (rather than reacting
        // to pointer events) is the only way to notice the cursor arriving over interactive content.
        _hitTestTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _hitTestTimer.Interval = TimeSpan.FromMilliseconds(50);
        _hitTestTimer.IsRepeating = true;
        _hitTestTimer.Tick += (_, _) => UpdateClickThrough();
        _hitTestTimer.Start();

        // Tear the tray icon + hotkey down when the window closes.
        nativeWindow.Closed += (_, _) =>
        {
            _hitTestTimer?.Stop();
            ApplyClickThrough(false);
            UnregisterHotKey(_hwnd, HotkeyId);
            if (_hotkeyProc is not null)
                RemoveWindowSubclass(_hwnd, _hotkeyProc, HotkeySubclassId);
            _trayIcon?.Dispose();
        };
    }

    public void SetInteractiveHitTest(Func<double, double, bool>? hitTest) => _hitTest = hitTest;

    public void SetForceInteractive(bool force)
    {
        _forceInteractive = force;
        if (force)
            ApplyClickThrough(false); // take effect immediately; don't wait for the next poll tick
    }

    private void UpdateClickThrough()
    {
        if (_appWindow is null || _hitTest is null)
            return;

        if (_forceInteractive)
        {
            ApplyClickThrough(false);
            return;
        }

        // Hidden (floated to tray): nothing to hit, leave the style as-is.
        if (!_appWindow.IsVisible)
            return;

        if (!GetCursorPos(out var pt))
            return;

        var client = pt;
        ScreenToClient(_hwnd, ref client);

        var size = _appWindow.ClientSize; // physical pixels
        if (client.X < 0 || client.Y < 0 || client.X >= size.Width || client.Y >= size.Height)
        {
            // Cursor is outside the window entirely — never block the apps behind.
            ApplyClickThrough(true);
            return;
        }

        // The hit-test works in DIPs; re-reading the DPI every tick keeps monitor changes correct.
        var scale = GetDpiForWindow(_hwnd) / 96.0;
        ApplyClickThrough(!_hitTest(client.X / scale, client.Y / scale));
    }

    private void ApplyClickThrough(bool enable)
    {
        if (_hwnd == 0 || enable == _clickThroughActive)
            return;
        _clickThroughActive = enable;

        var ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        if (!_layeredApplied)
        {
            // WS_EX_TRANSPARENT only passes input to other processes' windows when the window is
            // layered; alpha 255 keeps the DWM-composited visuals untouched.
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new nint(ex | WS_EX_LAYERED));
            SetLayeredWindowAttributes(_hwnd, 0, 255, LWA_ALPHA);
            _layeredApplied = true;
            ex |= WS_EX_LAYERED;
        }

        ex = enable ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new nint(ex));
    }

    private nint HotkeyWndProc(nint hWnd, uint msg, nint wParam, nint lParam, nuint id, nuint refData)
    {
        if (msg == WM_HOTKEY && (int)wParam == HotkeyId && GetCursorPos(out var pt))
            SummonRequested?.Invoke(pt.X, pt.Y);

        return DefSubclassProc(hWnd, msg, wParam, lParam);
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

    public void Resize(double widthDip, double heightDip, WindowAnchor anchor = WindowAnchor.Center)
    {
        if (_appWindow is null)
            return;

        // Sizes/positions are physical pixels; the incoming size is device-independent.
        var scale = GetDpiForWindow(_hwnd) / 96.0;
        var newWidth = (int)Math.Round(widthDip * scale);
        var newHeight = (int)Math.Round(heightDip * scale);

        var pos = _appWindow.Position;
        var size = _appWindow.Size;

        // Anchor the bottom edge so the window grows upward; horizontally anchor the left edge
        // (ring on the left stays put), the right edge (ring on the right stays put), or the center.
        var bottom = pos.Y + size.Height;
        var newX = anchor switch
        {
            WindowAnchor.Left => pos.X,
            WindowAnchor.Right => pos.X + size.Width - newWidth,
            _ => pos.X + (size.Width / 2) - (newWidth / 2),
        };

        _appWindow.MoveAndResize(new RectInt32(
            newX,
            bottom - newHeight,
            newWidth,
            newHeight));
    }

    public (int X, int Y, int Width, int Height) GetWorkArea()
    {
        if (_appWindow is null)
            return (0, 0, 0, 0);

        var area = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Nearest);
        var work = area.WorkArea;
        return (work.X, work.Y, work.Width, work.Height);
    }

    public (int X, int Y) GetPosition()
    {
        if (_appWindow is null)
            return (0, 0);
        var p = _appWindow.Position;
        return (p.X, p.Y);
    }

    public (int Width, int Height) GetSize()
    {
        if (_appWindow is null)
            return (0, 0);
        var s = _appWindow.Size;
        return (s.Width, s.Height);
    }

    public void MoveTo(int x, int y) => _appWindow?.Move(new PointInt32(x, y));

    public void Activate()
    {
        _appWindow?.Show();
        SetForegroundWindow(_hwnd);
    }

    public void Hide() => _appWindow?.Hide();

    public void FloatToTaskbarAndHide()
    {
        if (_appWindow is null)
            return;

        _floatHideTimer?.Stop();

        var start = _appWindow.Position;
        var size = _appWindow.Size;
        var area = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
        var work = area.WorkArea;

        const int marginPx = 12;
        var targetX = work.X + work.Width - size.Width - marginPx;
        var targetY = work.Y + work.Height - size.Height - marginPx;

        var timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(16);
        var startedAt = DateTime.UtcNow;
        const double durationMs = 300;

        timer.Tick += (_, _) =>
        {
            var elapsedMs = (DateTime.UtcNow - startedAt).TotalMilliseconds;
            var t = Math.Clamp(elapsedMs / durationMs, 0, 1);
            var eased = 1 - Math.Pow(1 - t, 3); // cubic-out

            _appWindow.Move(new PointInt32(
                (int)Math.Round(start.X + (targetX - start.X) * eased),
                (int)Math.Round(start.Y + (targetY - start.Y) * eased)));

            if (t < 1)
                return;

            timer.Stop();
            _floatHideTimer = null;
            _appWindow.Hide();
        };

        _floatHideTimer = timer;
        timer.Start();
    }

    // --- Global hotkey (Alt+F) ---

    private const int HotkeyId = 0xF10A;
    private const nuint HotkeySubclassId = 2; // distinct from WindowsTrayIcon's subclass id (1)
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint VK_F = 0x46;

    private delegate nint SUBCLASSPROC(nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(nint hWnd, SUBCLASSPROC pfnSubclass, nuint uIdSubclass, nuint dwRefData);

    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(nint hWnd, SUBCLASSPROC pfnSubclass, nuint uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam);

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

    // Extended-style click-through: WS_EX_TRANSPARENT on a layered window lets mouse input fall
    // through to whatever window is behind the overlay.
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x00000020L;
    private const long WS_EX_LAYERED = 0x00080000L;
    private const uint LWA_ALPHA = 0x00000002;

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(nint hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(nint hWnd, ref POINT lpPoint);

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
