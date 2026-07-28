using System.Runtime.InteropServices;
using Floaty.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using WinRT.Interop;

namespace Floaty.Platforms.Windows;

/// <summary>
/// Shared Windows implementation for borderless, transparent, always-on-top floating windows.
/// </summary>
public class WindowsBorderlessWindowController : IFloatingWindowController
{
    private AppWindow? _appWindow;
    private OverlappedPresenter? _presenter;
    private nint _hwnd;

    // Click-through state shared by overlay and standalone chat windows.
    private Func<double, double, bool>? _hitTest;
    private bool _forceInteractive;
    private bool _clickThroughActive;
    private bool _layeredApplied;
    private DispatcherQueueTimer? _hitTestTimer;

    protected AppWindow? AppWindow => _appWindow;
    protected nint Hwnd => _hwnd;

    public bool IsVisible => _appWindow?.IsVisible ?? false;

    /// <summary>
    /// Called from the WinUI OnWindowCreated lifecycle hook for the window this controller should own.
    /// </summary>
    public virtual void Initialize(Microsoft.UI.Xaml.Window nativeWindow)
    {
        if (_appWindow is not null)
            return;

        _hwnd = WindowNative.GetWindowHandle(nativeWindow);
        var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        if (_appWindow is null)
            return;

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            _presenter = presenter;
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        _appWindow.IsShownInSwitchers = false;

        nativeWindow.SystemBackdrop = new WinUIEx.TransparentTintBackdrop();

        Microsoft.UI.Xaml.Application.Current.Resources["NavigationViewContentBackground"] =
            new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);

        nativeWindow.Activated += OnActivatedRemoveBorder;

        _hitTestTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _hitTestTimer.Interval = TimeSpan.FromMilliseconds(50);
        _hitTestTimer.IsRepeating = true;
        _hitTestTimer.Tick += (_, _) => UpdateClickThrough();
        _hitTestTimer.Start();

        nativeWindow.Closed += (_, _) => ResetState();
    }

    public void SetAlwaysOnTop(bool alwaysOnTop)
    {
        if (_presenter is not null)
            _presenter.IsAlwaysOnTop = alwaysOnTop;
    }

    public void SetInteractiveHitTest(Func<double, double, bool>? hitTest) => _hitTest = hitTest;

    public void SetForceInteractive(bool force)
    {
        _forceInteractive = force;
        if (force)
            ApplyClickThrough(false);
    }

    public void MoveBy(double dxDip, double dyDip)
    {
        if (_appWindow is null)
            return;

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

        var scale = GetDpiForWindow(_hwnd) / 96.0;
        var newWidth = (int)Math.Round(widthDip * scale);
        var newHeight = (int)Math.Round(heightDip * scale);

        var pos = _appWindow.Position;
        var size = _appWindow.Size;

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
        if (_hwnd != 0)
            SetForegroundWindow(_hwnd);
    }

    public void Hide() => _appWindow?.Hide();

    protected void ResetState()
    {
        _hitTestTimer?.Stop();
        _hitTestTimer = null;
        ApplyClickThrough(false);

        _hitTest = null;
        _forceInteractive = false;
        _clickThroughActive = false;
        _layeredApplied = false;

        _presenter = null;
        _appWindow = null;
        _hwnd = 0;
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

        if (!_appWindow.IsVisible)
            return;

        if (!GetCursorPos(out var pt))
            return;

        var client = pt;
        ScreenToClient(_hwnd, ref client);

        var size = _appWindow.ClientSize;
        if (client.X < 0 || client.Y < 0 || client.X >= size.Width || client.Y >= size.Height)
        {
            ApplyClickThrough(true);
            return;
        }

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
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new nint(ex | WS_EX_LAYERED));
            SetLayeredWindowAttributes(_hwnd, 0, 255, LWA_ALPHA);
            _layeredApplied = true;
            ex |= WS_EX_LAYERED;
        }

        ex = enable ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new nint(ex));
    }

    private void OnActivatedRemoveBorder(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs args)
    {
        ((Microsoft.UI.Xaml.Window)sender).Activated -= OnActivatedRemoveBorder;

        var borderColorNone = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref borderColorNone, sizeof(uint));

        var style = GetWindowLongPtr(_hwnd, GWL_STYLE).ToInt64();
        style &= ~(WS_CAPTION | WS_THICKFRAME | WS_BORDER | WS_DLGFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
        style |= WS_POPUP;
        SetWindowLongPtr(_hwnd, GWL_STYLE, new nint(style));
        SetWindowPos(_hwnd, nint.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    [StructLayout(LayoutKind.Sequential)]
    protected struct POINT
    {
        public int X;
        public int Y;
    }

    // DWMWA_BORDER_COLOR (Windows 11 22000+); DWMWA_COLOR_NONE removes the border entirely.
    private const int DWMWA_BORDER_COLOR = 34;
    private const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;

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

    // Extended-style click-through.
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x00000020L;
    private const long WS_EX_LAYERED = 0x00080000L;
    private const uint LWA_ALPHA = 0x00000002;

    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref uint value, int size);

    [DllImport("user32.dll")]
    protected static extern uint GetDpiForWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(nint hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(nint hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint newLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hwnd, nint hwndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
