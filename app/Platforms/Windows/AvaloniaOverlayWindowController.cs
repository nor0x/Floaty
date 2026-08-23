using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Floaty.Services;

namespace Floaty.Platforms.Windows;

/// <summary>
/// Overlay-specific controller: the global summon hotkey and the float-to-taskbar hide. Shared
/// borderless window behavior lives in <see cref="AvaloniaBorderlessWindowController"/>, and the tray
/// icon moved out to <c>App.axaml.cs</c> now that Avalonia provides one.
/// </summary>
public sealed class AvaloniaOverlayWindowController : AvaloniaBorderlessWindowController, IOverlayWindowController
{
    private DispatcherTimer? _floatHideTimer;
    private Win32Properties.CustomWndProcHookCallback? _hotkeyHook;

    public event Action<int, int, nint>? SummonRequested;

    public override void Initialize(Window window)
    {
        if (Window is not null)
            return;

        base.Initialize(window);
        if (Window is null || Hwnd == nint.Zero)
            return;

        // Replaces the comctl32 SetWindowSubclass/DefSubclassProc dance: Avalonia exposes a first-class
        // hook into the window procedure, so WM_HOTKEY can be observed without subclassing.
        _hotkeyHook = HotkeyWndProc;
        Win32Properties.AddWndProcHookCallback(window, _hotkeyHook);

        if (!RegisterHotKey(Hwnd, HotkeyId, MOD_ALT | MOD_NOREPEAT, VK_F))
            System.Diagnostics.Debug.WriteLine("[Floaty] Alt+F hotkey registration failed (already in use?).");

        if (Environment.GetCommandLineArgs().Contains("--minimized", StringComparer.OrdinalIgnoreCase))
            window.Hide();

        window.Closed += (_, _) =>
        {
            _floatHideTimer?.Stop();
            _floatHideTimer = null;
            UnregisterHotKey(Hwnd, HotkeyId);
            if (_hotkeyHook is not null)
                Win32Properties.RemoveWndProcHookCallback(window, _hotkeyHook);
            _hotkeyHook = null;
        };
    }

    public void FloatToTaskbarAndHide()
    {
        if (Window is null)
            return;

        _floatHideTimer?.Stop();

        var start = Window.Position;
        var (width, height) = GetSize();
        var (workX, workY, workWidth, workHeight) = GetWorkArea();

        const int marginPx = 12;
        var targetX = workX + workWidth - width - marginPx;
        var targetY = workY + workHeight - height - marginPx;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        var startedAt = DateTime.UtcNow;
        const double durationMs = 300;

        timer.Tick += (_, _) =>
        {
            if (Window is null)
            {
                timer.Stop();
                _floatHideTimer = null;
                return;
            }

            var elapsedMs = (DateTime.UtcNow - startedAt).TotalMilliseconds;
            var t = Math.Clamp(elapsedMs / durationMs, 0, 1);
            var eased = 1 - Math.Pow(1 - t, 3);

            Window.Position = new PixelPoint(
                (int)Math.Round(start.X + (targetX - start.X) * eased),
                (int)Math.Round(start.Y + (targetY - start.Y) * eased));

            if (t < 1)
                return;

            timer.Stop();
            _floatHideTimer = null;
            Window.Hide();
        };

        _floatHideTimer = timer;
        timer.Start();
    }

    private nint HotkeyWndProc(nint hWnd, uint msg, nint wParam, nint lParam, ref bool handled)
    {
        // The hotkey doesn't change focus, so the foreground window here is still the app the user was
        // working in — the only reliable moment to identify it, since Activate() is about to steal it.
        if (msg == WM_HOTKEY && (int)wParam == HotkeyId && GetCursorPos(out var pt))
            SummonRequested?.Invoke(pt.X, pt.Y, GetForegroundWindow());

        return nint.Zero;
    }

    // --- Global hotkey (Alt+F) ---

    private const int HotkeyId = 0xF10A;
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint VK_F = 0x46;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
}
