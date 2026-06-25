using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using Floaty.Services;
using Microsoft.Extensions.AI;

namespace Floaty;

public partial class OverlayPage : ContentPage
{
    private readonly IOverlayWindowController _windowController;
    private readonly IChatService _chatService;
    private readonly IScreenCaptureService _captureService;
    private readonly IMemoryService _memoryService;
    private readonly IServiceProvider _services;

    // Compact (chat closed) vs expanded (chat open) overlay window sizes, in device-independent units.
    private const double CompactWidth = 200;
    private const double CompactHeight = 250;
    private const double ChatWidth = 360;

    // User-adjustable chat panel width (dragged via the panel's right edge), clamped to this range.
    private const double MinChatWidth = 280;
    private const double MaxChatWidth = 680;
    private double _chatWidth = ChatWidth;
    private double _resizeStartWidth;

    // Height reserved for the ring + action bar (everything below the chat panel). The chat window
    // height is this plus the chat panel's own measured height, so the window grows with the panel.
    private const double ChatBaseHeight = 196;

    // Last window height we requested from the panel's SizeChanged, to avoid redundant resizes / oscillation.
    private double _lastChatWindowHeight;

    // How many degrees the ring "rolls" per device-independent unit dragged horizontally.
    private const double RotationPerDip = 0.6;

    // Constant idle spin: a slow, subtle rotation while the ring is otherwise at rest.
    private const double IdleSpinDegPerSecond = 9;
    private const int IdleSpinIntervalMs = 33; // ~30 fps
    private IDispatcherTimer? _idleSpinTimer;

    // True while a drag or summon spin is driving the ring, so the idle spin yields to it.
    private bool _ringBusy;

    // Subtle pause after the summon glide finishes before the chat input auto-appears.
    private const int SummonRevealDelayMs = 180;

    // Cumulative pan offset reported on the previous PanUpdated event, used to derive per-frame deltas.
    private double _lastTotalX;
    private double _lastTotalY;

    private bool _chatOpen;

    public ObservableCollection<ChatMessageVm> Messages { get; } = new();

    public OverlayPage(
        IOverlayWindowController windowController,
        IChatService chatService,
        IScreenCaptureService captureService,
        IMemoryService memoryService,
        IServiceProvider services)
    {
        InitializeComponent();
        _windowController = windowController;
        _chatService = chatService;
        _captureService = captureService;
        _memoryService = memoryService;
        _services = services;
        MessagesList.ItemsSource = Messages;

        // Summon (Alt+F): glide the window to the mouse with a ring spin.
        _windowController.SummonRequested += OnSummonRequested;

        StartIdleSpin();
    }

    // Continuously rotate the ring by a small amount each tick, unless a drag/summon is in control.
    private void StartIdleSpin()
    {
        _idleSpinTimer = Dispatcher.CreateTimer();
        _idleSpinTimer.Interval = TimeSpan.FromMilliseconds(IdleSpinIntervalMs);
        _idleSpinTimer.Tick += (_, _) =>
        {
            if (_ringBusy)
                return;
            Ring.Rotation = (Ring.Rotation + IdleSpinDegPerSecond * IdleSpinIntervalMs / 1000.0) % 360;
        };
        _idleSpinTimer.Start();
    }

    private void OnRingPanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _lastTotalX = 0;
                _lastTotalY = 0;
                _ringBusy = true; // pause the idle spin while dragging
                break;

            case GestureStatus.Running:
                var dx = e.TotalX - _lastTotalX;
                var dy = e.TotalY - _lastTotalY;
                _lastTotalX = e.TotalX;
                _lastTotalY = e.TotalY;

                // Move the native window with the drag.
                _windowController.MoveBy(dx, dy);

                // Roll the ring naturally in the direction of horizontal travel.
                Ring.Rotation += dx * RotationPerDip;
                break;

            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                _ = Ring.RotateToAsync(Random.Shared.Next(0, 360), 350, Easing.SinOut);
                // Resume the idle spin from the ring's current angle.
                _ringBusy = false;
                break;
        }
    }

    // --- Summon (Alt+F): glide the window to the mouse cursor with a ring spin. ---

    // The window glides for SummonMoveMs; the ring keeps spinning longer (SummonSpinMs) and
    // decelerates to rest, so it carries momentum after the window has arrived.
    private const uint SummonMoveMs = 480;
    private const uint SummonSpinMs = 1000;
    private const double SummonSpinDegrees = 720; // whole turns so it settles back at 0°

    private void OnSummonRequested(int cursorX, int cursorY) =>
        Dispatcher.Dispatch(() => AnimateSummon(cursorX, cursorY));

    private void AnimateSummon(int cursorX, int cursorY)
    {
        _windowController.Activate();

        var (startX, startY) = _windowController.GetPosition();
        var (width, height) = _windowController.GetSize();

        // Center the window (and thus the ring) on the cursor.
        double dx = (cursorX - width / 2) - startX;
        double dy = (cursorY - height / 2) - startY;

        // Spin the ring (outlasts the glide and winds down with deceleration).
        _ = SpinRingAsync();

        new Animation(
            t => _windowController.MoveTo(
                (int)Math.Round(startX + dx * t),
                (int)Math.Round(startY + dy * t)),
            0, 1, Easing.CubicInOut)
            .Commit(this, "FloatySummon", length: SummonMoveMs, finished: (progress, cancelled) =>
            {
                // Once it lands, reveal the chat input after a subtle beat.
                if (!cancelled)
                    _ = RevealChatAfterSummonAsync();
            });
    }

    private async Task RevealChatAfterSummonAsync()
    {
        await Task.Delay(SummonRevealDelayMs);
        await ShowChatAsync();
    }

    private async Task SpinRingAsync()
    {
        _ringBusy = true; // take over from the idle spin for the summon flourish
        // CubicOut decelerates: the ring spins fast through the glide, then eases to a stop afterwards.
        await Ring.RotateToAsync(Random.Shared.Next(0, 360), SummonSpinMs, Easing.CubicOut);
        _ringBusy = false;
    }

    // Toggle the slide-out chat panel, growing/shrinking the overlay window to match.
    private async void OnOpenChatClicked(object? sender, EventArgs e)
    {
        if (_chatOpen)
            CloseChat();
        else
            await ShowChatAsync();
    }

    // Open the chat panel (idempotent — a no-op if already open). Only the input row shows until
    // messages exist; the panel's SizeChanged grows the window from here as the messages area expands.
    private async Task ShowChatAsync()
    {
        if (_chatOpen)
            return;

        _chatOpen = true;
        MessagesList.IsVisible = Messages.Count > 0;
        _lastChatWindowHeight = 0;
        _windowController.Resize(_chatWidth, ChatBaseHeight + 80);
        ChatPanel.IsVisible = true;

        // Slide the panel up into view.
        ChatPanel.TranslationY = 24;
        ChatPanel.Opacity = 0;
        await Task.WhenAll(
            ChatPanel.TranslateToAsync(0, 0, 220, Easing.SinOut),
            ChatPanel.FadeToAsync(1, 220));

        ChatEntry.Focus();
    }

    private void CloseChat()
    {
        _chatOpen = false;
        ChatEntry.Unfocus();
        ChatPanel.IsVisible = false;
        _lastChatWindowHeight = 0;
        _windowController.Resize(CompactWidth, CompactHeight);
    }

    // Grow (or shrink) the overlay window to match the chat panel's content height. The panel hugs its
    // content — collapsed messages area when empty, expanding up to MessagesList's MaximumHeightRequest
    // (after which the CollectionView scrolls), so the window tracks it without leaving dead space.
    private void OnChatPanelSizeChanged(object? sender, EventArgs e)
    {
        if (!_chatOpen || ChatPanel.Height <= 0)
            return;

        var target = ChatBaseHeight + ChatPanel.Height;
        if (Math.Abs(target - _lastChatWindowHeight) < 1)
            return;

        _lastChatWindowHeight = target;
        // Width is unchanged here, so center vs left anchor is equivalent — keep the existing behavior.
        _windowController.Resize(_chatWidth, target);
    }

    // Drag the chat panel's right edge to widen/narrow it. The window grows from the left edge so the
    // ring stays put; the bottom edge stays anchored. Height re-tracks via OnChatPanelSizeChanged as
    // the message bubbles re-wrap to the new width.
    private void OnResizeHandlePanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _resizeStartWidth = _chatWidth;
                break;

            case GestureStatus.Running:
                _chatWidth = Math.Clamp(_resizeStartWidth + e.TotalX, MinChatWidth, MaxChatWidth);
                var height = _lastChatWindowHeight > 0 ? _lastChatWindowHeight : ChatBaseHeight + 80;
                _windowController.Resize(_chatWidth, height, anchorLeft: true);
                break;
        }
    }

    private async void OnSendClicked(object? sender, EventArgs e)
    {
        var text = ChatEntry.Text?.Trim();
        if (string.IsNullOrEmpty(text))
            return;

        ChatEntry.Text = string.Empty;

        // Build the conversation to send before adding the pending placeholder.
        var history = Messages
            .Select(m => new ChatMessage(m.IsUser ? ChatRole.User : ChatRole.Assistant, m.Text))
            .ToList();
        history.Add(new ChatMessage(ChatRole.User, text));

        Messages.Add(new ChatMessageVm(isUser: true, text));
        var pending = new ChatMessageVm(isUser: false, "…");
        Messages.Add(pending);
        MessagesList.IsVisible = true;
        ScrollToLatest();

        try
        {
            var streamed = new StringBuilder();
            var repaint = Stopwatch.StartNew();
            var lastScrollMs = 0L;

            await foreach (var chunk in _chatService.GetStreamingResponseAsync(history))
            {
                Debug.WriteLine($"[Chat] Received chunk: {chunk}");
				if (string.IsNullOrEmpty(chunk))
                    continue;

                streamed.Append(chunk);

                // Repaint at ~30 FPS so streaming feels fluid without overwhelming the UI thread.
                if (repaint.ElapsedMilliseconds < 33)
                    continue;

                pending.Text = streamed.ToString();
                if (repaint.ElapsedMilliseconds - lastScrollMs >= 140)
                {
                    ScrollToLatest();
                    lastScrollMs = repaint.ElapsedMilliseconds;
                }

                repaint.Restart();
            }

            pending.Text = streamed.Length == 0 ? "(no response)" : streamed.ToString();
        }
        catch (Exception ex)
        {
            pending.Text = $"⚠️ {ex.Message}";
        }

        ScrollToLatest();
    }

    private void ScrollToLatest()
    {
        if (Messages.Count > 0)
            MessagesList.ScrollTo(Messages[^1], position: ScrollToPosition.End, animate: true);
    }

    private async void OnScreenshotClicked(object? sender, EventArgs e)
    {
        try
        {
            var result = await _captureService.CaptureUnderlyingWindowAsync();
            if (result is null)
            {
                await ShowToastAsync("Nothing to capture");
                return;
            }

            var stored = await _memoryService.RememberCaptureAsync(result);
            await ShowToastAsync($"Saved ✓ — {result.WindowTitle}{(stored ? " · embedded" : " · no API key")}");
        }
        catch (Exception ex)
        {
            await ShowToastAsync($"⚠️ {ex.Message}");
        }
    }

    // Fade a short status message in above the action bar, hold briefly, then fade out.
    private async Task ShowToastAsync(string message)
    {
        CaptureToast.Text = message;
        CaptureToast.IsVisible = true;
        await CaptureToast.FadeToAsync(1, 150);
        await Task.Delay(1600);
        await CaptureToast.FadeToAsync(0, 250);
        CaptureToast.IsVisible = false;
    }

    private void OnSettingsClicked(object? sender, EventArgs e)
    {
        var settingsPage = _services.GetRequiredService<SettingsPage>();
        Application.Current?.OpenWindow(new Window(settingsPage)
        {
            Title = "Floaty Settings",
            Width = 520,
            Height = 640,
        });
    }

    private void OnFloatToTaskbarClicked(object? sender, EventArgs e) =>
        _windowController.FloatToTaskbarAndHide();

    private void OnCloseClicked(object? sender, EventArgs e) =>
        Application.Current?.Quit();
}
