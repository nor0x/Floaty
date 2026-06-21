namespace Floaty.Services;

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
    /// Resizes the overlay window to the given size in device-independent units, anchored at the
    /// bottom edge and horizontal center so the window grows upward while the ring stays in place.
    /// Used to expand the overlay when the chat panel slides out and shrink it back when it closes.
    /// </summary>
    void Resize(double widthDip, double heightDip);

    /// <summary>
    /// Raised when the user presses the global summon hotkey (Alt+F). Carries the mouse cursor
    /// position in physical screen pixels so the overlay can animate toward it.
    /// </summary>
    event Action<int, int>? SummonRequested;

    /// <summary>Current top-left position of the overlay window, in physical screen pixels.</summary>
    (int X, int Y) GetPosition();

    /// <summary>Current size of the overlay window, in physical pixels.</summary>
    (int Width, int Height) GetSize();

    /// <summary>Moves the overlay window's top-left corner to the given physical screen pixel coordinate.</summary>
    void MoveTo(int x, int y);

    /// <summary>Ensures the overlay is visible and brought to the foreground (used when summoned).</summary>
    void Activate();
}
