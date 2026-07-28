using System.Runtime.InteropServices;
using Floaty.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;

namespace Floaty.Platforms.Windows;

/// <summary>
/// Overlay-specific controller: tray icon, global summon hotkey, and float-to-taskbar hide.
/// Shared borderless window behavior lives in <see cref="WindowsBorderlessWindowController"/>.
/// </summary>
public sealed class WindowsOverlayWindowController : WindowsBorderlessWindowController, IOverlayWindowController
{
	private WindowsTrayIcon? _trayIcon;
	private DispatcherQueueTimer? _floatHideTimer;

	// Keep the subclass delegate alive for the window's lifetime so the GC can't collect it.
	private SUBCLASSPROC? _hotkeyProc;

	public event Action<int, int>? SummonRequested;

	public override void Initialize(Microsoft.UI.Xaml.Window nativeWindow)
	{
		if (AppWindow is not null)
			return;

		base.Initialize(nativeWindow);
		if (AppWindow is null)
			return;

		_trayIcon = new WindowsTrayIcon(Hwnd, AppWindow);
		_trayIcon.Show();

		if (Environment.GetCommandLineArgs().Contains("--minimized", StringComparer.OrdinalIgnoreCase))
		{
			AppWindow.Hide();
			nativeWindow.Activated += OnActivatedStartMinimized;
		}

		_hotkeyProc = HotkeyWndProc;
		SetWindowSubclass(Hwnd, _hotkeyProc, HotkeySubclassId, 0);
		if (!RegisterHotKey(Hwnd, HotkeyId, MOD_ALT | MOD_NOREPEAT, VK_F))
			System.Diagnostics.Debug.WriteLine("[Floaty] Alt+F hotkey registration failed (already in use?).");

		nativeWindow.Closed += (_, _) =>
		{
			_floatHideTimer?.Stop();
			_floatHideTimer = null;
			UnregisterHotKey(Hwnd, HotkeyId);
			if (_hotkeyProc is not null)
				RemoveWindowSubclass(Hwnd, _hotkeyProc, HotkeySubclassId);
			_trayIcon?.Dispose();
			_trayIcon = null;
			_hotkeyProc = null;
		};
	}

	public void FloatToTaskbarAndHide()
	{
		if (AppWindow is null)
			return;

		_floatHideTimer?.Stop();

		var start = AppWindow.Position;
		var size = AppWindow.Size;
		var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
		var work = area.WorkArea;

		const int marginPx = 12;
		var targetX = work.X + work.Width - size.Width - marginPx;
		var targetY = work.Y + work.Height - size.Height - marginPx;

		var timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
		timer.Interval = TimeSpan.FromMilliseconds(16);
		var startedAt = DateTime.UtcNow;
		const double durationMs = 300;

		timer.Tick += (_, _) =>
		{
			if (AppWindow is null)
			{
				timer.Stop();
				_floatHideTimer = null;
				return;
			}

			var elapsedMs = (DateTime.UtcNow - startedAt).TotalMilliseconds;
			var t = Math.Clamp(elapsedMs / durationMs, 0, 1);
			var eased = 1 - Math.Pow(1 - t, 3);

			AppWindow.Move(new global::Windows.Graphics.PointInt32(
				(int)Math.Round(start.X + (targetX - start.X) * eased),
				(int)Math.Round(start.Y + (targetY - start.Y) * eased)));

			if (t < 1)
				return;

			timer.Stop();
			_floatHideTimer = null;
			AppWindow.Hide();
		};

		_floatHideTimer = timer;
		timer.Start();
	}

	private nint HotkeyWndProc(nint hWnd, uint msg, nint wParam, nint lParam, nuint id, nuint refData)
	{
		if (msg == WM_HOTKEY && (int)wParam == HotkeyId && GetCursorPos(out var pt))
			SummonRequested?.Invoke(pt.X, pt.Y);

		return DefSubclassProc(hWnd, msg, wParam, lParam);
	}

	private void OnActivatedStartMinimized(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs args)
	{
		((Microsoft.UI.Xaml.Window)sender).Activated -= OnActivatedStartMinimized;
		AppWindow?.Hide();
	}

	// --- Global hotkey (Alt+F) ---

	private const int HotkeyId = 0xF10A;
	private const nuint HotkeySubclassId = 2;
	private const int WM_HOTKEY = 0x0312;
	private const uint MOD_ALT = 0x0001;
	private const uint MOD_NOREPEAT = 0x4000;
	private const uint VK_F = 0x46;

	private delegate nint SUBCLASSPROC(nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData);

	[StructLayout(LayoutKind.Sequential)]
	private struct HOTKEYPOINT
	{
		public int X;
		public int Y;
	}

	[DllImport("user32.dll")]
	private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

	[DllImport("user32.dll")]
	private static extern bool UnregisterHotKey(nint hWnd, int id);

	[DllImport("user32.dll")]
	private static extern bool GetCursorPos(out HOTKEYPOINT lpPoint);

	[DllImport("comctl32.dll")]
	private static extern bool SetWindowSubclass(nint hWnd, SUBCLASSPROC pfnSubclass, nuint uIdSubclass, nuint dwRefData);

	[DllImport("comctl32.dll")]
	private static extern bool RemoveWindowSubclass(nint hWnd, SUBCLASSPROC pfnSubclass, nuint uIdSubclass);

	[DllImport("comctl32.dll")]
	private static extern nint DefSubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam);
}
