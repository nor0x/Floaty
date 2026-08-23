using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Floaty.Services;

namespace Floaty.Platforms.Windows;

/// <summary>
/// Shared Windows implementation for borderless, transparent, always-on-top floating windows.
/// </summary>
/// <remarks>
/// The window chrome itself (undecorated, transparent, topmost, hidden from the taskbar) is declared
/// on the Avalonia <see cref="Window"/>, so what used to need <c>AppWindow</c>, <c>OverlappedPresenter</c>
/// and <c>WinUIEx.TransparentTintBackdrop</c> is now XAML. What Avalonia has no answer for is
/// OS-level click-through, so the <c>WS_EX_LAYERED</c>/<c>WS_EX_TRANSPARENT</c> interop below is
/// carried over unchanged from the WinUI implementation - it only ever needed an HWND, which
/// <see cref="TopLevel.TryGetPlatformHandle"/> provides.
/// </remarks>
public class AvaloniaBorderlessWindowController : IFloatingWindowController
{
    private Window? _window;
    private nint _hwnd;

    // The size we last asked for, in physical pixels. Anchored resizes have to know the window's
    // current rect, and reading it back from ClientSize does not work: Avalonia applies Width/Height
    // through layout, so a resize issued before the previous one has been laid out would anchor
    // against a stale size and walk the window across the screen. (WinUI's AppWindow.MoveAndResize
    // was atomic and had no such gap.)
    private int _appliedWidthPx;
    private int _appliedHeightPx;

    // Click-through state shared by overlay and standalone chat windows.
    private Func<double, double, bool>? _hitTest;
    private bool _forceInteractive;
    // Auto-expiring counterpart to _forceInteractive, refreshed while a file drag hovers the window.
    private DateTime _interactiveUntilUtc = DateTime.MinValue;
    private bool _clickThroughActive;
    private bool _layeredApplied;
    private DispatcherTimer? _hitTestTimer;

    protected Window? Window => _window;
    protected nint Hwnd => _hwnd;

    public bool IsVisible => _window?.IsVisible ?? false;

    /// <summary>
    /// Binds the controller to the window it owns. Called directly at composition time - unlike MAUI
    /// there is no <c>OnWindowCreated</c> lifecycle hook to wait for, because we construct the window
    /// ourselves.
    /// </summary>
    public virtual void Initialize(Window window)
    {
        if (_window is not null)
            return;

        _window = window;
        _hwnd = window.TryGetPlatformHandle()?.Handle ?? nint.Zero;

        _hitTestTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
        _hitTestTimer.Tick += (_, _) => UpdateClickThrough();
        _hitTestTimer.Start();

        window.Closed += (_, _) => ResetState();
    }

    public void SetAlwaysOnTop(bool alwaysOnTop)
    {
        if (_window is not null)
            _window.Topmost = alwaysOnTop;
    }

    public void SetInteractiveHitTest(Func<double, double, bool>? hitTest) => _hitTest = hitTest;

    public void SetForceInteractive(bool force)
    {
        _forceInteractive = force;
        if (force)
            ApplyClickThrough(false);
    }

    public void KeepInteractiveFor(TimeSpan duration)
    {
        var until = DateTime.UtcNow + duration;
        if (until <= _interactiveUntilUtc)
            return;

        _interactiveUntilUtc = until;
        // Clear WS_EX_TRANSPARENT immediately rather than waiting for the next poll tick: a drag that
        // arrives between ticks would otherwise find a window that can't accept the drop.
        ApplyClickThrough(false);
    }

    public void MoveBy(double dxDip, double dyDip)
    {
        if (_window is null)
            return;

        // Position is in physical pixels; the interface speaks device-independent units.
        var scale = _window.RenderScaling;
        _window.Position = new PixelPoint(
            _window.Position.X + (int)Math.Round(dxDip * scale),
            _window.Position.Y + (int)Math.Round(dyDip * scale));
    }

    public void Resize(double widthDip, double heightDip, WindowAnchor anchor = WindowAnchor.Center)
    {
        if (_window is null)
            return;

        var scale = _window.RenderScaling;
        var newWidth = (int)Math.Round(widthDip * scale);
        var newHeight = (int)Math.Round(heightDip * scale);

        var pos = _window.Position;
        var (width, height) = AppliedSize();

        // The bottom edge is always anchored, so the window grows upward.
        var bottom = pos.Y + height;
        var newX = anchor switch
        {
            WindowAnchor.Left => pos.X,
            WindowAnchor.Right => pos.X + width - newWidth,
            _ => pos.X + (width / 2) - (newWidth / 2),
        };

        // Size is set in DIPs (Avalonia's Width/Height), position in physical pixels.
        _window.Width = widthDip;
        _window.Height = heightDip;
        _window.Position = new PixelPoint(newX, bottom - newHeight);

        _appliedWidthPx = newWidth;
        _appliedHeightPx = newHeight;
    }

    public (int X, int Y, int Width, int Height) GetWorkArea()
    {
        if (_window is null)
            return (0, 0, 0, 0);

        var screen = _window.Screens.ScreenFromWindow(_window) ?? _window.Screens.Primary;
        if (screen is null)
            return (0, 0, 0, 0);

        var work = screen.WorkingArea;
        return (work.X, work.Y, work.Width, work.Height);
    }

    public (int X, int Y) GetPosition() =>
        _window is null ? (0, 0) : (_window.Position.X, _window.Position.Y);

    public (int Width, int Height) GetSize() => AppliedSize();

    // The window's physical size: whatever we last asked for, falling back to the measured client
    // size before the first resize (and while it is still zero during startup).
    private (int Width, int Height) AppliedSize()
    {
        if (_window is null)
            return (0, 0);

        if (_appliedWidthPx > 0 && _appliedHeightPx > 0)
            return (_appliedWidthPx, _appliedHeightPx);

        var scale = _window.RenderScaling;
        return ((int)Math.Round(_window.ClientSize.Width * scale),
                (int)Math.Round(_window.ClientSize.Height * scale));
    }

    public void MoveTo(int x, int y)
    {
        if (_window is not null)
            _window.Position = new PixelPoint(x, y);
    }

    public void Activate()
    {
        _window?.Show();
        _window?.Activate();
        // Avalonia's Activate() alone does not reliably pull focus across processes, which is exactly
        // what the summon hotkey needs.
        if (_hwnd != 0)
            SetForegroundWindow(_hwnd);
    }

    public void Hide() => _window?.Hide();

    protected void ResetState()
    {
        _hitTestTimer?.Stop();
        _hitTestTimer = null;
        ApplyClickThrough(false);

        _hitTest = null;
        _forceInteractive = false;
        _interactiveUntilUtc = DateTime.MinValue;
        _clickThroughActive = false;
        _layeredApplied = false;

        _appliedWidthPx = 0;
        _appliedHeightPx = 0;
        _window = null;
        _hwnd = 0;
    }

    private void UpdateClickThrough()
    {
        if (_window is null || _hitTest is null)
            return;

        if (_forceInteractive || DateTime.UtcNow < _interactiveUntilUtc)
        {
            ApplyClickThrough(false);
            return;
        }

        if (!_window.IsVisible)
            return;

        if (!GetCursorPos(out var pt))
            return;

        var client = pt;
        ScreenToClient(_hwnd, ref client);

        var (width, height) = GetSize();
        if (client.X < 0 || client.Y < 0 || client.X >= width || client.Y >= height)
        {
            ApplyClickThrough(true);
            return;
        }

        var scale = _window.RenderScaling;
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

    [StructLayout(LayoutKind.Sequential)]
    protected struct POINT
    {
        public int X;
        public int Y;
    }

    // Extended-style click-through. Avalonia has no cross-platform equivalent, and on Windows this
    // is the only way to make mouse input fall through to the window behind.
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x00000020L;
    private const long WS_EX_LAYERED = 0x00080000L;
    private const uint LWA_ALPHA = 0x00000002;

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
}
