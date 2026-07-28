namespace Floaty.Services;

/// <summary>
/// No-op floating window controller for platforms without a native standalone chat window.
/// </summary>
public sealed class NullFloatingWindowController : IChatWindowController
{
	public bool IsVisible => false;

	public void MoveBy(double dxDip, double dyDip)
	{
	}

	public void Resize(double widthDip, double heightDip, WindowAnchor anchor = WindowAnchor.Center)
	{
	}

	public (int X, int Y) GetPosition() => (0, 0);

	public (int Width, int Height) GetSize() => (0, 0);

	public (int X, int Y, int Width, int Height) GetWorkArea() => (0, 0, 0, 0);

	public void MoveTo(int x, int y)
	{
	}

	public void Activate()
	{
	}

	public void Hide()
	{
	}

	public void SetInteractiveHitTest(Func<double, double, bool>? hitTest)
	{
	}

	public void SetForceInteractive(bool force)
	{
	}

	public void SetAlwaysOnTop(bool alwaysOnTop)
	{
	}
}
