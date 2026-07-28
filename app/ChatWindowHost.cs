using Floaty.Services;
using Microsoft.Extensions.DependencyInjection;

#if WINDOWS
using Floaty.Platforms.Windows;
#endif

namespace Floaty;

/// <summary>
/// Standalone host for the fixed chat panel placement.
/// </summary>
public sealed class ChatWindowHost : IChatPanelHost
{
	private const double WindowMarginDip = 8;
	private const int FirstPlacementMarginPx = 24;

	private readonly IServiceProvider _services;
	private readonly SettingsService _settings;
	private readonly IRingFeedback _ringFeedback;
	private readonly IChatWindowController _chatController;
#if WINDOWS
	private readonly NativeWindowBinder? _binder;
#endif

	private ChatPanelView? _panel;
	private ChatWindowPage? _page;
	private Window? _window;

	private double _panelWidth;
	private double _panelHeight;
	private IDispatcherTimer? _persistTimer;

	public ChatWindowHost(IServiceProvider services, SettingsService settings, IRingFeedback ringFeedback)
	{
		_services = services;
		_settings = settings;
		_ringFeedback = ringFeedback;
		_chatController = _services.GetRequiredService<IChatWindowController>();
#if WINDOWS
		_binder = _services.GetService<NativeWindowBinder>();
#endif

		_panelWidth = ClampWidth(_settings.Current.ChatWindowWidth);
		_panelHeight = ClampHeight(_settings.Current.ChatWindowHeight);
	}

	public bool IsOpen { get; private set; }

	public void Toggle()
	{
		if (IsOpen)
			CollapseRequested();
		else
			Show();
	}

	public void Show()
	{
		EnsureWindow();
		if (_panel is null)
			return;

		if (IsOpen)
		{
			_chatController.Activate();
			_panel.FocusEntry();
			return;
		}

		_chatController.Activate();
		_chatController.SetAlwaysOnTop(_settings.Current.AlwaysOnTop);

		_panel.BeginOpen();
		_chatController.Resize(
			_panelWidth + (WindowMarginDip * 2),
			_panelHeight + (WindowMarginDip * 2),
			WindowAnchor.Left);

		_ = _panel.AnimateInAsync();
		IsOpen = true;
		SchedulePersistWindowBounds();
	}

	/// <summary>
	/// Opens the chat window (creating it if needed) and attaches files dropped on the ring to the
	/// pending prompt. The panel only exists once the window has been built, hence the Show() first.
	/// With <paramref name="memorize"/> the files are embedded into memory instead (Alt-drop).
	/// </summary>
	public void DropFiles(IReadOnlyList<string> paths, bool memorize = false)
	{
		Show();
		if (memorize)
			_panel?.MemorizeFiles(paths);
		else
			_panel?.AttachFiles(paths);
	}

	/// <summary>
	/// Opens the chat window and attaches the text the user had selected when they hit the summon
	/// hotkey. Same shape as <see cref="DropFiles"/>: the panel only exists once the window is built.
	/// </summary>
	public void AttachSelection(SelectedText selection)
	{
		Show();
		_panel?.AttachSelection(selection);
	}

	/// <summary>Surfaces the "folders aren't supported" hint in the panel's inline toast.</summary>
	public void ShowFolderDropHint()
	{
		Show();
		_panel?.ShowFolderDropHint();
	}

	public void Close()
	{
		_persistTimer?.Stop();

		if (_panel is not null)
		{
			_panel.Detach();
			_panel = null;
		}

		if (_window is not null)
		{
			Application.Current?.CloseWindow(_window);
			_window = null;
		}
		else
		{
			_chatController.Hide();
		}

		_page = null;
		IsOpen = false;
	}

	public void SetAlwaysOnTop(bool alwaysOnTop) => _chatController.SetAlwaysOnTop(alwaysOnTop);

	public void RequestPanelSize(double widthDip, double heightDip)
	{
		_panelWidth = ClampWidth(widthDip);
		_panelHeight = ClampHeight(heightDip);

		_chatController.Resize(
			_panelWidth + (WindowMarginDip * 2),
			_panelHeight + (WindowMarginDip * 2),
			WindowAnchor.Left);

		SchedulePersistWindowBounds();
	}

	public double AvailableWidthDip()
	{
		var wa = _chatController.GetWorkArea();
		if (wa.Width <= 0)
			return ChatPanelView.MaxChatWidth;

		var (x, _) = _chatController.GetPosition();
		var max = ((wa.X + wa.Width) - x) / DisplayScale - (WindowMarginDip * 2);
		return Math.Clamp(max, ChatPanelView.MinChatWidth, ChatPanelView.MaxChatWidth);
	}

	public double AvailableListHeightDip(double chromeDip)
	{
		var wa = _chatController.GetWorkArea();
		if (wa.Height <= 0)
			return ChatPanelView.MaxChatListHeight;

		var (_, y) = _chatController.GetPosition();
		var (_, h) = _chatController.GetSize();
		var maxWindowDip = (y + h - wa.Y) / DisplayScale - 8;
		return Math.Clamp(maxWindowDip - chromeDip - (WindowMarginDip * 2),
			ChatPanelView.MinChatListHeight, ChatPanelView.MaxChatListHeight);
	}

	public void SetForceInteractive(bool force) => _chatController.SetForceInteractive(force);

	public void KeepInteractiveFor(TimeSpan duration) => _chatController.KeepInteractiveFor(duration);

	public void MoveWindowBy(double dxDip, double dyDip)
	{
		_chatController.MoveBy(dxDip, dyDip);
		SchedulePersistWindowBounds();
	}

	public void CollapseRequested() => _ = CollapseAsync();

	public void SetBusy(bool busy) => _ringFeedback.SetBusy(busy);

	private async Task CollapseAsync()
	{
		if (_panel is null || !IsOpen)
			return;

		_panel.BeginClose();
		await _panel.FadeOutAsync();
		_panel.EndClose();

		_chatController.Hide();
		IsOpen = false;
		SchedulePersistWindowBounds();
	}

	private void EnsureWindow()
	{
		if (_window is not null)
			return;

		var panel = _services.GetRequiredService<ChatPanelView>();
		panel.Attach(this, _panelWidth);
		panel.SetDragBarVisible(true);

		var page = new ChatWindowPage(_chatController, panel);
		_panel = panel;
		_page = page;

#if WINDOWS
		if (_binder is not null && _chatController is WindowsChatWindowController windowsChat)
			_binder.ExpectNext(windowsChat.Initialize);
#endif

		_window = new Window(page)
		{
			Title = "Floaty Chat",
			Width = _panelWidth + (WindowMarginDip * 2),
			Height = _panelHeight + (WindowMarginDip * 2),
			MinimumWidth = ChatPanelView.MinChatWidth + (WindowMarginDip * 2),
			MinimumHeight = 220,
		};

		Application.Current?.OpenWindow(_window);
		PositionWindow();
	}

	private void PositionWindow()
	{
		var wa = _chatController.GetWorkArea();
		if (wa.Width <= 0)
			return;

		var (w, h) = _chatController.GetSize();
		if (w <= 0 || h <= 0)
		{
			w = (int)Math.Round((_panelWidth + (WindowMarginDip * 2)) * DisplayScale);
			h = (int)Math.Round((_panelHeight + (WindowMarginDip * 2)) * DisplayScale);
		}

		var savedX = _settings.Current.ChatWindowX;
		var savedY = _settings.Current.ChatWindowY;

		int x;
		int y;
		if (savedX.HasValue && savedY.HasValue)
		{
			x = Math.Clamp(savedX.Value, wa.X, wa.X + wa.Width - w);
			y = Math.Clamp(savedY.Value, wa.Y, wa.Y + wa.Height - h);
		}
		else
		{
			x = wa.X + FirstPlacementMarginPx;
			y = wa.Y + wa.Height - h - FirstPlacementMarginPx;
		}

		_chatController.MoveTo(x, y);
		SchedulePersistWindowBounds();
	}

	private void SchedulePersistWindowBounds()
	{
		if (!IsOpen && !_chatController.IsVisible)
			return;

		_persistTimer ??= CreatePersistTimer();
		_persistTimer.Stop();
		_persistTimer.Start();
	}

	private IDispatcherTimer CreatePersistTimer()
	{
		var timer = Application.Current?.Dispatcher.CreateTimer()
			?? throw new InvalidOperationException("Application dispatcher is unavailable.");

		timer.Interval = TimeSpan.FromMilliseconds(500);
		timer.IsRepeating = false;
		timer.Tick += (_, _) =>
		{
			timer.Stop();
			PersistWindowBounds();
		};
		return timer;
	}

	private void PersistWindowBounds()
	{
		var (x, y) = _chatController.GetPosition();

		var config = _settings.Current;
		var width = ClampWidth(_panelWidth);
		var height = ClampHeight(_panelHeight);

		if (config.ChatWindowX == x
			&& config.ChatWindowY == y
			&& Math.Abs(config.ChatWindowWidth - width) < 0.5
			&& Math.Abs(config.ChatWindowHeight - height) < 0.5)
			return;

		config.ChatWindowX = x;
		config.ChatWindowY = y;
		config.ChatWindowWidth = width;
		config.ChatWindowHeight = height;
		_settings.Save(config);
	}

	private static double ClampWidth(double width) =>
		Math.Clamp(width <= 0 ? ChatPanelView.DefaultChatWidth : width,
			ChatPanelView.MinChatWidth, ChatPanelView.MaxChatWidth);

	private static double ClampHeight(double height) =>
		Math.Clamp(height <= 0 ? 420 : height,
			ChatPanelView.MinChatListHeight, ChatPanelView.MaxChatListHeight);

	private static double DisplayScale => DeviceDisplay.Current.MainDisplayInfo.Density;
}
