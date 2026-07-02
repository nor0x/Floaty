namespace Floaty.Services;

/// <summary>
/// Which edge of the overlay window stays fixed while it is resized. The bottom edge is always
/// anchored (the window grows upward); this controls the horizontal anchor. <see cref="Left"/>
/// keeps the left edge fixed (window grows right — ring on the left stays put), <see cref="Right"/>
/// keeps the right edge fixed (window grows left — ring on the right stays put), and
/// <see cref="Center"/> keeps the horizontal center fixed.
/// </summary>
public enum WindowAnchor
{
    Center,
    Left,
    Right,
}

/// <summary>
/// Abstraction over the native top-level window that hosts the floating swim-ring overlay.
/// Platform implementations (Windows / macOS) translate the shared, device-independent
/// drag deltas coming from the MAUI gesture layer into native window moves.
/// </summary>
public interface IOverlayWindowController
{
    /// <summary>
    /// Moves the overlay window by the given delta, expressed in device-independent
    /// units (the same units MAUI gesture events report). Implementations are responsible
    /// for any DPI / coordinate-system conversion the platform requires.
    /// </summary>
    void MoveBy(double dxDip, double dyDip);

    /// <summary>
    /// Resizes the overlay window to the given size in device-independent units. The bottom edge is
    /// always anchored (the window grows upward). Horizontally, the window is anchored per
    /// <paramref name="anchor"/> — <see cref="WindowAnchor.Left"/> / <see cref="WindowAnchor.Right"/>
    /// keep the respective edge fixed so the ring on that side stays put as the chat panel grows.
    /// </summary>
    void Resize(double widthDip, double heightDip, WindowAnchor anchor = WindowAnchor.Center);

    /// <summary>
    /// Raised when the user presses the global summon hotkey (Alt+F). Carries the mouse cursor
    /// position in physical screen pixels so the overlay can animate toward it.
    /// </summary>
    event Action<int, int>? SummonRequested;

    /// <summary>Current top-left position of the overlay window, in physical screen pixels.</summary>
    (int X, int Y) GetPosition();

    /// <summary>Current size of the overlay window, in physical pixels.</summary>
    (int Width, int Height) GetSize();

    /// <summary>
    /// Work area (excluding taskbar/menu bar) of the display the overlay currently sits on, in
    /// physical screen pixels. Used to decide which side of the ring the chat panel can open on.
    /// Returns a zero-size rect on platforms/hosts where it is unavailable.
    /// </summary>
    (int X, int Y, int Width, int Height) GetWorkArea();

    /// <summary>Moves the overlay window's top-left corner to the given physical screen pixel coordinate.</summary>
    void MoveTo(int x, int y);

    /// <summary>Ensures the overlay is visible and brought to the foreground (used when summoned).</summary>
    void Activate();

    /// <summary>Hides the overlay window while keeping the app process running.</summary>
    void Hide();

    /// <summary>
    /// Animates the overlay to the platform taskbar/menu-bar corner and then hides it,
    /// keeping the process alive so summon can bring it back.
    /// </summary>
    void FloatToTaskbarAndHide();
}
