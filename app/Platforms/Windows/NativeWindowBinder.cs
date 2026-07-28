namespace Floaty.Platforms.Windows;

/// <summary>
/// Routes the next native MAUI window creation callback to a caller-provided initializer.
/// </summary>
public sealed class NativeWindowBinder
{
	private Action<Microsoft.UI.Xaml.Window>? _pending;

	public void ExpectNext(Action<Microsoft.UI.Xaml.Window> initializer)
	{
		_pending = initializer;
	}

	public bool TryConsume(Microsoft.UI.Xaml.Window nativeWindow)
	{
		var pending = _pending;
		if (pending is null)
			return false;

		_pending = null;
		pending(nativeWindow);
		return true;
	}
}
