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
}
