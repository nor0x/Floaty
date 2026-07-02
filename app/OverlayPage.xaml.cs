using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using Floaty.Services;
using Microsoft.Extensions.AI;

namespace Floaty;

public partial class OverlayPage : ContentPage
{
    private enum SlashKind
    {
        Action, // built-in commands executed immediately (e.g. /new, /settings)
        Server, // an MCP server: selecting it fills the "/name " prefix to scope the next message
        Memory, // memory commands taking free text (/remember, /recall): prefix-filled, handled on send
        Skill,  // an agent skill (SKILL.md): scopes the next message to that skill's instructions
    }

    private sealed class SlashCommand
    {
        public SlashCommand(string name, string description, SlashKind kind = SlashKind.Action, string? icon = null)
        {
            Name = name;
            Description = description;
            Kind = kind;
            Icon = icon ?? IconFont.TablerLine.Bolt;
        }

        public string Name { get; }
        public string Description { get; }
        public SlashKind Kind { get; }
        public string Icon { get; }
        public string Token => $"/{Name}";
    }

    private readonly IOverlayWindowController _windowController;
    private readonly SettingsService _settings;
    private readonly IChatService _chatService;
    private readonly IScreenCaptureService _captureService;
    private readonly IMemoryService _memoryService;
    private readonly ConversationService _conversationStore;
    private readonly SkillService _skillService;
    private readonly IServiceProvider _services;

    // The conversation currently shown in Messages; created lazily on first chat open (resume most recent).
    private Conversation? _currentConversation;
    private bool _conversationLoaded;

    // Conversation switcher (shown inside MessagesList under /chats).
    private bool _listMode;
    private readonly ObservableCollection<ConversationItemVm> _conversationItems = new();

    // Ring image width (matches the Ring's WidthRequest in XAML). The compact window hugs the ring
    // so it sits flush against both window edges, letting the chat panel open to either side with
    // the ring staying visually put.
    private const double RingWidthDip = 148;

    // Compact (chat closed) vs expanded (chat open) overlay window sizes, in device-independent units.
    private const double CompactWidth = 150;
    private const double CompactHeight = 250;
    private const double ChatWidth = 360;

    // Which side of the ring the chat panel currently occupies. Chosen from available screen space
    // when the chat opens, and re-evaluated after the ring is dragged or summoned (see ApplyChatSide).
    private bool _chatOnLeft;

    // User-adjustable chat panel width (dragged via the panel's right edge), clamped to this range.
    private const double MinChatWidth = 300;
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

#if WINDOWS
    // Mouse wheel tuning: each wheel delta unit rotates this many degrees, then idle spin
    // resumes after a short period without wheel activity.
    private const double WheelRotationPerDelta = 0.15;
    private const int ManualWheelResumeDelayMs = 400;
    private DateTime _manualWheelResumeAtUtc = DateTime.MinValue;
    private Microsoft.UI.Xaml.FrameworkElement? _ringPlatformView;
    private bool _ringPointerOver;
#endif

    // True while a drag or summon spin is driving the ring, so the idle spin yields to it.
    private bool _ringBusy;

    // Subtle pause after the summon glide finishes before the chat input auto-appears.
    private const int SummonRevealDelayMs = 180;

    // While waiting for the first model token, the ring does a "spin, pause, spin" loader loop.
    private CancellationTokenSource? _chatWaitingSpinCts;

    private readonly IReadOnlyList<SlashCommand> _builtInSlashCommands =
    [
        new("new", "Start a new conversation", icon: IconFont.TablerLine.Sparkles),
        new("chats", "Switch between conversations", icon: IconFont.TablerLine.Messages),
        new("capture", "Capture and remember the current app", icon: IconFont.TablerLine.Camera),
        new("remember", "Save text to memory", SlashKind.Memory, IconFont.TablerLine.Bulb),
        new("recall", "Search your memory", SlashKind.Memory, IconFont.TablerLine.Search),
        new("settings", "Open Floaty settings", icon: IconFont.TablerLine.Settings),
        new("config", "Open Floaty config folder", icon: IconFont.TablerLine.Folder),
    ];

    // Built-in commands plus one per enabled MCP server; rebuilt when settings change.
    private readonly List<SlashCommand> _allSlashCommands = new();
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
        ConversationService conversationStore,
        SkillService skillService,
        IServiceProvider services)
    {
        InitializeComponent();
        _windowController = windowController;
        _settings = settings;
        _chatService = chatService;
        _captureService = captureService;
        _memoryService = memoryService;
        _conversationStore = conversationStore;
        _skillService = skillService;
        _services = services;
        MessagesList.ItemsSource = Messages;
        SlashSuggestionsList.ItemsSource = _filteredSlashCommands;
        ChatEntry.HandlerChanged += OnChatEntryHandlerChanged;
        Ring.HandlerChanged += OnRingHandlerChanged;

        _settings.Changed += OnSettingsChanged;
        ApplyRingImage();
        RebuildSlashCommands();

        // Summon (Alt+F): glide the window to the mouse with a ring spin.
        _windowController.SummonRequested += OnSummonRequested;

        StartIdleSpin();
    }

    private void OnSettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.Dispatch(() =>
        {
            ApplyRingImage();
            RebuildSlashCommands();
        });

    // Rebuild the slash-command list: built-in actions, then a /name per enabled MCP server, then per
    // enabled agent skill. Names already taken by an earlier command are skipped.
    private void RebuildSlashCommands()
    {
        _allSlashCommands.Clear();
        _allSlashCommands.AddRange(_builtInSlashCommands);

        bool NameTaken(string name) =>
            _allSlashCommands.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

        foreach (var server in _settings.Current.McpServers)
        {
            if (!server.Enabled || string.IsNullOrWhiteSpace(server.Name) || NameTaken(server.Name))
                continue;
            _allSlashCommands.Add(new SlashCommand(server.Name, "MCP server", SlashKind.Server, IconFont.TablerLine.Database));
        }

        _skillService.Reload();
        foreach (var skill in _skillService.Skills)
        {
            if (!skill.Enabled || string.IsNullOrWhiteSpace(skill.Name) || NameTaken(skill.Name))
                continue;
            var description = string.IsNullOrWhiteSpace(skill.Description) ? "Agent skill" : skill.Description;
            _allSlashCommands.Add(new SlashCommand(skill.Name, description, SlashKind.Skill, IconFont.TablerLine.Bolt));
        }
    }

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
            if (IsManualWheelRotationActive())
                return;
            Ring.Rotation = (Ring.Rotation + IdleSpinDegPerSecond * IdleSpinIntervalMs / 1000.0) % 360;
        };
        _idleSpinTimer.Start();
    }

    private bool IsManualWheelRotationActive()
    {
#if WINDOWS
        return DateTime.UtcNow < _manualWheelResumeAtUtc;
#else
        return false;
#endif
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
                // The ring (and any open panel) just moved; flip sides if the panel no longer fits.
                ReevaluateChatSide();
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
        // If the chat was already open when summoned, the window moved — flip sides if needed.
        ReevaluateChatSide();
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
    // The side (left/right of the ring) is chosen from available screen space; the window then grows
    // away from the ring (ChatAnchor keeps the ring's edge fixed) so the ring stays put.
    private async Task ShowChatAsync()
    {
        if (_chatOpen)
            return;

        EnsureConversationLoaded();

        // Decide the side while still compact (the window hugs the ring, so its rect is the ring's).
        ApplyChatSide(ShouldOpenOnLeft());

        _chatOpen = true;
        MessagesList.IsVisible = Messages.Count > 0;
        _lastChatWindowHeight = 0;
        _windowController.Resize(_chatWidth, ChatBaseHeight + 80, ChatAnchor);
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
        if (_listMode)
            ExitListMode();
        PersistCurrentConversation();
        _chatOpen = false;
        _chatAnimating = true;
        ChatEntry.Unfocus();

        var startWidth = _chatWidth;
        var startHeight = _lastChatWindowHeight > 0 ? _lastChatWindowHeight : ChatBaseHeight + 80;

        // Collapse toward the ring: anchor the ring's current edge so the panel slides shut into it.
        var anchor = ChatAnchor;
        _ = ChatPanel.FadeToAsync(0, 180);
        new Animation(t => _windowController.Resize(
                startWidth + (CompactWidth - startWidth) * t,
                startHeight + (CompactHeight - startHeight) * t,
                anchor),
            0, 1, Easing.CubicIn)
            .Commit(this, "ChatCollapse", length: 220, finished: (_, _) =>
            {
                ChatPanel.IsVisible = false;
                ChatPanel.Opacity = 1;
                ChatPanel.TranslationY = 0;
                _lastChatWindowHeight = 0;
                _chatAnimating = false;
                _windowController.Resize(CompactWidth, CompactHeight, anchor);
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
        // Anchor the ring's current edge so it stays put as the panel height changes.
        _windowController.Resize(_chatWidth, target, ChatAnchor);
    }

    // Drag the chat panel's outer edge to widen/narrow it. The window grows away from the ring
    // (ChatAnchor keeps the ring's edge fixed) so the ring stays put; the bottom edge stays anchored.
    // On the left side the grip is on the panel's left, so dragging left (negative X) widens it.
    // Width is clamped to the space available on the current side so the panel can't run off-screen.
    private void OnResizeHandlePanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _resizeStartWidth = _chatWidth;
                break;

            case GestureStatus.Running:
                var delta = _chatOnLeft ? -e.TotalX : e.TotalX;
                _chatWidth = Math.Clamp(_resizeStartWidth + delta, MinChatWidth, AvailableChatWidthDip());
                var height = _lastChatWindowHeight > 0 ? _lastChatWindowHeight : ChatBaseHeight + 80;
                _windowController.Resize(_chatWidth, height, ChatAnchor);
                break;
        }
    }

    // --- Dynamic chat-panel side (left/right of the ring) ---

    // Horizontal anchor that keeps the ring's current edge fixed while the window resizes: when the
    // panel is on the left the ring is flush right (anchor right); otherwise flush left (anchor left).
    private WindowAnchor ChatAnchor => _chatOnLeft ? WindowAnchor.Right : WindowAnchor.Left;

    // Scale for converting MAUI device-independent units to physical screen pixels.
    private static double DisplayScale => DeviceDisplay.Current.MainDisplayInfo.Density;

    // The ring's left/right edges in physical screen pixels. While the chat is open the window spans
    // ring+panel and the ring is flush against the anchored edge; while compact it hugs the ring.
    private (double Left, double Right) RingScreenEdgesPx()
    {
        var (winX, _) = _windowController.GetPosition();
        var (winW, _) = _windowController.GetSize();
        if (!_chatOpen)
            return (winX, winX + winW);

        var ringWidthPx = RingWidthDip * DisplayScale;
        return _chatOnLeft
            ? (winX + winW - ringWidthPx, winX + winW) // ring flush right
            : (winX, winX + ringWidthPx);              // ring flush left
    }

    // True when the chat panel should sit on the ring's left: it doesn't fit on the right and the
    // left has more room. Falls back to the right when the work area is unknown.
    private bool ShouldOpenOnLeft() => PreferLeft(RingScreenEdgesPx());

    private bool PreferLeft((double Left, double Right) ring)
    {
        var wa = _windowController.GetWorkArea();
        if (wa.Width <= 0)
            return false;

        var chatPx = _chatWidth * DisplayScale;
        var rightSpace = (wa.X + wa.Width) - ring.Right;
        var leftSpace = ring.Left - wa.X;

        if (rightSpace >= chatPx)
            return false;
        return leftSpace > rightSpace;
    }

    // The widest the panel may grow on its current side without crossing the screen edge, clamped to
    // the [Min, Max] range. Returns MaxChatWidth when the work area is unknown.
    private double AvailableChatWidthDip()
    {
        var wa = _windowController.GetWorkArea();
        if (wa.Width <= 0)
            return MaxChatWidth;

        var (ringLeft, ringRight) = RingScreenEdgesPx();
        var spacePx = _chatOnLeft ? ringLeft - wa.X : (wa.X + wa.Width) - ringRight;
        return Math.Clamp(spacePx / DisplayScale, MinChatWidth, MaxChatWidth);
    }

    // Place the chat panel on the given side of the ring: swap the star/zero side columns, the
    // panel's column and overlap margin, and mirror the collapse chevron + resize grip.
    private void ApplyChatSide(bool onLeft)
    {
        _chatOnLeft = onLeft;
        if (onLeft)
        {
            LeftSpace.Width = new GridLength(1, GridUnitType.Star);
            RightSpace.Width = new GridLength(0);
            Grid.SetColumn(ChatPanel, 0);
            ChatPanel.Margin = new Thickness(0, 10, -30, 0);
            CollapseButton.Text = IconFont.TablerLine.CaretRight;
            Grid.SetColumn(CollapseButton, 2);
            Grid.SetColumn(ResizeGrip, 0);
        }
        else
        {
            LeftSpace.Width = new GridLength(0);
            RightSpace.Width = new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(ChatPanel, 2);
            ChatPanel.Margin = new Thickness(-30, 10, 0, 0);
            CollapseButton.Text = IconFont.TablerLine.CaretLeft;
            Grid.SetColumn(CollapseButton, 0);
            Grid.SetColumn(ResizeGrip, 2);
        }
    }

    // After the ring moves with the chat open, flip the panel to the other side only if the current
    // side now overflows the screen and the other side has more room. Staying put unless we must
    // avoids twitchy flips when the ring hovers near the boundary. The window is shifted horizontally
    // so the ring stays visually put through the flip.
    private void ReevaluateChatSide()
    {
        if (!_chatOpen || _chatAnimating)
            return;

        var wa = _windowController.GetWorkArea();
        if (wa.Width <= 0)
            return;

        var ring = RingScreenEdgesPx();
        var chatPx = _chatWidth * DisplayScale;
        var rightSpace = (wa.X + wa.Width) - ring.Right;
        var leftSpace = ring.Left - wa.X;

        var currentSpace = _chatOnLeft ? leftSpace : rightSpace;
        var otherSpace = _chatOnLeft ? rightSpace : leftSpace;
        if (currentSpace >= chatPx || otherSpace <= currentSpace)
            return; // current side still fits, or flipping wouldn't help

        var (_, winY) = _windowController.GetPosition();
        var (winW, _) = _windowController.GetSize();

        var wantLeft = !_chatOnLeft;
        ApplyChatSide(wantLeft);

        // Keep the ring's screen rect fixed: same-width window, shifted so the ring lands on its new
        // (flush) edge exactly where it already was.
        var newWinX = wantLeft
            ? ring.Right - winW // ring becomes flush-right: window right edge = old ring right
            : ring.Left;        // ring becomes flush-left:  window left edge  = old ring left
        _windowController.MoveTo((int)Math.Round(newWinX), winY);
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

        // Server/memory commands take free text: fill the "/name " prefix and let the user type the rest.
        if (command.Kind != SlashKind.Action)
            return TryAutocompleteSelectedCommand();

        await ExecuteSlashCommandAsync(command);
        return true;
    }

    private async Task ExecuteSlashCommandAsync(SlashCommand command)
    {
        switch (command.Name)
        {
            case "new":
                StopChatWaitingSpin();
                NewConversation();
                break;

            case "chats":
                ShowConversationList();
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

    // If the text begins with "/server" matching an enabled MCP server, returns that server name and
    // the remaining prompt; otherwise returns (null, original text). Empty prompt falls back to a default.
    private (string? Server, string Prompt) TryParseMcpScope(string text)
    {
        if (!text.StartsWith('/'))
            return (null, text);

        var spaceIndex = text.IndexOf(' ');
        var token = (spaceIndex < 0 ? text[1..] : text[1..spaceIndex]).Trim();

        var server = _allSlashCommands.FirstOrDefault(c =>
            c.Kind == SlashKind.Server && string.Equals(c.Name, token, StringComparison.OrdinalIgnoreCase));
        if (server is null)
            return (null, text);

        var remainder = spaceIndex < 0 ? string.Empty : text[(spaceIndex + 1)..].Trim();
        return (server.Name, remainder.Length == 0 ? "What can you do?" : remainder);
    }

    // If the text begins with "/skill" matching an enabled agent skill, returns that skill and the
    // remaining prompt; otherwise returns (null, original text).
    private (FloatySkill? Skill, string Prompt) TryParseSkillScope(string text)
    {
        if (!text.StartsWith('/'))
            return (null, text);

        var spaceIndex = text.IndexOf(' ');
        var token = (spaceIndex < 0 ? text[1..] : text[1..spaceIndex]).Trim();

        var command = _allSlashCommands.FirstOrDefault(c =>
            c.Kind == SlashKind.Skill && string.Equals(c.Name, token, StringComparison.OrdinalIgnoreCase));
        if (command is null)
            return (null, text);

        var skill = _skillService.GetEnabled(command.Name);
        if (skill is null)
            return (null, text);

        var remainder = spaceIndex < 0 ? string.Empty : text[(spaceIndex + 1)..].Trim();
        return (skill, remainder.Length == 0 ? "What can you do with this skill?" : remainder);
    }

    // Handles /remember and /recall directly (no LLM). Returns true when the text was a memory command.
    private async Task<bool> TryHandleMemoryCommandAsync(string text)
    {
        if (!text.StartsWith('/'))
            return false;

        var spaceIndex = text.IndexOf(' ');
        var token = (spaceIndex < 0 ? text[1..] : text[1..spaceIndex]).Trim();
        var argument = spaceIndex < 0 ? string.Empty : text[(spaceIndex + 1)..].Trim();

        if (string.Equals(token, "remember", StringComparison.OrdinalIgnoreCase))
        {
            // With text: save it. Without text: save the whole conversation so far as one fact.
            string toSave;
            string confirmation;
            if (argument.Length > 0)
            {
                toSave = argument;
                confirmation = $"System: saved to memory — \"{Ellipsize(argument, 80)}\"";
            }
            else
            {
                toSave = BuildConversationTranscript();
                if (string.IsNullOrWhiteSpace(toSave))
                {
                    await ShowToastAsync("Nothing to remember");
                    return true;
                }

                var count = Messages.Count(m => !m.IsSystemNote && !string.IsNullOrWhiteSpace(m.Text));
                confirmation = $"System: saved this conversation to memory ({count} message{(count == 1 ? "" : "s")}).";
            }

            try
            {
                var saved = await _memoryService.RememberTextAsync(toSave);
                Messages.Add(new ChatMessageVm(
                    isUser: false,
                    saved ? confirmation : "System: couldn't save (add your OpenAI API key in Settings).",
                    isSystemNote: true));
                MessagesList.IsVisible = true;
                ScrollToLatest();
                PersistCurrentConversation();
                await ShowToastAsync(saved ? "Saved to memory" : "No API key");
            }
            catch (Exception ex)
            {
                await ShowToastAsync($"⚠️ {ex.Message}");
            }

            return true;
        }

        if (string.Equals(token, "recall", StringComparison.OrdinalIgnoreCase))
        {
            if (argument.Length == 0)
            {
                await ShowToastAsync("Type something to recall");
                return true;
            }

            try
            {
                var results = await _memoryService.SearchCapturesAsync(argument);
                var message = new ChatMessageVm(isUser: false, FormatMemoryResults(argument, results), isSystemNote: true);

                var sources = results
                    .Where(r => !string.IsNullOrWhiteSpace(r.ImagePath) || !string.IsNullOrWhiteSpace(r.TextPath))
                    .Select(r => new MemoryCitation(r.Title, r.ImagePath, r.TextPath, r.CapturedUtc))
                    .ToList();
                if (sources.Count > 0)
                {
                    message.Citations = sources.Select(ToCitationVm).ToList();
                    message.CitationSources = sources;
                }

                Messages.Add(message);
                MessagesList.IsVisible = true;
                ScrollToLatest();
                PersistCurrentConversation();
            }
            catch (Exception ex)
            {
                await ShowToastAsync($"⚠️ {ex.Message}");
            }

            return true;
        }

        return false;
    }

    // Joins the real conversation (excluding Floaty's own notices) into a single transcript to remember.
    private string BuildConversationTranscript() =>
        string.Join("\n\n", Messages
            .Where(m => !m.IsSystemNote && !string.IsNullOrWhiteSpace(m.Text))
            .Select(m => $"{(m.IsUser ? "User" : "Floaty")}: {m.Text}"));

    // --- Conversations (persisted threads, switchable via /chats) ---

    // On first chat open, resume the most recently updated conversation (or begin a fresh one).
    private void EnsureConversationLoaded()
    {
        if (_conversationLoaded)
            return;
        _conversationLoaded = true;

        var recent = _conversationStore.LoadAll().FirstOrDefault();
        if (recent is not null)
        {
            _currentConversation = recent;
            LoadMessagesFrom(recent);
        }
        else
        {
            _currentConversation = new Conversation();
        }
    }

    // Saves the current thread to disk. Skips empty threads (no real user/assistant messages).
    private void PersistCurrentConversation()
    {
        if (_currentConversation is null)
            return;

        var stored = Messages.Select(m => new StoredMessage
        {
            IsUser = m.IsUser,
            Text = m.Text,
            IsSystemNote = m.IsSystemNote,
            Citations = m.CitationSources.Count > 0 ? m.CitationSources.ToList() : null,
        }).ToList();

        if (!stored.Any(m => !m.IsSystemNote && !string.IsNullOrWhiteSpace(m.Text)))
            return;

        if (string.IsNullOrWhiteSpace(_currentConversation.Title))
        {
            var firstUser = Messages.FirstOrDefault(m => m.IsUser && !string.IsNullOrWhiteSpace(m.Text));
            _currentConversation.Title = firstUser is not null ? Ellipsize(firstUser.Text, 40) : "Conversation";
        }

        _currentConversation.Messages = stored;
        _currentConversation.UpdatedUtc = DateTime.UtcNow;
        try
        {
            _conversationStore.Save(_currentConversation);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Conversations] save failed: {ex.Message}");
        }
    }

    private void LoadMessagesFrom(Conversation conversation)
    {
        Messages.Clear();
        foreach (var stored in conversation.Messages)
        {
            var vm = new ChatMessageVm(stored.IsUser, stored.Text, stored.IsSystemNote);
            if (stored.Citations is { Count: > 0 } sources)
            {
                vm.Citations = sources.Select(ToCitationVm).ToList();
                vm.CitationSources = sources;
            }
            Messages.Add(vm);
        }
    }

    // Persist the current thread, then start a fresh empty one.
    private void NewConversation()
    {
        StopChatWaitingSpin();
        PersistCurrentConversation();
        _currentConversation = new Conversation();
        Messages.Clear();
        ExitListMode();
        MessagesList.IsVisible = false;
    }

    // Show the conversation switcher inside the message list.
    private void ShowConversationList()
    {
        EnsureConversationLoaded();
        PersistCurrentConversation();
        BuildConversationItems();
        _listMode = true;
        MessagesList.ItemsSource = _conversationItems;
        MessagesList.IsVisible = true;
        if (_conversationItems.Count > 0)
            MessagesList.ScrollTo(_conversationItems[0], position: ScrollToPosition.Start, animate: false);
    }

    private void BuildConversationItems()
    {
        _conversationItems.Clear();
        _conversationItems.Add(new ConversationItemVm(
            title: "New conversation",
            subtitle: "Start a fresh thread",
            isCurrent: false,
            isNewAction: true,
            openCommand: new Command(NewConversation),
            deleteCommand: null));

        foreach (var c in _conversationStore.LoadAll())
        {
            var id = c.Id;
            var count = c.Messages.Count(m => !m.IsSystemNote && !string.IsNullOrWhiteSpace(m.Text));
            var isCurrent = id == _currentConversation?.Id;
            var subtitle = $"{count} message{(count == 1 ? "" : "s")} · {RelativeTime(c.UpdatedUtc)}{(isCurrent ? " · current" : "")}";

            _conversationItems.Add(new ConversationItemVm(
                title: string.IsNullOrWhiteSpace(c.Title) ? "Conversation" : c.Title,
                subtitle: subtitle,
                isCurrent: isCurrent,
                isNewAction: false,
                openCommand: new Command(() => OpenConversation(id)),
                deleteCommand: new Command(() => DeleteConversation(id))));
        }
    }

    private void OpenConversation(string id)
    {
        if (id == _currentConversation?.Id)
        {
            ExitListMode();
            return;
        }

        PersistCurrentConversation();
        var conversation = _conversationStore.Load(id);
        if (conversation is null)
            return;

        _currentConversation = conversation;
        LoadMessagesFrom(conversation);
        ExitListMode();
    }

    private void DeleteConversation(string id)
    {
        _conversationStore.Delete(id);
        if (id == _currentConversation?.Id)
        {
            _currentConversation = new Conversation();
            Messages.Clear();
        }

        BuildConversationItems(); // refresh the visible list
    }

    private void ExitListMode()
    {
        _listMode = false;
        MessagesList.ItemsSource = Messages;
        MessagesList.IsVisible = Messages.Count > 0;
    }

    private static string RelativeTime(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays}d ago";
        return utc.ToLocalTime().ToString("yyyy-MM-dd");
    }

    // Maps a memory source to a citation with open-commands for whichever of its files exist.
    private CitationVm ToCitationVm(MemoryCitation citation)
    {
        var openImage = string.IsNullOrWhiteSpace(citation.ImagePath)
            ? null
            : new Command(() => _ = OpenSourceAsync(citation.ImagePath!));
        var openText = string.IsNullOrWhiteSpace(citation.TextPath)
            ? null
            : new Command(() => _ = OpenSourceAsync(citation.TextPath!));
        return new CitationVm(citation.Title, openImage, openText);
    }

    // Opens a cited source file (screenshot or text) in the OS default application.
    private async Task OpenSourceAsync(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                await ShowToastAsync("Source not found");
                return;
            }

            await Launcher.Default.OpenAsync(new Microsoft.Maui.ApplicationModel.OpenFileRequest
            {
                File = new Microsoft.Maui.Storage.ReadOnlyFile(path),
            });
        }
        catch (Exception ex)
        {
            await ShowToastAsync($"⚠️ {ex.Message}");
        }
    }

    private static string FormatMemoryResults(string query, IReadOnlyList<CaptureSearchResult> results)
    {
        if (results.Count == 0)
            return $"No matching memories found for \"{query}\".";

        var sb = new StringBuilder();
        sb.Append("Memory results for \"").Append(query).Append("\":");

        var index = 1;
        foreach (var r in results)
        {
            var when = r.CapturedUtc is { } utc ? utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "unknown time";
            sb.AppendLine();
            sb.AppendLine();
            sb.Append('[').Append(index).Append("] ").Append(r.Title).Append(" · ").Append(when);
            sb.AppendLine();
            sb.Append(Ellipsize(r.Content, 400));
            index++;
        }

        return sb.ToString();
    }

    private static string Ellipsize(string text, int max)
    {
        text = text.Trim();
        return text.Length <= max ? text : text[..max] + "…";
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
                        : $"System: capture saved from {result.WindowTitle} (not embedded; no API key).",
                    isSystemNote: true));
                MessagesList.IsVisible = true;
                ScrollToLatest();
                PersistCurrentConversation();
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

        // Tapping a server/memory command fills its "/name " prefix; tapping an action runs it.
        if (command.Kind != SlashKind.Action)
        {
            TryAutocompleteSelectedCommand();
            return;
        }

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

    private void OnRingHandlerChanged(object? sender, EventArgs e)
    {
#if WINDOWS
        if (_ringPlatformView is not null)
        {
            _ringPlatformView.PointerEntered -= OnRingPointerEntered;
            _ringPlatformView.PointerExited -= OnRingPointerExited;
            _ringPlatformView.PointerWheelChanged -= OnRingPointerWheelChanged;
        }

        _ringPlatformView = Ring.Handler?.PlatformView as Microsoft.UI.Xaml.FrameworkElement;
        if (_ringPlatformView is not null)
        {
            _ringPlatformView.PointerEntered += OnRingPointerEntered;
            _ringPlatformView.PointerExited += OnRingPointerExited;
            _ringPlatformView.PointerWheelChanged += OnRingPointerWheelChanged;
        }
#endif
    }

#if WINDOWS
    private void OnRingPointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) =>
        _ringPointerOver = true;

    private void OnRingPointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) =>
        _ringPointerOver = false;

    private void OnRingPointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_ringBusy || !_ringPointerOver || _ringPlatformView is null)
            return;

        var delta = e.GetCurrentPoint(_ringPlatformView).Properties.MouseWheelDelta;
        if (delta == 0)
            return;

        _manualWheelResumeAtUtc = DateTime.UtcNow.AddMilliseconds(ManualWheelResumeDelayMs);

        var rotation = (Ring.Rotation + delta * WheelRotationPerDelta) % 360;
        if (rotation < 0)
            rotation += 360;
        Ring.Rotation = rotation;

        e.Handled = true;
    }
#endif

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

        // Direct memory commands (/remember, /recall) act on memory without calling the LLM.
        if (await TryHandleMemoryCommandAsync(text))
        {
            ChatEntry.Text = string.Empty;
            return;
        }

        ChatEntry.Text = string.Empty;

        // Sending while the conversation switcher is shown returns to the active thread.
        if (_listMode)
            ExitListMode();

        // A leading "/server" routes this turn to that MCP server's tools; "/skill" injects a skill's
        // instructions. The rest of the text is the prompt.
        var (mcpServer, prompt) = TryParseMcpScope(text);
        string? skillInstructions = null;
        if (mcpServer is null)
        {
            var (skill, skillPrompt) = TryParseSkillScope(text);
            if (skill is not null)
            {
                skillInstructions = skill.Instructions;
                prompt = skillPrompt;
            }
        }

        // Build the conversation to send before adding the pending placeholder.
        var history = Messages
            .Select(m => new ChatMessage(m.IsUser ? ChatRole.User : ChatRole.Assistant, m.Text))
            .ToList();
        history.Add(new ChatMessage(ChatRole.User, prompt));

        Messages.Add(new ChatMessageVm(isUser: true, text));
        var pending = new ChatMessageVm(isUser: false, "…");
        Messages.Add(pending);
        MessagesList.IsVisible = true;
        ScrollToLatest();
        StartChatWaitingSpin();

        // Sources the model retrieves this turn (filled by the search_captures tool), shown as citations.
        var citations = new List<MemoryCitation>();

        try
        {
            var streamed = new StringBuilder();
            var repaint = Stopwatch.StartNew();
            var lastScrollMs = 0L;

            await foreach (var chunk in _chatService.GetStreamingResponseAsync(history, mcpServer, citations, skillInstructions))
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

        if (citations.Count > 0)
        {
            pending.Citations = citations.Select(ToCitationVm).ToList();
            pending.CitationSources = citations.ToList();
        }

        ScrollToLatest();
        PersistCurrentConversation();
    }

    private void ScrollToLatest()
    {
        if (Messages.Count > 0)
            MessagesList.ScrollTo(Messages[^1], position: ScrollToPosition.End, animate: true);
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

	private void OnRingTapped(object sender, TappedEventArgs e)
	{
		OnOpenChatClicked(sender, e);
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
}
