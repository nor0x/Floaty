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

    public void Resize(double widthDip, double heightDip, WindowAnchor anchor = WindowAnchor.Center)
    {
        // Intentionally no-op: the floating overlay is a desktop-only surface.
    }

    // The summon hotkey is desktop-only; this event never fires on mobile platforms.
#pragma warning disable CS0067
    public event Action<int, int>? SummonRequested;
#pragma warning restore CS0067

    public (int X, int Y) GetPosition() => (0, 0);

    public (int Width, int Height) GetSize() => (0, 0);

    public (int X, int Y, int Width, int Height) GetWorkArea() => (0, 0, 0, 0);

    public void MoveTo(int x, int y)
    {
        // Intentionally no-op.
    }

    public void Activate()
    {
        // Intentionally no-op.
    }

    public void Hide()
    {
        // Intentionally no-op.
    }

    public void FloatToTaskbarAndHide()
    {
        // Intentionally no-op.
    }
}
