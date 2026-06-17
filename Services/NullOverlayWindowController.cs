namespace Floaty.Services;

/// <summary>
/// Fallback controller used on platforms that don't host the desktop overlay
/// (Android / iOS). Dragging the ring is a no-op there.
/// </summary>
public sealed class NullOverlayWindowController : IOverlayWindowController
{
    public void MoveBy(double dxDip, double dyDip)
    {
        // Intentionally no-op: the floating overlay is a desktop-only surface.
    }
}
