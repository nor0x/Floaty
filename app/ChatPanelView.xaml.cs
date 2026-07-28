using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using AsyncAwaitBestPractices;
using Floaty.Services;
using Microsoft.Extensions.AI;

namespace Floaty;

/// <summary>
/// The chat surface: message list, conversation switcher, slash/@ pickers, exec approval and the input
/// row. It owns everything about the conversation and nothing about windows — whenever it needs to grow,
/// move or close it asks its <see cref="IChatPanelHost"/>, so the same view works both glued to the ring
/// (<see cref="ChatPanelPlacement.Floating"/>) and in its own window (<see cref="ChatPanelPlacement.Fixed"/>).
/// </summary>
public partial class ChatPanelView : ContentView
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

    // A window the user attached to the pending prompt via @. The capture starts the moment the
    // window is picked; the send path awaits it so what you saw when tagging is what gets sent.
    private sealed class PromptAttachmentVm
    {
        public nint Hwnd { get; init; }
        public string Title { get; init; } = string.Empty;
        public Task<CaptureResult?>? CaptureTask { get; set; }
        public Command? RemoveCommand { get; set; }
    }

    private readonly SettingsService _settings;
    private readonly IChatService _chatService;
    private readonly IScreenCaptureService _captureService;
    private readonly IMemoryService _memoryService;
    private readonly ConversationService _conversationStore;
    private readonly SkillService _skillService;
    private readonly IVoiceInputService _voiceInput;
    private readonly IServiceProvider _services;

    // Set by Attach() before the panel is shown; every window operation goes through it.
    private IChatPanelHost _host = NullChatPanelHost.Instance;

    // True while the mic pulse animation loop should keep running (set on start/stop listening).
    private bool _micPulsing;

    // The conversation currently shown in Messages; created lazily on first chat open (resume most recent).
    private Conversation? _currentConversation;
    private bool _conversationLoaded;

    // Conversation switcher (shown inside MessagesList under /chats).
    private bool _listMode;
    private readonly ObservableCollection<ConversationItemVm> _conversationItems = new();

    // Which side of the ring the panel occupies (floating placement only; a panel with its own window
    // always uses the "right of the ring" layout). Drives the collapse chevron and the corner grip.
    private bool _onLeft;

    // User-adjustable chat panel width (dragged via the corner grip), clamped to this range.
    public const double DefaultChatWidth = 360;
    public const double MinChatWidth = 300;
    public const double MaxChatWidth = 680;
    private double _chatWidth = DefaultChatWidth;
    private double _resizeStartWidth;

    // User-adjustable messages-list height (the corner grip's vertical axis). Null until the user
    // drags vertically — the lists then keep their XAML default (content-driven, max 240). The value
    // lives on the lists themselves so the existing SizeChanged → window-resize pipeline follows it.
    public const double MinChatListHeight = 80;
    public const double MaxChatListHeight = 800; // fallback ceiling when the work area is unknown
    private const double DefaultListMaxHeight = 240; // mirrors the lists' XAML MaximumHeightRequest
    private double? _userListHeight;
    private double _resizeStartListHeight;
    private double _resizeStartChromeDip;

    // Panel height fallback before the first real measurement (matches the old ChatBaseHeight + 80).
    private const double InitialPanelHeight = 80;

    // Last height we reported to the host, to avoid redundant resizes / oscillation.
    private double _lastPanelHeight;

    // Cumulative pan offset reported on the previous drag-bar PanUpdated, used to derive per-frame deltas.
    private double _lastDragTotalX;
    private double _lastDragTotalY;

    private bool _waitingForFirstChunk;

    private const double InlineToastHeightDip = 28;
    private const uint InlineToastInMs = 170;
    private const uint InlineToastOutMs = 180;
    private const int InlineToastHoldMs = 1600;
    private int _inlineToastVersion;

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
    private int _conversationSelectedIndex = -1;
    private bool _updatingConversationSelection;

    // @-mention window picker. The open-window list is enumerated once per popup opening
    // (invalidated on hide) and filtered per keystroke.
    private const int MaxWindowQueryLength = 40; // longer text after @ is prose, not a filter
    private const int MaxAttachmentChars = 12_000; // cap per-attachment text sent to the model
    private readonly ObservableCollection<PromptAttachmentVm> _attachments = new();
    private readonly ObservableCollection<WindowInfo> _filteredWindows = new();
    private IReadOnlyList<WindowInfo> _windowCache = Array.Empty<WindowInfo>();
    private bool _windowCacheValid;
    private bool _windowCacheLoading;
    private bool _windowSuggestionsVisible;
    private int _windowSelectedIndex = -1;
    private bool _updatingWindowSelection;
    private int _atTokenIndex = -1;          // index of the '@' driving the popup
    private int _dismissedAtTokenIndex = -1; // Escape'd '@': stay hidden until its position changes

#if WINDOWS
    private Microsoft.UI.Xaml.Controls.TextBox? _chatEntryTextBox;

    // Shared brush behind the WinUI theme overrides (Entry focus underline, list selection
    // indicators); mutated in ApplyAccentColor so already-rendered controls recolor live.
    private readonly Microsoft.UI.Xaml.Media.SolidColorBrush _winAccentBrush = new();
#endif

    /// <summary>True while the panel is open (visible and accepting input).</summary>
    public bool IsOpen { get; private set; }

    /// <summary>Current panel width in device-independent units, as set by the corner grip.</summary>
    public double PanelWidth => _chatWidth;

    /// <summary>Panel height last reported to the host, or a sensible starting height before first measure.</summary>
    public double PanelHeightOrDefault => _lastPanelHeight > 0 ? _lastPanelHeight : InitialPanelHeight;

    public ObservableCollection<ChatMessageVm> Messages { get; } = new();

    public ChatPanelView(
        SettingsService settings,
        IChatService chatService,
        IScreenCaptureService captureService,
        IMemoryService memoryService,
        ConversationService conversationStore,
        SkillService skillService,
        IVoiceInputService voiceInput,
        IServiceProvider services)
    {
        InitializeComponent();
        _settings = settings;
        _chatService = chatService;
        _captureService = captureService;
        _memoryService = memoryService;
        _conversationStore = conversationStore;
        _skillService = skillService;
        _voiceInput = voiceInput;
        _services = services;

        MessagesList.ItemsSource = Messages;
        SlashSuggestionsList.ItemsSource = _filteredSlashCommands;
        WindowSuggestionsList.ItemsSource = _filteredWindows;
        BindableLayout.SetItemsSource(AttachmentChipsPanel, _attachments);
        ChatEntry.HandlerChanged += OnChatEntryHandlerChanged;
        MessagesList.HandlerChanged += OnListHandlerChanged;
        MessagesList.SizeChanged += OnMessagesListSizeChanged;
        SlashSuggestionsList.HandlerChanged += OnListHandlerChanged;
        WindowSuggestionsList.HandlerChanged += OnListHandlerChanged;
        ResizeCornerGrip.HandlerChanged += OnResizeGripHandlerChanged;
        SizeChanged += OnPanelSizeChanged;

        _settings.Changed += OnSettingsChanged;
        _settings.AccentColorPreviewRequested += OnAccentColorPreviewRequested;
        _voiceInput.SegmentTranscribed += OnVoiceSegment;
        _voiceInput.PauseElapsed += OnVoicePause;
        _voiceInput.Error += OnVoiceError;

        ApplyAccentColor(_settings.Current.AccentColor);
        ApplyPanelSide(onLeft: false);
        RebuildSlashCommands();
        UpdateMicVisibility();
    }

    /// <summary>
    /// Binds the panel to the window that hosts it. Called by the host right after construction, before
    /// the panel is shown; <paramref name="startWidth"/> restores a previously dragged width.
    /// </summary>
    public void Attach(IChatPanelHost host, double startWidth = DefaultChatWidth)
    {
        _host = host;
        _chatWidth = Math.Clamp(startWidth, MinChatWidth, MaxChatWidth);
    }

    /// <summary>
    /// Unhooks the shared singleton services this panel listens to. Called when the host is torn down
    /// (placement change / window close) so a replacement panel doesn't double-handle their events.
    /// </summary>
    public void Detach()
    {
        _settings.Changed -= OnSettingsChanged;
        _settings.AccentColorPreviewRequested -= OnAccentColorPreviewRequested;
        _voiceInput.SegmentTranscribed -= OnVoiceSegment;
        _voiceInput.PauseElapsed -= OnVoicePause;
        _voiceInput.Error -= OnVoiceError;

        if (_voiceInput.IsListening)
            _ = _voiceInput.StopAsync();

        PersistCurrentConversation();
        _host = NullChatPanelHost.Instance;
    }

    // --- Open / close, driven by the host ---

    /// <summary>
    /// Makes the panel ready to be shown: resumes the most recent conversation on first open and sizes
    /// the messages area. The host resizes its window before calling <see cref="AnimateInAsync"/>.
    /// </summary>
    public void BeginOpen()
    {
        EnsureConversationLoaded();
        IsOpen = true;
        MessagesList.IsVisible = !_listMode && Messages.Count > 0;
        _lastPanelHeight = 0;
        IsVisible = true;
    }

    /// <summary>Slides the panel up into view and focuses the input.</summary>
    public async Task AnimateInAsync()
    {
        TranslationY = 24;
        Opacity = 0;
        await Task.WhenAll(
            this.TranslateToAsync(0, 0, 220, Easing.SinOut),
            this.FadeToAsync(1, 220));

        ChatEntry.Focus();
    }

    /// <summary>
    /// Tears down the transient chat state before the host hides the panel: popups, the conversation
    /// switcher and the microphone all close, and the thread is written to disk.
    /// </summary>
    public void BeginClose()
    {
        HideSlashSuggestions();
        HideWindowSuggestions();
        HideInlineToast(immediate: true);
        if (_listMode)
            ExitListMode();
        // A collapsed panel must never keep the microphone hot.
        if (_voiceInput.IsListening)
            _ = StopListeningAsync();
        PersistCurrentConversation();
        IsOpen = false;
        ChatEntry.Unfocus();
    }

    /// <summary>Fades the panel out; the host animates its window at the same time.</summary>
    public Task FadeOutAsync() => this.FadeToAsync(0, 180);

    /// <summary>Resets the panel to its hidden resting state once the host's close animation finishes.</summary>
    public void EndClose()
    {
        HideInlineToast(immediate: true);
        IsVisible = false;
        Opacity = 1;
        TranslationY = 0;
        _lastPanelHeight = 0;
    }

    /// <summary>Focuses the input without reopening (used when an already-open panel is re-summoned).</summary>
    public void FocusEntry() => ChatEntry.Focus();

    /// <summary>Shows the drag handle, for hosts where there is no ring to move the panel by.</summary>
    public void SetDragBarVisible(bool visible) => DragBar.IsVisible = visible;

    /// <summary>
    /// Whether the point (window-client DIPs) lands on the panel. The corner grip overhangs the panel's
    /// outer top corner via negative margins, so it is tested separately.
    /// </summary>
    public bool IsInteractiveAt(double x, double y)
    {
        if (!IsVisible)
            return false;

        return this.BoundsInPage().Contains(x, y)
            || ResizeCornerGrip.BoundsInPage().Contains(x, y);
    }

    /// <summary>
    /// Places the panel on the given side of the ring: mirrors the collapse chevron and the corner resize
    /// grip. The grip hugs the panel's outer top corner; its glyph is drawn for the top-right corner and
    /// mirrored via ScaleX when the panel sits on the left. The negative margins reach into the panel's
    /// padding (14 left / 8 right) so the grip sits visually on the corner.
    /// </summary>
    public void ApplyPanelSide(bool onLeft)
    {
        _onLeft = onLeft;
        if (onLeft)
        {
            CollapseButton.Text = IconFont.TablerLine.CaretRight;
			CollapseButton.Margin = new Thickness(0, 0, 6, 0);
            Grid.SetColumn(CollapseButton, 2);
            ResizeCornerGrip.HorizontalOptions = LayoutOptions.Start;
            ResizeCornerGrip.Margin = new Thickness(-14, -8, 0, 0);
			ResizeCornerGlyph.Text = IconFont.TablerLine.RadiusTopLeft;

		}
		else
        {
            CollapseButton.Text = IconFont.TablerLine.CaretLeft;
            CollapseButton.Margin = new Thickness(6, 0, 0, 0);
            Grid.SetColumn(CollapseButton, 0);
            ResizeCornerGrip.HorizontalOptions = LayoutOptions.End;
            ResizeCornerGrip.Margin = new Thickness(0, -8, -8, 0);
            ResizeCornerGlyph.Text = IconFont.TablerLine.RadiusTopRight;

		}

        ApplyResizeGripCursor();
    }

    // --- Settings / accent ---

    private void OnSettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.Dispatch(() =>
        {
            ApplyAccentColor(_settings.Current.AccentColor);
            RebuildSlashCommands();
            UpdateMicVisibility();
        });

    // Live preview from the Appearance accent picker: apply without persisting (the settings page
    // reverts to the saved value when it closes without a Save).
    private void OnAccentColorPreviewRequested(object? sender, string hex) =>
        Dispatcher.Dispatch(() => ApplyAccentColor(hex));

    // Recolor the accent surfaces this panel owns: user chat bubbles via the shared static + per-message
    // refresh, and the WinUI theme brushes behind the entry underline / list selection pill. The
    // AccentColor / AccentIconOnDarkColor DynamicResources themselves are application-wide and are
    // updated by OverlayPage.
    private void ApplyAccentColor(string? hex)
    {
        var palette = AccentPalette.From(hex);
        ChatMessageVm.UserBubbleColor = Color.FromArgb(palette.Base);
        foreach (var message in Messages)
            message.RefreshBubbleColor();

#if WINDOWS
        _winAccentBrush.Color = Microsoft.Maui.Platform.ColorExtensions.ToWindowsColor(Color.FromArgb(palette.Base));
#endif
    }

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

    private void OnCollapseChatClicked(object? sender, EventArgs e) => _host.CollapseRequested();

    // --- Voice input ---

    // The mic button only shows while a downloaded speech-to-text model is selected in settings
    // (and the platform can capture audio). Re-evaluated on every settings change.
    private void UpdateMicVisibility()
    {
        MicButton.IsVisible = _voiceInput.IsConfigured;
        if (!MicButton.IsVisible && _voiceInput.IsListening)
            _ = StopListeningAsync();
    }

    private async void OnMicClicked(object? sender, EventArgs e)
    {
        if (_voiceInput.IsListening)
        {
            await StopListeningAsync();
            return;
        }

        // Disabled while the model loads — first start can take seconds for the larger models.
        MicButton.IsEnabled = false;
        try
        {
            await _voiceInput.StartAsync();
            ApplyMicListeningVisuals(true);
        }
        catch (Exception ex)
        {
            await ShowInlineToastAsync($"⚠️ {ex.Message}");
        }
        finally
        {
            MicButton.IsEnabled = true;
        }
    }

    private async Task StopListeningAsync()
    {
        ApplyMicListeningVisuals(false);
        try
        {
            await _voiceInput.StopAsync();
        }
        catch (Exception ex)
        {
            await ShowInlineToastAsync($"⚠️ {ex.Message}");
        }
    }

    // Listening: record glyph on the accent color with a soft opacity pulse; idle: plain mic on
    // the neutral input-row background.
    private void ApplyMicListeningVisuals(bool listening)
    {
        if (listening)
        {
            MicButton.Text = IconFont.TablerLine.PlayerRecord;
            MicButton.SetDynamicResource(VisualElement.BackgroundColorProperty, "AccentColor");
            if (!_micPulsing)
            {
                _micPulsing = true;
                _ = PulseMicAsync();
            }
        }
        else
        {
            _micPulsing = false;
            MicButton.Text = IconFont.TablerLine.Microphone;
            MicButton.BackgroundColor = Color.FromArgb("#33FFFFFF");
        }
    }

    private async Task PulseMicAsync()
    {
        while (_micPulsing)
        {
            await MicButton.FadeToAsync(0.55, 500, Easing.SinInOut);
            await MicButton.FadeToAsync(1.0, 500, Easing.SinInOut);
        }
        MicButton.Opacity = 1;
    }

    // A finished speech segment: append to the entry like the user typed it. Deliberately not
    // wrapped in _suppressEntryTextChanged — dictated text is user content, so slash/@ popup
    // parsing should behave exactly as it does for typing.
    private void OnVoiceSegment(object? sender, string text) =>
        Dispatcher.Dispatch(() =>
        {
            var existing = ChatEntry.Text ?? string.Empty;
            ChatEntry.Text = existing.Length == 0 ? text : $"{existing.TrimEnd()} {text}";
            ChatEntry.CursorPosition = ChatEntry.Text.Length;
        });

    // Long silence after speech: in auto-send mode this sends through the normal send path
    // (slash/@ handling included). Listening always stops first so the mic never transcribes
    // while the reply streams.
    private void OnVoicePause(object? sender, EventArgs e) =>
        Dispatcher.Dispatch(async () =>
        {
            if (!_voiceInput.IsListening
                || _settings.Current.VoiceSendMode != VoiceSendMode.AutoSendOnPause
                || string.IsNullOrWhiteSpace(ChatEntry.Text))
                return;

            await StopListeningAsync();
            OnSendClicked(MicButton, EventArgs.Empty);
        });

    private void OnVoiceError(object? sender, string message) =>
        Dispatcher.Dispatch(async () =>
        {
            ApplyMicListeningVisuals(false);
            await ShowInlineToastAsync($"⚠️ {message}");
        });

    // --- Sizing ---

    // Ask the host to grow (or shrink) its window to match the panel's content height. The panel hugs its
    // content — collapsed messages area when empty, expanding up to MessagesList's MaximumHeightRequest
    // (after which the CollectionView scrolls), so the window tracks it without leaving dead space.
    private void OnPanelSizeChanged(object? sender, EventArgs e)
    {
        if (!IsOpen || Height <= 0)
            return;

        if (Math.Abs(Height - _lastPanelHeight) < 1)
            return;

        _lastPanelHeight = Height;
        _host.RequestPanelSize(_chatWidth, Height);
    }

    // Keep assistant bubbles at 80% and user bubbles capped at 60% of the chat panel: recompute from
    // the message list's measured width whenever the panel is resized (corner grip), then reflow the
    // bubbles already on screen. New bubbles pick the values up at bind time via ChatMessageVm.
    private void OnMessagesListSizeChanged(object? sender, EventArgs e)
    {
        if (MessagesList.Width <= 0)
            return;

        var assistantWidth = Math.Round(MessagesList.Width * 0.8);
        var userMaxWidth = Math.Round(MessagesList.Width * 0.6);
        if (Math.Abs(assistantWidth - ChatMessageVm.AssistantBubbleWidth) < 1
            && Math.Abs(userMaxWidth - ChatMessageVm.UserBubbleMaxWidth) < 1)
            return;

        ChatMessageVm.AssistantBubbleWidth = assistantWidth;
        ChatMessageVm.UserBubbleMaxWidth = userMaxWidth;
        foreach (var message in Messages)
            message.RefreshBubbleWidth();
    }

    // Drag the panel's outer top corner to resize it in both axes. Width: the window grows away from the
    // ring (the host anchors the ring's edge); on the left side the grip is on the panel's left, so
    // dragging left (negative X) widens it. Height: the grip is on top and the window's bottom edge is
    // anchored, so dragging up (negative Y) grows it — the drag sets a user height on the lists, the panel
    // grows, and OnPanelSizeChanged asks the host to follow. Both axes are clamped to the space available.
    private void OnResizeCornerPanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                // The grip drag routinely leaves the grip's small hit-rect; stay input-opaque until release.
                _host.SetForceInteractive(true);
                _resizeStartWidth = _chatWidth;
                var measuredList = MeasuredListHeightDip();
                _resizeStartListHeight = measuredList > 0 ? measuredList : _userListHeight ?? DefaultListMaxHeight;
                // Fixed chrome (input row, padding, suggestions…) around the list, captured once so
                // it isn't re-measured mid-drag while the layout is in flux.
                _resizeStartChromeDip = Math.Max(0, Height - measuredList);
                break;

            case GestureStatus.Running:
                var widthDelta = _onLeft ? -e.TotalX : e.TotalX;
                var heightDelta = -e.TotalY;
                _chatWidth = Math.Clamp(_resizeStartWidth + widthDelta, MinChatWidth, _host.AvailableWidthDip());
                var listHeight = Math.Clamp(_resizeStartListHeight + heightDelta,
                    MinChatListHeight, _host.AvailableListHeightDip(_resizeStartChromeDip));
                ApplyUserListHeight(listHeight);
                _host.RequestPanelSize(_chatWidth, PanelHeightOrDefault);
                break;

            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                _host.SetForceInteractive(false);
                break;
        }
    }

    // Drag the panel's own window by its handle (fixed placement). Mirrors the ring's pan handling:
    // per-frame deltas derived from the cumulative totals, input-opaque for the whole gesture.
    private void OnDragBarPanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _lastDragTotalX = 0;
                _lastDragTotalY = 0;
                _host.SetForceInteractive(true);
                break;

            case GestureStatus.Running:
                var dx = e.TotalX - _lastDragTotalX;
                var dy = e.TotalY - _lastDragTotalY;
                _lastDragTotalX = e.TotalX;
                _lastDragTotalY = e.TotalY;
                _host.MoveWindowBy(dx, dy);
                break;

            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                _host.SetForceInteractive(false);
                break;
        }
    }

    // Measured height of whichever list is currently visible, or 0 when the chat is empty (both
    // lists collapsed). Used as the drag baseline so the grip tracks the pointer from the real size.
    private double MeasuredListHeightDip()
    {
        if (MessagesList.IsVisible && MessagesList.Height > 0)
            return MessagesList.Height;
        if (ConversationList.IsVisible && ConversationList.Height > 0)
            return ConversationList.Height;
        return 0;
    }

    // Apply a user-dragged list height to both lists (so the /chats switcher matches the messages
    // view). Both HeightRequest and MaximumHeightRequest are set — the XAML max of 240 would
    // otherwise cap the request, and the fixed HeightRequest makes the drag track the pointer even
    // when the content is shorter than the requested height.
    private void ApplyUserListHeight(double heightDip)
    {
        _userListHeight = heightDip;
        MessagesList.MaximumHeightRequest = heightDip;
        MessagesList.HeightRequest = heightDip;
        ConversationList.MaximumHeightRequest = heightDip;
        ConversationList.HeightRequest = heightDip;
    }

    // --- Input parsing: slash commands and @-window mentions ---

    private void OnChatEntryTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressEntryTextChanged)
            return;

        UpdateSlashSuggestions(e.NewTextValue);
        UpdateWindowSuggestions(e.NewTextValue);
    }

    // The caret from the platform TextBox on Windows: MAUI's CursorPosition lags behind during
    // TextChanged there, and @-parsing needs the position the user is actually typing at.
    private int GetChatEntryCaretIndex(string text)
    {
#if WINDOWS
        var caret = _chatEntryTextBox?.SelectionStart ?? text.Length;
#else
        var caret = ChatEntry.CursorPosition;
#endif
        return Math.Clamp(caret, 0, text.Length);
    }

    // Show the open-window picker while the caret sits in an "@query" token (start of text or
    // preceded by whitespace, so emails like user@host never trigger it).
    private void UpdateWindowSuggestions(string? text)
    {
        if (!IsOpen || string.IsNullOrEmpty(text) || _slashSuggestionsVisible)
        {
            HideWindowSuggestions();
            return;
        }

        var caret = GetChatEntryCaretIndex(text);
        var atIndex = caret > 0 ? text.LastIndexOf('@', caret - 1) : -1;
        if (atIndex < 0 || (atIndex > 0 && !char.IsWhiteSpace(text[atIndex - 1])))
        {
            HideWindowSuggestions();
            return;
        }

        // Escape dismissed the popup for this '@'; stay hidden until the token moves.
        if (atIndex == _dismissedAtTokenIndex)
        {
            HideWindowSuggestions();
            return;
        }
        _dismissedAtTokenIndex = -1;

        var query = text[(atIndex + 1)..caret];
        if (query.Length > MaxWindowQueryLength || query.Contains('\n'))
        {
            HideWindowSuggestions();
            return;
        }

        _atTokenIndex = atIndex;

        // First keystroke of a popup session: enumerate windows fresh, then filter once the list
        // lands (LoadWindowCacheAsync re-enters this method).
        if (!_windowCacheValid)
        {
            if (!_windowCacheLoading)
            {
                _windowCacheLoading = true;
                _ = LoadWindowCacheAsync();
            }
            return;
        }

        FilterWindowSuggestions(query);
    }

    private async Task LoadWindowCacheAsync()
    {
        try
        {
            _windowCache = await _captureService.ListWindowsAsync();
        }
        catch
        {
            _windowCache = Array.Empty<WindowInfo>();
        }

        _windowCacheValid = true;
        _windowCacheLoading = false;
        UpdateWindowSuggestions(ChatEntry.Text);
    }

    private void FilterWindowSuggestions(string query)
    {
        var matches = _windowCache
            .Where(window => _attachments.All(a => a.Hwnd != window.Hwnd))
            .Where(window => query.Length == 0
                || window.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || window.ProcessName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
        {
            HideWindowSuggestions();
            return;
        }

        _filteredWindows.Clear();
        foreach (var window in matches)
            _filteredWindows.Add(window);

        _windowSuggestionsVisible = true;
        WindowSuggestionsPanel.IsVisible = true;
        SetWindowSelection(0);
    }

    private void HideWindowSuggestions()
    {
        _windowSuggestionsVisible = false;
        _windowSelectedIndex = -1;
        _atTokenIndex = -1;
        _windowCacheValid = false; // re-enumerate next time the popup opens
        WindowSuggestionsPanel.IsVisible = false;

        _updatingWindowSelection = true;
        try
        {
            WindowSuggestionsList.SelectedItem = null;
        }
        finally
        {
            _updatingWindowSelection = false;
        }

        _filteredWindows.Clear();
    }

    private void SetWindowSelection(int index)
    {
        if (_filteredWindows.Count == 0)
        {
            _windowSelectedIndex = -1;
            return;
        }

        _windowSelectedIndex = Math.Clamp(index, 0, _filteredWindows.Count - 1);
        _updatingWindowSelection = true;
        try
        {
            var selected = _filteredWindows[_windowSelectedIndex];
            WindowSuggestionsList.SelectedItem = selected;
            WindowSuggestionsList.ScrollTo(selected, position: ScrollToPosition.MakeVisible, animate: true);
        }
        finally
        {
            _updatingWindowSelection = false;
        }
    }

    private void MoveWindowSelection(int delta)
    {
        var count = _filteredWindows.Count;
        if (count == 0)
            return;

        var start = _windowSelectedIndex < 0 ? 0 : _windowSelectedIndex;
        var next = ((start + delta) % count + count) % count; // wrap-around
        SetWindowSelection(next);
    }

    private bool TryAttachSelectedWindow()
    {
        if (!_windowSuggestionsVisible)
            return false;
        if (_windowSelectedIndex < 0 || _windowSelectedIndex >= _filteredWindows.Count)
            return false;

        AttachWindow(_filteredWindows[_windowSelectedIndex]);
        return true;
    }

    private void AttachWindow(WindowInfo window)
    {
        // Remove the "@query" token from the entry and put the caret where it was.
        var text = ChatEntry.Text ?? string.Empty;
        var caret = GetChatEntryCaretIndex(text);
        var atIndex = _atTokenIndex;

        HideWindowSuggestions();

        if (atIndex >= 0 && atIndex < text.Length)
        {
            var removeLength = Math.Min(Math.Max(1, caret - atIndex), text.Length - atIndex);
            var newText = text.Remove(atIndex, removeLength);
            _suppressEntryTextChanged = true;
            ChatEntry.Text = newText;
            _suppressEntryTextChanged = false;
            ChatEntry.CursorPosition = Math.Min(atIndex, newText.Length);
        }

        if (_attachments.Any(a => a.Hwnd == window.Hwnd))
        {
            _ = ShowInlineToastAsync("Window already attached");
            return;
        }

        // Capture right away (downscaled like auto-history captures) so what the user saw when
        // tagging is what gets sent, even if the window closes before send.
        var vm = new PromptAttachmentVm { Hwnd = window.Hwnd, Title = window.Title };
        vm.RemoveCommand = new Command(() => RemoveAttachment(vm));
        vm.CaptureTask = _captureService.CaptureWindowAsync(window.Hwnd, includeScreenshot: true);
        _attachments.Add(vm);
        AttachmentChipsPanel.IsVisible = true;

        _ = FinishAttachmentAsync(vm);
    }

    private void RemoveAttachment(PromptAttachmentVm vm)
    {
        _attachments.Remove(vm);
        AttachmentChipsPanel.IsVisible = _attachments.Count > 0;
    }

    private async Task FinishAttachmentAsync(PromptAttachmentVm vm)
    {
        CaptureResult? result = null;
        try
        {
            result = vm.CaptureTask is null ? null : await vm.CaptureTask;
        }
        catch
        {
            // fall through: treated as a failed capture below
        }

        if (result is null)
        {
            // Only toast if the chip is still pending (the user may have removed it already).
            if (_attachments.Remove(vm))
            {
                AttachmentChipsPanel.IsVisible = _attachments.Count > 0;
                await ShowInlineToastAsync($"Couldn't capture {vm.Title}");
            }
            return;
        }

        if (_settings.Current.RememberTaggedCaptures)
        {
            try
            {
                await _memoryService.RememberCaptureAsync(result, IMemoryService.TaggedCaptureSource);
            }
            catch
            {
                // Memory persistence is best-effort; the attachment still rides on the prompt.
            }
        }
    }

    private void OnWindowSuggestionsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingWindowSelection)
            return;

        if (e.CurrentSelection.FirstOrDefault() is not WindowInfo window)
            return;

        _windowSelectedIndex = _filteredWindows.IndexOf(window);
        AttachWindow(window);
    }

    private void UpdateSlashSuggestions(string? text)
    {
        if (!IsOpen || string.IsNullOrEmpty(text) || !text.StartsWith("/", StringComparison.Ordinal))
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

    private void MoveSlashSelection(int delta)
    {
        var count = _filteredSlashCommands.Count;
        if (count == 0)
            return;

        var start = _slashSelectedIndex < 0 ? 0 : _slashSelectedIndex;
        var next = ((start + delta) % count + count) % count; // wrap-around
        SetSlashSelection(next);
    }

    private void SetConversationSelection(int index)
    {
        if (_conversationItems.Count == 0)
        {
            _conversationSelectedIndex = -1;
            return;
        }

        _conversationSelectedIndex = Math.Clamp(index, 0, _conversationItems.Count - 1);
        _updatingConversationSelection = true;
        try
        {
            var selected = _conversationItems[_conversationSelectedIndex];
            ConversationList.SelectedItem = selected;
            ConversationList.ScrollTo(selected, position: ScrollToPosition.MakeVisible, animate: true);
        }
        finally
        {
            _updatingConversationSelection = false;
        }
    }

    private void MoveConversationSelection(int delta)
    {
        var count = _conversationItems.Count;
        if (count == 0)
            return;

        var start = _conversationSelectedIndex < 0 ? 0 : _conversationSelectedIndex;
        var next = ((start + delta) % count + count) % count; // wrap-around
        SetConversationSelection(next);
    }

    private void OpenSelectedConversation()
    {
        if (_conversationSelectedIndex < 0 || _conversationSelectedIndex >= _conversationItems.Count)
            return;

        // Reuse each row's existing OpenCommand (NewConversation or OpenConversation(id)),
        // both of which call ExitListMode() themselves.
        _conversationItems[_conversationSelectedIndex].OpenCommand.Execute(null);
    }

    private void OnConversationSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingConversationSelection)
            return;

        if (e.CurrentSelection.FirstOrDefault() is not ConversationItemVm item)
            return;

        _conversationSelectedIndex = _conversationItems.IndexOf(item);
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
                _host.SetBusy(false);
                NewConversation();
                break;

            case "chats":
                ShowConversationList();
                break;

            case "capture":
                await CaptureAndRememberAsync(addSystemNote: true);
                break;

            case "settings":
                SettingsPage.OpenWindow(_services);
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
                    await ShowInlineToastAsync("Nothing to remember");
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
                await ShowInlineToastAsync(saved ? "Saved to memory" : "No API key");
            }
            catch (Exception ex)
            {
                await ShowInlineToastAsync($"⚠️ {ex.Message}");
            }

            return true;
        }

        if (string.Equals(token, "recall", StringComparison.OrdinalIgnoreCase))
        {
            if (argument.Length == 0)
            {
                await ShowInlineToastAsync("Type something to recall");
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
                await ShowInlineToastAsync($"⚠️ {ex.Message}");
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
        _host.SetBusy(false);
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
        ConversationList.ItemsSource = _conversationItems;
        MessagesList.IsVisible = false;
        ConversationList.IsVisible = true;
        SetConversationSelection(0);   // "New conversation" row is index 0
        ChatEntry.Focus();             // ensure Up/Down/Enter reach OnChatEntryTextBoxKeyDown
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
        ConversationList.IsVisible = false;
        _conversationSelectedIndex = -1;
        _updatingConversationSelection = true;
        try
        {
            ConversationList.SelectedItem = null;
        }
        finally
        {
            _updatingConversationSelection = false;
        }
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
                await ShowInlineToastAsync("Source not found");
                return;
            }

            await Launcher.Default.OpenAsync(new Microsoft.Maui.ApplicationModel.OpenFileRequest
            {
                File = new Microsoft.Maui.Storage.ReadOnlyFile(path),
            });
        }
        catch (Exception ex)
        {
            await ShowInlineToastAsync($"⚠️ {ex.Message}");
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
                await ShowInlineToastAsync("Unable to open config folder");
        }
        catch (Exception ex)
        {
            await ShowInlineToastAsync($"⚠️ {ex.Message}");
        }
    }

    /// <summary>
    /// Captures the window behind the overlay and saves it to memory. Public so the ring's own
    /// <c>/capture</c> affordances can reuse it.
    /// </summary>
    public async Task CaptureAndRememberAsync(bool addSystemNote)
    {
        try
        {
            var result = await _captureService.CaptureUnderlyingWindowAsync();
            if (result is null)
            {
                await ShowInlineToastAsync("Nothing to capture");
                return;
            }

            var stored = await _memoryService.RememberCaptureAsync(result);
            await ShowInlineToastAsync($"Saved ✓ — {result.WindowTitle}{(stored ? " · embedded" : " · no API key")}");

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
            await ShowInlineToastAsync($"⚠️ {ex.Message}");
        }
    }

    // Inline status toast under the input row. Height + opacity are animated together so the panel
    // grows/shrinks smoothly instead of snapping when short status messages appear.
    private async Task ShowInlineToastAsync(string message)
    {
        var version = ++_inlineToastVersion;
        InlineToastLabel.Text = message;

        InlineToastPanel.AbortAnimation(nameof(InlineToastPanel));
        InlineToastPanel.IsVisible = true;

        var wasHidden = InlineToastPanel.Opacity <= 0.01 || InlineToastPanel.HeightRequest <= 0.5;
        if (wasHidden)
        {
            InlineToastPanel.Opacity = 0;
            InlineToastPanel.HeightRequest = 0;
            InlineToastPanel.TranslationY = -4;

            await Task.WhenAll(
                InlineToastPanel.FadeToAsync(1, InlineToastInMs, Easing.CubicOut),
                InlineToastPanel.TranslateToAsync(0, 0, InlineToastInMs, Easing.CubicOut),
                AnimateInlineToastHeightAsync(InlineToastHeightDip, InlineToastInMs, Easing.CubicOut));
        }
        else
        {
            InlineToastPanel.Opacity = 1;
            InlineToastPanel.HeightRequest = InlineToastHeightDip;
            InlineToastPanel.TranslationY = 0;
        }

        await Task.Delay(InlineToastHoldMs);
        if (version != _inlineToastVersion || !IsOpen)
            return;

        await Task.WhenAll(
            InlineToastPanel.FadeToAsync(0, InlineToastOutMs, Easing.CubicIn),
            InlineToastPanel.TranslateToAsync(0, -3, InlineToastOutMs, Easing.CubicIn),
            AnimateInlineToastHeightAsync(0, InlineToastOutMs, Easing.CubicIn));

        if (version != _inlineToastVersion)
            return;

        HideInlineToast(immediate: true);
    }

    private Task AnimateInlineToastHeightAsync(double targetHeight, uint durationMs, Easing easing)
    {
        var startHeight = InlineToastPanel.HeightRequest;
        var completion = new TaskCompletionSource<bool>();

        InlineToastPanel.AbortAnimation(nameof(InlineToastPanel));
        new Animation(
            callback: value => InlineToastPanel.HeightRequest = value,
            start: startHeight,
            end: targetHeight,
            easing: easing)
            .Commit(
                owner: this,
                name: nameof(InlineToastPanel),
                rate: 16,
                length: durationMs,
                finished: (_, _) => completion.TrySetResult(true));

        return completion.Task;
    }

    private void HideInlineToast(bool immediate)
    {
        _inlineToastVersion++;

        InlineToastPanel.AbortAnimation(nameof(InlineToastPanel));
        InlineToastPanel.CancelAnimations();

        if (!immediate)
            return;

        InlineToastPanel.IsVisible = false;
        InlineToastPanel.Opacity = 0;
        InlineToastPanel.HeightRequest = 0;
        InlineToastPanel.TranslationY = -4;
        InlineToastLabel.Text = string.Empty;
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
        {
            _chatEntryTextBox.KeyDown += OnChatEntryTextBoxKeyDown;

            // WinUI's focused underline and text-selection highlight come from theme resources
            // (system accent); repoint them at the configured accent (lightweight styling).
            _chatEntryTextBox.Resources["TextControlBorderBrushFocused"] = _winAccentBrush;
            _chatEntryTextBox.Resources["TextControlSelectionHighlightColor"] = _winAccentBrush;
        }
#endif
    }

    // WinUI draws a system-accent selection pill on ListView items (slash-command list, /chats
    // conversation list); repoint that theme brush at the configured accent.
    private void OnListHandlerChanged(object? sender, EventArgs e)
    {
#if WINDOWS
        if ((sender as VisualElement)?.Handler?.PlatformView is Microsoft.UI.Xaml.FrameworkElement fe)
            fe.Resources["ListViewItemSelectionIndicatorBrush"] = _winAccentBrush;
#endif
    }

    private void OnResizeGripHandlerChanged(object? sender, EventArgs e) => ApplyResizeGripCursor();

    // Show a diagonal resize cursor while hovering the corner grip (Windows only — MAUI has no
    // cross-platform cursor API). The diagonal matches the corner the grip occupies: top-right of
    // the panel gets NE-SW, top-left gets NW-SE. WinUI's UIElement.ProtectedCursor is protected,
    // so it is set via reflection — the standard workaround until WinUI exposes it publicly.
    private void ApplyResizeGripCursor()
    {
#if WINDOWS
        if (ResizeCornerGrip.Handler?.PlatformView is not Microsoft.UI.Xaml.UIElement element)
            return;

        var shape = _onLeft
            ? Microsoft.UI.Input.InputSystemCursorShape.SizeNorthwestSoutheast
            : Microsoft.UI.Input.InputSystemCursorShape.SizeNortheastSouthwest;
        typeof(Microsoft.UI.Xaml.UIElement)
            .GetProperty("ProtectedCursor",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.SetValue(element, Microsoft.UI.Input.InputSystemCursor.Create(shape));
#endif
    }

#if WINDOWS
    private void OnChatEntryTextBoxKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Tab:
                if (TryAttachSelectedWindow() || TryAutocompleteSelectedCommand())
                    e.Handled = true;
                return;

            case Windows.System.VirtualKey.Down:
                if (_slashSuggestionsVisible)
                {
                    MoveSlashSelection(1);
                    e.Handled = true;
                }
                else if (_windowSuggestionsVisible)
                {
                    MoveWindowSelection(1);
                    e.Handled = true;
                }
                else if (_listMode)
                {
                    MoveConversationSelection(1);
                    e.Handled = true;
                }
                return;

            case Windows.System.VirtualKey.Up:
                if (_slashSuggestionsVisible)
                {
                    MoveSlashSelection(-1);
                    e.Handled = true;
                }
                else if (_windowSuggestionsVisible)
                {
                    MoveWindowSelection(-1);
                    e.Handled = true;
                }
                else if (_listMode)
                {
                    MoveConversationSelection(-1);
                    e.Handled = true;
                }
                return;

            case Windows.System.VirtualKey.Enter:
                if (_listMode)
                {
                    OpenSelectedConversation();
                    e.Handled = true;
                }
                return;

            case Windows.System.VirtualKey.Escape:
                if (_slashSuggestionsVisible)
                {
                    HideSlashSuggestions();
                    e.Handled = true;
                }
                else if (_windowSuggestionsVisible)
                {
                    _dismissedAtTokenIndex = _atTokenIndex;
                    HideWindowSuggestions();
                    e.Handled = true;
                }
                else if (_listMode)
                {
                    ExitListMode();
                    e.Handled = true;
                }
                return;
        }
    }
#endif

    private async void OnSendClicked(object? sender, EventArgs e)
    {
        // Enter with the @-window picker open attaches the highlighted window instead of sending.
        if (TryAttachSelectedWindow())
            return;

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

        // Windows attached via @ ride along on this message only; the chips clear on send.
        var attachments = _attachments.ToList();
        _attachments.Clear();
        AttachmentChipsPanel.IsVisible = false;

        // Build the conversation to send before adding the pending placeholder.
        var history = Messages
            .Select(m => new ChatMessage(m.IsUser ? ChatRole.User : ChatRole.Assistant, m.Text))
            .ToList();
        history.Add(await BuildUserMessageAsync(prompt, attachments));

        // Later turns rebuild history from bubble text only, so keep at least a marker of what
        // was attached in the bubble itself.
        var bubbleText = attachments.Count == 0
            ? text
            : $"{text}\n[Attached: {string.Join(", ", attachments.Select(a => a.Title))}]";
        Messages.Add(new ChatMessageVm(isUser: true, bubbleText));
        var pending = new ChatMessageVm(isUser: false, "…");
        Messages.Add(pending);
        MessagesList.IsVisible = true;
        ScrollToLatest();
        _waitingForFirstChunk = true;
        _host.SetBusy(true);

        // Sources the model retrieves this turn (filled by the search_captures tool), shown as citations.
        var citations = new List<MemoryCitation>();

        try
        {
            var streamed = new StringBuilder();
            var repaint = Stopwatch.StartNew();
            var lastScrollMs = 0L;

            await foreach (var chunk in _chatService.GetStreamingResponseAsync(history, mcpServer, citations, skillInstructions, ApproveExecAsync))
            {
                Debug.WriteLine($"[Chat] Received chunk: {chunk}");
                if (string.IsNullOrEmpty(chunk))
                    continue;

                if (_waitingForFirstChunk)
                {
                    _waitingForFirstChunk = false;
                    _host.SetBusy(false);
                }

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
            _waitingForFirstChunk = false;
            _host.SetBusy(false);
        }

        if (citations.Count > 0)
        {
            pending.Citations = citations.Select(ToCitationVm).ToList();
            pending.CitationSources = citations.ToList();
        }

        ScrollToLatest();
        PersistCurrentConversation();
    }

    // Completes when the user clicks Run/Cancel on the exec approval panel; set while a command is pending.
    private TaskCompletionSource<bool>? _pendingExecApproval;

    // Called by the exec tool (possibly off the UI thread) before it runs a command: shows the approval
    // panel with the exact command, waits for Run/Cancel, records the outcome as a system note, and returns
    // whether the user approved. All UI mutation is marshaled to the main thread.
    private async Task<bool> ApproveExecAsync(ExecApprovalRequest request)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            _pendingExecApproval = tcs;
            ExecApprovalHeaderLabel.Text = $"Run this command in {request.ShellName}?";
            ExecApprovalCommandLabel.Text = request.Command;

            if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
            {
                ExecApprovalDirLabel.Text = $"in {request.WorkingDirectory}";
                ExecApprovalDirLabel.IsVisible = true;
            }
            else
            {
                ExecApprovalDirLabel.IsVisible = false;
            }

            ExecApprovalPanel.IsVisible = true;
            ScrollToLatest();
        });

        var approved = await tcs.Task;

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            ExecApprovalPanel.IsVisible = false;
            var note = approved ? $"⚡ Ran in {request.ShellName}: {request.Command}"
                                : $"🚫 Declined: {request.Command}";
            Messages.Add(new ChatMessageVm(isUser: false, note, isSystemNote: true));
            MessagesList.IsVisible = true;
            ScrollToLatest();
        });

        return approved;
    }

    private void OnExecApprovalRunClicked(object? sender, EventArgs e) => ResolveExecApproval(true);

    private void OnExecApprovalCancelClicked(object? sender, EventArgs e) => ResolveExecApproval(false);

    private void ResolveExecApproval(bool approved)
    {
        var pending = _pendingExecApproval;
        _pendingExecApproval = null;
        ExecApprovalPanel.IsVisible = false;
        pending?.TrySetResult(approved);
    }

    // The outgoing user message: plain text, or multimodal when windows were attached via @ —
    // each attachment contributes its accessibility text and (when captured) the screenshot bytes.
    private static async Task<ChatMessage> BuildUserMessageAsync(
        string prompt,
        List<PromptAttachmentVm> attachments)
    {
        if (attachments.Count == 0)
            return new ChatMessage(ChatRole.User, prompt);

        var contents = new List<AIContent> { new TextContent(prompt) };
        foreach (var attachment in attachments)
        {
            CaptureResult? capture = null;
            try
            {
                capture = attachment.CaptureTask is null ? null : await attachment.CaptureTask;
            }
            catch
            {
                // Failed captures were already removed + toasted by FinishAttachmentAsync.
            }

            if (capture is null)
                continue;

            var body = capture.Content.Length > MaxAttachmentChars
                ? capture.Content[..MaxAttachmentChars] + "…"
                : capture.Content;
            contents.Add(new TextContent($"[Attached window: {capture.WindowTitle}]\n{body}"));

            if (!string.IsNullOrEmpty(capture.ImagePath) && File.Exists(capture.ImagePath))
                contents.Add(new DataContent(await File.ReadAllBytesAsync(capture.ImagePath), "image/png"));
        }

        return new ChatMessage(ChatRole.User, contents);
    }

    private void ScrollToLatest()
    {
        if (Messages.Count > 0)
            MessagesList.ScrollTo(Messages[^1], position: ScrollToPosition.End, animate: true);
    }
}

/// <summary>
/// Stand-in host used between construction and <see cref="ChatPanelView.Attach"/> (and after
/// <see cref="ChatPanelView.Detach"/>), so the panel never has to null-check its host.
/// </summary>
internal sealed class NullChatPanelHost : IChatPanelHost
{
    public static readonly NullChatPanelHost Instance = new();

    public void RequestPanelSize(double widthDip, double heightDip) { }
    public double AvailableWidthDip() => ChatPanelView.MaxChatWidth;
    public double AvailableListHeightDip(double chromeDip) => ChatPanelView.MaxChatListHeight;
    public void SetForceInteractive(bool force) { }
    public void MoveWindowBy(double dxDip, double dyDip) { }
    public void CollapseRequested() { }
    public void SetBusy(bool busy) { }
}
