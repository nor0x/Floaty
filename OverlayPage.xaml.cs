using System.Collections.ObjectModel;
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
    private const double ChatHeight = 560;

    // How many degrees the ring "rolls" per device-independent unit dragged horizontally.
    private const double RotationPerDip = 0.6;

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
    }

    private void OnRingPanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _lastTotalX = 0;
                _lastTotalY = 0;
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
                // Ease the roll back to rest for a settled, natural feel.
                _ = Ring.RotateToAsync(0, 350, Easing.SinOut);
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
            .Commit(this, "FloatySummon", length: SummonMoveMs);
    }

    private async Task SpinRingAsync()
    {
        // CubicOut decelerates: the ring spins fast through the glide, then eases to a stop afterwards.
        await Ring.RotateToAsync(Ring.Rotation + SummonSpinDegrees, SummonSpinMs, Easing.CubicOut);
        Ring.Rotation = 0; // 720° ≡ 0°; reset without an unwind animation
    }

    // Toggle the slide-out chat panel, growing/shrinking the overlay window to match.
    private async void OnOpenChatClicked(object? sender, EventArgs e)
    {
        _chatOpen = !_chatOpen;

        if (_chatOpen)
        {
            _windowController.Resize(ChatWidth, ChatHeight);
            ChatPanel.IsVisible = true;

            // Slide the panel up into view.
            ChatPanel.TranslationY = 24;
            ChatPanel.Opacity = 0;
            await Task.WhenAll(
                ChatPanel.TranslateToAsync(0, 0, 220, Easing.SinOut),
                ChatPanel.FadeToAsync(1, 220));

            ChatEntry.Focus();
        }
        else
        {
            ChatEntry.Unfocus();
            ChatPanel.IsVisible = false;
            _windowController.Resize(CompactWidth, CompactHeight);
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
        ScrollToLatest();

        try
        {
            var reply = await _chatService.GetResponseAsync(history);
            pending.Text = string.IsNullOrWhiteSpace(reply) ? "(no response)" : reply;
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

    private void OnCloseClicked(object? sender, EventArgs e) =>
        Application.Current?.Quit();
}
