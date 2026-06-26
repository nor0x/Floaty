using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using Floaty.Services;
using Microsoft.Extensions.AI;

namespace Floaty;

public partial class OverlayPage : ContentPage
{
    private sealed class SlashCommand
    {
        public SlashCommand(string name, string description)
        {
            Name = name;
            Description = description;
        }

        public string Name { get; }
        public string Description { get; }
        public string Token => $"/{Name}";
    }

    private readonly IOverlayWindowController _windowController;
    private readonly SettingsService _settings;
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

    // While waiting for the first model token, the ring does a "spin, pause, spin" loader loop.
    private CancellationTokenSource? _chatWaitingSpinCts;

    private readonly IReadOnlyList<SlashCommand> _allSlashCommands =
    [
        new("new", "Clear the current conversation"),
        new("capture", "Capture and remember the current app"),
        new("settings", "Open Floaty settings"),
        new("config", "Open Floaty config folder"),
    ];
    private readonly ObservableCollection<SlashCommand> _filteredSlashCommands = new();
    private bool _slashSuggestionsVisible;
    private int _slashSelectedIndex = -1;
    private string _activeSlashToken = string.Empty;
    private bool _suppressEntryTextChanged;
    private bool _updatingSlashSelection;

#if WINDOWS
    private Microsoft.UI.Xaml.Controls.TextBox? _chatEntryTextBox;
#endif

    // Cumulative pan offset reported on the previous PanUpdated event, used to derive per-frame deltas.
    private double _lastTotalX;
    private double _lastTotalY;

    private bool _chatOpen;

    // True while the open/collapse animation is running, so SizeChanged doesn't fight the animated resize.
    private bool _chatAnimating;

    private bool _waitingForFirstChunk;

    public ObservableCollection<ChatMessageVm> Messages { get; } = new();

    public OverlayPage(
        IOverlayWindowController windowController,
        SettingsService settings,
        IChatService chatService,
        IScreenCaptureService captureService,
        IMemoryService memoryService,
        IServiceProvider services)
    {
        InitializeComponent();
        _windowController = windowController;
        _settings = settings;
        _chatService = chatService;
        _captureService = captureService;
        _memoryService = memoryService;
        _services = services;
        MessagesList.ItemsSource = Messages;
        SlashSuggestionsList.ItemsSource = _filteredSlashCommands;
        ChatEntry.HandlerChanged += OnChatEntryHandlerChanged;

        _settings.Changed += OnSettingsChanged;
        ApplyRingImage();

        // Summon (Alt+F): glide the window to the mouse with a ring spin.
        _windowController.SummonRequested += OnSummonRequested;

        StartIdleSpin();
    }

    private void OnSettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.Dispatch(ApplyRingImage);

    private void ApplyRingImage()
    {
        var selected = _settings.Current.RingImageFileName;
        if (_settings.IsBuiltInRingImage(selected))
        {
            Ring.Source = selected;
            return;
        }

        var selectedPath = _settings.GetRingImageFullPath(selected);
        Ring.Source = selectedPath is null ? "ring1.png" : ImageSource.FromFile(selectedPath);
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

    // Chat loader animation: full spin -> short wait -> full spin -> longer wait, repeating
    // until the first non-empty chunk arrives from the model.
    private void StartChatWaitingSpin()
    {
        StopChatWaitingSpin();

        _waitingForFirstChunk = true;
        _ringBusy = true;
        _chatWaitingSpinCts = new CancellationTokenSource();
        _ = RunChatWaitingSpinAsync(_chatWaitingSpinCts.Token);
    }

    private void StopChatWaitingSpin()
    {
        _waitingForFirstChunk = false;

        if (_chatWaitingSpinCts is not null)
        {
            _chatWaitingSpinCts.Cancel();
            _chatWaitingSpinCts.Dispose();
            _chatWaitingSpinCts = null;
        }

        _ringBusy = false;
    }

    private async Task RunChatWaitingSpinAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await AnimateRingByAsync(360, 720, Easing.CubicInOut, cancellationToken);
                await Task.Delay(160, cancellationToken);
                await AnimateRingByAsync(360, 620, Easing.SinOut, cancellationToken);
                await Task.Delay(320, cancellationToken);

                // Keep rotation values bounded while preserving visual orientation.
                if (Math.Abs(Ring.Rotation) > 3600)
                    Ring.Rotation %= 360;
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the first streamed text arrives or the request completes.
        }
    }

    private async Task AnimateRingByAsync(
        double deltaDegrees,
        int durationMs,
        Easing easing,
        CancellationToken cancellationToken)
    {
        var start = Ring.Rotation;
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.ElapsedMilliseconds < durationMs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var t = Math.Clamp(stopwatch.Elapsed.TotalMilliseconds / durationMs, 0, 1);
            Ring.Rotation = start + deltaDegrees * easing.Ease(t);
            await Task.Delay(16, cancellationToken);
        }

        Ring.Rotation = start + deltaDegrees;
    }

    // Toggle the slide-out chat panel, growing/shrinking the overlay window to match.
    private void OnOpenChatClicked(object? sender, EventArgs e)
    {
        if (_chatOpen)
            CollapseChat();
        else
            _ = ShowChatAsync();
    }

    // Open the chat panel (idempotent — a no-op if already open). Only the input row shows until
    // messages exist; the panel's SizeChanged grows the window from here as the messages area expands.
    // The window grows from its left edge (anchorLeft) so the ring on the left stays put.
    private async Task ShowChatAsync()
    {
        if (_chatOpen)
            return;

        _chatOpen = true;
        MessagesList.IsVisible = Messages.Count > 0;
        _lastChatWindowHeight = 0;
        _windowController.Resize(_chatWidth, ChatBaseHeight + 80, anchorLeft: true);
        ChatPanel.IsVisible = true;

        // Slide the panel up into view.
        ChatPanel.TranslationY = 24;
        ChatPanel.Opacity = 0;
        await Task.WhenAll(
            ChatPanel.TranslateToAsync(0, 0, 220, Easing.SinOut),
            ChatPanel.FadeToAsync(1, 220));

        ChatEntry.Focus();
    }

    private void OnCollapseChatClicked(object? sender, EventArgs e) => CollapseChat();

    // Collapse the chat panel: animate the window width down to compact, anchored at the left edge so
    // the panel slides shut to the left (to width 0) while the ring and action bar stay fixed in place.
    private void CollapseChat()
    {
        if (!_chatOpen)
            return;

        HideSlashSuggestions();
        _chatOpen = false;
        _chatAnimating = true;
        ChatEntry.Unfocus();

        var startWidth = _chatWidth;
        var startHeight = _lastChatWindowHeight > 0 ? _lastChatWindowHeight : ChatBaseHeight + 80;

        _ = ChatPanel.FadeToAsync(0, 180);
        new Animation(t => _windowController.Resize(
                startWidth + (CompactWidth - startWidth) * t,
                startHeight + (CompactHeight - startHeight) * t,
                anchorLeft: true),
            0, 1, Easing.CubicIn)
            .Commit(this, "ChatCollapse", length: 220, finished: (_, _) =>
            {
                ChatPanel.IsVisible = false;
                ChatPanel.Opacity = 1;
                ChatPanel.TranslationY = 0;
                _lastChatWindowHeight = 0;
                _chatAnimating = false;
                _windowController.Resize(CompactWidth, CompactHeight, anchorLeft: true);
            });
    }

    // Grow (or shrink) the overlay window to match the chat panel's content height. The panel hugs its
    // content — collapsed messages area when empty, expanding up to MessagesList's MaximumHeightRequest
    // (after which the CollectionView scrolls), so the window tracks it without leaving dead space.
    private void OnChatPanelSizeChanged(object? sender, EventArgs e)
    {
        if (!_chatOpen || _chatAnimating || ChatPanel.Height <= 0)
            return;

        var target = ChatBaseHeight + ChatPanel.Height;
        if (Math.Abs(target - _lastChatWindowHeight) < 1)
            return;

        _lastChatWindowHeight = target;
        // Anchor the left edge so the ring stays put as the panel height changes.
        _windowController.Resize(_chatWidth, target, anchorLeft: true);
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

    private void OnChatEntryTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressEntryTextChanged)
            return;

        UpdateSlashSuggestions(e.NewTextValue);
    }

    private void UpdateSlashSuggestions(string? text)
    {
        if (!_chatOpen || string.IsNullOrEmpty(text) || !text.StartsWith("/", StringComparison.Ordinal))
        {
            HideSlashSuggestions();
            return;
        }

        var firstSpaceIndex = text.IndexOf(' ');
        if (firstSpaceIndex >= 0)
        {
            HideSlashSuggestions();
            return;
        }

        _activeSlashToken = text;
        var filter = text[1..];
        var previousSelection = GetSelectedSlashCommand()?.Name;

        var matches = _allSlashCommands
            .Where(command => command.Name.StartsWith(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
        {
            HideSlashSuggestions();
            return;
        }

        _filteredSlashCommands.Clear();
        foreach (var command in matches)
            _filteredSlashCommands.Add(command);

        var selectedIndex = 0;
        if (!string.IsNullOrEmpty(previousSelection))
        {
            var existingIndex = matches.FindIndex(command =>
                string.Equals(command.Name, previousSelection, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
                selectedIndex = existingIndex;
        }

        _slashSuggestionsVisible = true;
        SlashSuggestionsPanel.IsVisible = true;
        SetSlashSelection(selectedIndex);
    }

    private void HideSlashSuggestions()
    {
        _slashSuggestionsVisible = false;
        _slashSelectedIndex = -1;
        _activeSlashToken = string.Empty;
        SlashSuggestionsPanel.IsVisible = false;

        _updatingSlashSelection = true;
        try
        {
            SlashSuggestionsList.SelectedItem = null;
        }
        finally
        {
            _updatingSlashSelection = false;
        }

        _filteredSlashCommands.Clear();
    }

    private void SetSlashSelection(int index)
    {
        if (_filteredSlashCommands.Count == 0)
        {
            _slashSelectedIndex = -1;
            return;
        }

        _slashSelectedIndex = Math.Clamp(index, 0, _filteredSlashCommands.Count - 1);
        _updatingSlashSelection = true;
        try
        {
            var selected = _filteredSlashCommands[_slashSelectedIndex];
            SlashSuggestionsList.SelectedItem = selected;
            SlashSuggestionsList.ScrollTo(selected, position: ScrollToPosition.MakeVisible, animate: true);
        }
        finally
        {
            _updatingSlashSelection = false;
        }
    }

    private SlashCommand? GetSelectedSlashCommand()
    {
        if (_slashSelectedIndex < 0 || _slashSelectedIndex >= _filteredSlashCommands.Count)
            return null;
        return _filteredSlashCommands[_slashSelectedIndex];
    }

    private bool TryAutocompleteSelectedCommand()
    {
        if (!_slashSuggestionsVisible)
            return false;

        var command = GetSelectedSlashCommand();
        if (command is null)
            return false;

        var text = ChatEntry.Text ?? string.Empty;
        var firstSpace = text.IndexOf(' ');
        var remainder = firstSpace >= 0 ? text[(firstSpace + 1)..].TrimStart() : string.Empty;
        var nextText = string.IsNullOrEmpty(remainder)
            ? $"{command.Token} "
            : $"{command.Token} {remainder}";

        _suppressEntryTextChanged = true;
        ChatEntry.Text = nextText;
        _suppressEntryTextChanged = false;

        ChatEntry.CursorPosition = nextText.Length;
        HideSlashSuggestions();
        return true;
    }

    private async Task<bool> TryExecuteSelectedSlashCommandAsync()
    {
        if (!_slashSuggestionsVisible)
            return false;

        var command = GetSelectedSlashCommand();
        if (command is null)
            return false;

        await ExecuteSlashCommandAsync(command);
        return true;
    }

    private async Task ExecuteSlashCommandAsync(SlashCommand command)
    {
        switch (command.Name)
        {
            case "new":
                StopChatWaitingSpin();
                Messages.Clear();
                MessagesList.IsVisible = false;
                break;

            case "capture":
                await CaptureAndRememberAsync(addSystemNote: true);
                break;

            case "settings":
                OnSettingsClicked(this, EventArgs.Empty);
                break;

            case "config":
                await OpenConfigFolderAsync();
                break;
        }

        _suppressEntryTextChanged = true;
        ChatEntry.Text = string.Empty;
        _suppressEntryTextChanged = false;
        HideSlashSuggestions();
    }

    private async Task OpenConfigFolderAsync()
    {
        try
        {
            var homeUri = new UriBuilder(Uri.UriSchemeFile, string.Empty)
            {
                Path = FloatyPaths.Home,
            }.Uri;

            var opened = await Launcher.Default.OpenAsync(homeUri);
            if (!opened)
                await ShowToastAsync("Unable to open config folder");
        }
        catch (Exception ex)
        {
            await ShowToastAsync($"⚠️ {ex.Message}");
        }
    }

    private async Task CaptureAndRememberAsync(bool addSystemNote)
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

            if (addSystemNote)
            {
                Messages.Add(new ChatMessageVm(
                    isUser: false,
                    stored
                        ? $"System: capture saved and embedded from {result.WindowTitle}."
                        : $"System: capture saved from {result.WindowTitle} (not embedded; no API key)."));
                MessagesList.IsVisible = true;
                ScrollToLatest();
            }
        }
        catch (Exception ex)
        {
            await ShowToastAsync($"⚠️ {ex.Message}");
        }
    }

    private void OnSlashSuggestionsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingSlashSelection)
            return;

        if (e.CurrentSelection.FirstOrDefault() is not SlashCommand command)
            return;

        _slashSelectedIndex = _filteredSlashCommands.IndexOf(command);
        _ = ExecuteSlashCommandAsync(command);
    }

    private void OnChatEntryHandlerChanged(object? sender, EventArgs e)
    {
#if WINDOWS
        if (_chatEntryTextBox is not null)
            _chatEntryTextBox.KeyDown -= OnChatEntryTextBoxKeyDown;

        _chatEntryTextBox = ChatEntry.Handler?.PlatformView as Microsoft.UI.Xaml.Controls.TextBox;
        if (_chatEntryTextBox is not null)
            _chatEntryTextBox.KeyDown += OnChatEntryTextBoxKeyDown;
#endif
    }

#if WINDOWS
    private void OnChatEntryTextBoxKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Tab)
            return;

        if (!TryAutocompleteSelectedCommand())
            return;

        e.Handled = true;
    }
#endif

    private async void OnSendClicked(object? sender, EventArgs e)
    {
        if (await TryExecuteSelectedSlashCommandAsync())
            return;

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
        StartChatWaitingSpin();

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

                if (_waitingForFirstChunk)
                    StopChatWaitingSpin();

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
        finally
        {
            // Ensure the loader always stops (errors, empty streams, or very fast responses).
            StopChatWaitingSpin();
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
        await CaptureAndRememberAsync(addSystemNote: false);
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
