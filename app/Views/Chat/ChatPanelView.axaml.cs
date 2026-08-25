using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using AsyncAwaitBestPractices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Floaty.IconFont;
using Floaty.Services;
using Floaty.Ui;
using Microsoft.Extensions.AI;

namespace Floaty.Views.Chat;

/// <summary>
/// The chat surface: message list, conversation switcher, slash/@ pickers, exec approval and the input
/// row. It owns everything about the conversation and nothing about windows — whenever it needs to grow,
/// move or close it asks its <see cref="IChatPanelHost"/>, so the same view works both glued to the ring
/// (<see cref="ChatPanelPlacement.Floating"/>) and in its own window (<see cref="ChatPanelPlacement.Fixed"/>).
/// </summary>
public partial class ChatPanelView : UserControl
{
    private readonly SettingsService _settings;
    private readonly IChatService _chatService;
    private readonly IScreenCaptureService _captureService;
    private readonly IMemoryService _memoryService;
    private readonly ConversationService _conversationStore;
    private readonly SkillService _skillService;
    private readonly IVoiceInputService _voiceInput;
    private readonly IFileIngestService _fileIngest;
    private readonly ISoundService _sounds;
    private readonly IServiceProvider _services;

    // Set by Attach() before the panel is shown; every window operation goes through it.
    private IChatPanelHost _host = NullChatPanelHost.Instance;

    // True while the mic pulse animation loop should keep running (set on start/stop listening).
    private bool _micPulsing;

    // The conversation currently shown in Messages; created lazily on first chat open (resume most recent).
    private Conversation? _currentConversation;
    private bool _conversationLoaded;

    // Conversation switcher (shown in the message list's slot under /chats).
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
    private const double DefaultListMaxHeight = 240; // mirrors the lists' XAML MaxHeight
    private double? _userListHeight;
    private double _resizeStartListHeight;
    private double _resizeStartChromeDip;

    // Panel height fallback before the first real measurement (matches the old ChatBaseHeight + 80).
    private const double InitialPanelHeight = 80;

    // Last height we reported to the host, to avoid redundant resizes / oscillation.
    private double _lastPanelHeight;

    // Cumulative pan offset reported on the previous drag-bar PanUpdated, used to derive per-frame deltas.
    // MAUI's TranslationY, as a transform Avalonia can animate.
    private readonly TranslateTransform _panelSlide = new();
    private readonly TranslateTransform _inlineToastSlide = new();

    private bool _resizing;
    private Point _resizeOrigin;
    private bool _draggingWindow;
    private PixelPoint _lastDragScreen;
    // A text selection is in progress in the message list; see OnMessagesPointerPressed.
    private bool _selecting;

    private bool _waitingForFirstChunk;

    private const double InlineToastHeightDip = 28;
    private const uint InlineToastInMs = 170;
    private const uint InlineToastOutMs = 180;
    private const int InlineToastHoldMs = 1600;
    private int _inlineToastVersion;

    private readonly IReadOnlyList<SlashCommand> _builtInSlashCommands =
    [
        new("new", "Start a new conversation", icon: TablerLine.Sparkles),
        new("chats", "Switch between conversations", icon: TablerLine.Messages),
        new("capture", "Capture and remember the current app", icon: TablerLine.Camera),
        new("remember", "Save text to memory", SlashKind.Memory, TablerLine.Bulb),
        new("recall", "Search your memory", SlashKind.Memory, TablerLine.Search),
        new("settings", "Open Floaty settings", icon: TablerLine.Settings),
        new("config", "Open Floaty config folder", icon: TablerLine.Folder),
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
    private const int MaxAttachmentChars = 12_000;      // cap per-attachment text sent to the model
    private const int MaxTotalAttachmentChars = 40_000; // …and across every attachment on one turn
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

    // --- File drops ---

    // How long each drag-over keeps the host window input-opaque; see IChatPanelHost.KeepInteractiveFor.
    private static readonly TimeSpan DragInteractiveGrace = TimeSpan.FromMilliseconds(400);

    // A drag that ends outside the app doesn't reliably report a leave, so a watchdog restarted on
    // every drag-over restores the border rather than leaving it stuck highlighted.
    private const int PanelDropFeedbackTimeoutMs = 600;
    private static readonly Brush DefaultPanelStroke = new SolidColorBrush(Color.Parse("#22FFFFFF"));
    private readonly SolidColorBrush _accentBrush = new(Colors.White);
    private bool _panelDropActive;
    private DispatcherTimer? _panelDropWatchdog;

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
        IFileIngestService fileIngest,
        ISoundService sounds,
        IServiceProvider services)
    {
        InitializeComponent();
        _settings = settings;
        _chatService = chatService;
        _captureService = captureService;
        _memoryService = memoryService;
        _fileIngest = fileIngest;
        _conversationStore = conversationStore;
        _skillService = skillService;
        _voiceInput = voiceInput;
        _sounds = sounds;
        _services = services;

        // The message list binds straight to the collection. Where the Blazor version needed a bridge
        // object, a root-component registration and a JS ResizeObserver reporting heights back, an
        // ItemsControl observes the ObservableCollection directly and sizes itself.
        MessagesList.ItemsSource = Messages;
        Messages.CollectionChanged += OnMessagesChanged;

        SlashSuggestionsList.ItemsSource = _filteredSlashCommands;
        WindowSuggestionsList.ItemsSource = _filteredWindows;
        AttachmentChipsPanel.ItemsSource = _attachments;

        // Panel-wide file drops. Avalonia routes these itself, so the WinUI platform-view escape and
        // its four native drag events are gone.
        DragDrop.SetAllowDrop(PanelBorder, true);
        PanelBorder.AddHandler(DragDrop.DragEnterEvent, OnPanelDragEnter);
        PanelBorder.AddHandler(DragDrop.DragOverEvent, OnPanelDragOver);
        PanelBorder.AddHandler(DragDrop.DragLeaveEvent, OnPanelDragLeave);
        PanelBorder.AddHandler(DragDrop.DropEvent, OnPanelDrop);

        // One handler for every bubble: MarkdownPresenter raises a bubbling routed event.
        PanelBorder.AddHandler(MarkdownPresenter.LinkClickedEvent, OnMarkdownLinkClicked);

        // The corner grip and the drag bar are pointer-driven; MAUI used PanGestureRecognizer.
        ResizeCornerGrip.PointerPressed += OnResizeCornerPointerPressed;
        ResizeCornerGrip.PointerMoved += OnResizeCornerPointerMoved;
        ResizeCornerGrip.PointerReleased += OnResizeCornerPointerReleased;
        ResizeCornerGrip.Cursor = new Cursor(StandardCursorType.TopRightCorner);

        DragBar.PointerPressed += OnDragBarPointerPressed;
        DragBar.PointerMoved += OnDragBarPointerMoved;
        DragBar.PointerReleased += OnDragBarPointerReleased;
        DragBar.Cursor = new Cursor(StandardCursorType.SizeAll);

        // Text selection in the bubbles. handledEventsToo because SelectableTextBlock marks the press
        // handled the moment it takes the pointer.
        MessagesScroller.AddHandler(PointerPressedEvent, OnMessagesPointerPressed,
            RoutingStrategies.Bubble, handledEventsToo: true);
        MessagesScroller.AddHandler(PointerReleasedEvent, OnMessagesPointerReleased,
            RoutingStrategies.Bubble, handledEventsToo: true);

        RenderTransform = _panelSlide;
        InlineToastPanel.RenderTransform = _inlineToastSlide;

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
        RefreshConversationTitle();
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

        Messages.CollectionChanged -= OnMessagesChanged;

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
        RefreshMessageAreaHeight();
        _lastPanelHeight = 0;
        IsVisible = true;
        RefreshConversationTitle();
    }

    /// <summary>Slides the panel up into view and focuses the input.</summary>
    public async Task AnimateInAsync()
    {
        _panelSlide.Y = 24;
        Opacity = 0;
        await Task.WhenAll(
            Anim.RunAsync(24, 0, 220, Anim.SinOut, v => _panelSlide.Y = v),
            Anim.RunAsync(0, 1, 220, Anim.Linear, v => Opacity = v));

        ChatEntry.Focus();

        // Land on the newest message. BeginOpen's restore runs before the scroller has been laid out,
        // so a scroll issued there has nothing to scroll; by now the panel is on screen and measured.
        ScrollToLatest();
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
        // Avalonia has no Unfocus(); moving focus to the panel itself takes it off the input.
        Focus();
    }

    /// <summary>Fades the panel out; the host animates its window at the same time.</summary>
    public Task FadeOutAsync() =>
        Anim.RunAsync(Opacity, 0, 180, Anim.Linear, v => Opacity = v);

    /// <summary>Resets the panel to its hidden resting state once the host's close animation finishes.</summary>
    public void EndClose()
    {
        HideInlineToast(immediate: true);
        IsVisible = false;
        Opacity = 1;
        _panelSlide.Y = 0;
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

        // BoundsInPage walked MAUI's Frame chain by hand; Avalonia can translate a control's own
        // bounds into the window's coordinate space directly.
        return ContainsInWindow(this, x, y) || ContainsInWindow(ResizeCornerGrip, x, y);
    }

    private static bool ContainsInWindow(Visual visual, double x, double y)
    {
        var topLevel = TopLevel.GetTopLevel(visual);
        if (topLevel is null || visual.TranslatePoint(default, topLevel) is not { } origin)
            return false;

        return x >= origin.X && x <= origin.X + visual.Bounds.Width
            && y >= origin.Y && y <= origin.Y + visual.Bounds.Height;
    }

    /// <summary>
    /// Places the panel on the given side of the ring: mirrors the collapse chevron and the corner resize
    /// grip. The resize handle shares the top row with the drag bar but stays in its own edge cell so the
    /// gestures don't compete: column 0 when the panel sits left of the ring, column 2 otherwise.
    /// </summary>
    public void ApplyPanelSide(bool onLeft)
    {
        _onLeft = onLeft;
        if (onLeft)
        {
            CollapseButton.Content = TablerLine.CaretRight;
            CollapseButton.Margin = new Thickness(0, 0, 6, 0);
            Grid.SetColumn(CollapseButton, 2);
            Grid.SetColumn(ResizeCornerGrip, 0);
            ResizeCornerGrip.HorizontalAlignment = HorizontalAlignment.Left;
            ResizeCornerGrip.Margin = new Thickness(-5, -5, 0, 0);
            ResizeCornerGlyph.Text = TablerLine.RadiusTopLeft;
            Grid.SetColumn(ConversationTitleHost, 2);
            ConversationTitleHost.HorizontalAlignment = HorizontalAlignment.Right;
            ConversationTitleHost.Margin = new Thickness(0, 0, 6, 0);

        }
        else
        {
            CollapseButton.Content = TablerLine.CaretLeft;
            CollapseButton.Margin = new Thickness(6, 0, 0, 0);
            Grid.SetColumn(CollapseButton, 0);
            Grid.SetColumn(ResizeCornerGrip, 2);
            ResizeCornerGrip.HorizontalAlignment = HorizontalAlignment.Right;
            ResizeCornerGrip.Margin = new Thickness(0, -5, 2, 0);
            ResizeCornerGlyph.Text = TablerLine.RadiusTopRight;
            Grid.SetColumn(ConversationTitleHost, 0);
            ConversationTitleHost.HorizontalAlignment = HorizontalAlignment.Left;
            ConversationTitleHost.Margin = new Thickness(6, 0, 0, 0);

        }

        ApplyResizeGripCursor();
    }

    private static string DefaultConversationTitle => "Conversation";

    private void RefreshConversationTitle()
    {
        var title = string.IsNullOrWhiteSpace(_currentConversation?.Title)
            ? DefaultConversationTitle
            : _currentConversation!.Title.Trim();

        ConversationTitleLabel.Text = title;
        if (!ConversationTitleEditorBorder.IsVisible)
            ConversationTitleEntry.Text = title;
    }

    private void OnConversationTitleTapped(object? sender, RoutedEventArgs e)
    {
        EnsureConversationLoaded();

        ConversationTitleChip.IsVisible = false;
        ConversationTitleEditorBorder.IsVisible = true;
        ConversationTitleEntry.Text = string.IsNullOrWhiteSpace(_currentConversation?.Title)
            ? string.Empty
            : _currentConversation!.Title.Trim();

        Dispatcher.UIThread.Post(() =>
        {
            ConversationTitleEntry.Focus();
            ConversationTitleEntry.CaretIndex = ConversationTitleEntry.Text?.Length ?? 0;
        });
    }

    // Enter commits, Escape abandons. MAUI had a dedicated Completed event for the first of these.
    private void OnConversationTitleKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitConversationTitleEdit();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            ConversationTitleEditorBorder.IsVisible = false;
            ConversationTitleChip.IsVisible = true;
            RefreshConversationTitle();
        }
    }

    private void OnConversationTitleLostFocus(object? sender, RoutedEventArgs e) => CommitConversationTitleEdit();

    private void CommitConversationTitleEdit()
    {
        if (!ConversationTitleEditorBorder.IsVisible)
            return;

        EnsureConversationLoaded();
        if (_currentConversation is null)
            return;

        var edited = (ConversationTitleEntry.Text ?? string.Empty).Trim();
        _currentConversation.Title = edited;

        ConversationTitleEditorBorder.IsVisible = false;
        ConversationTitleChip.IsVisible = true;
        RefreshConversationTitle();

        PersistCurrentConversation();
    }

    // --- Settings / accent ---

    private void OnSettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            ApplyAccentColor(_settings.Current.AccentColor);
            RebuildSlashCommands();
            UpdateMicVisibility();
        });

    // Live preview from the Appearance accent picker: apply without persisting (the settings page
    // reverts to the saved value when it closes without a Save).
    private void OnAccentColorPreviewRequested(object? sender, string hex) =>
        Dispatcher.UIThread.Post(() => ApplyAccentColor(hex));

    // Recolor the accent surfaces this panel owns: user chat bubbles via the shared static + per-message
    // refresh, and the WinUI theme brushes behind the entry underline / list selection pill. The
    // AccentColor / AccentIconOnDarkColor DynamicResources themselves are application-wide and are
    // updated by OverlayPage.
    private void ApplyAccentColor(string? hex)
    {
        var palette = AccentPalette.From(hex);

        // Shared with the drag-over border highlight, so it follows an accent change immediately.
        // Everything else (send button, glyphs, selection) resolves the application-wide
        // AccentColor / AccentIconOnDarkColor DynamicResources, which App.ApplyAccentColor updates.
        _accentBrush.Color = Color.Parse(palette.Base);
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
            _allSlashCommands.Add(new SlashCommand(server.Name, "MCP server", SlashKind.Server, TablerLine.Database));
        }

        _skillService.Reload();
        foreach (var skill in _skillService.Skills)
        {
            if (!skill.Enabled || string.IsNullOrWhiteSpace(skill.Name) || NameTaken(skill.Name))
                continue;
            var description = string.IsNullOrWhiteSpace(skill.Description) ? "Agent skill" : skill.Description;
            _allSlashCommands.Add(new SlashCommand(skill.Name, description, SlashKind.Skill, TablerLine.Bolt));
        }
    }

    private void OnCollapseChatClicked(object? sender, RoutedEventArgs e) => _host.CollapseRequested();

    // --- Voice input ---

    // The mic button only shows while a downloaded speech-to-text model is selected in settings
    // (and the platform can capture audio). Re-evaluated on every settings change.
    private void UpdateMicVisibility()
    {
        MicButton.IsVisible = _voiceInput.IsConfigured;
        if (!MicButton.IsVisible && _voiceInput.IsListening)
            _ = StopListeningAsync();
    }

    private async void OnMicClicked(object? sender, RoutedEventArgs e)
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
            MicButton.Content = TablerLine.PlayerRecord;
            MicButton[!BackgroundProperty] = new DynamicResourceExtension("AccentColor");
            if (!_micPulsing)
            {
                _micPulsing = true;
                _ = PulseMicAsync();
            }
        }
        else
        {
            _micPulsing = false;
            MicButton.Content = TablerLine.Microphone;
            MicButton.Background = new SolidColorBrush(Color.Parse("#33FFFFFF"));
        }
    }

    private async Task PulseMicAsync()
    {
        while (_micPulsing)
        {
            await Anim.RunAsync(1.0, 0.55, 500, Anim.SinOut, v => MicButton.Opacity = v);
            await Anim.RunAsync(0.55, 1.0, 500, Anim.SinOut, v => MicButton.Opacity = v);
        }
        MicButton.Opacity = 1;
    }

    // A finished speech segment: append to the entry like the user typed it. Deliberately not
    // wrapped in _suppressEntryTextChanged — dictated text is user content, so slash/@ popup
    // parsing should behave exactly as it does for typing.
    private void OnVoiceSegment(object? sender, string text) =>
        Dispatcher.UIThread.Post(() =>
        {
            var existing = ChatEntry.Text ?? string.Empty;
            ChatEntry.Text = existing.Length == 0 ? text : $"{existing.TrimEnd()} {text}";
            ChatEntry.CaretIndex = ChatEntry.Text.Length;
        });

    // Long silence after speech: in auto-send mode this sends through the normal send path
    // (slash/@ handling included). Listening always stops first so the mic never transcribes
    // while the reply streams.
    private void OnVoicePause(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(async () =>
        {
            if (!_voiceInput.IsListening
                || _settings.Current.VoiceSendMode != VoiceSendMode.AutoSendOnPause
                || string.IsNullOrWhiteSpace(ChatEntry.Text))
                return;

            await StopListeningAsync();
            OnSendClicked(MicButton, new RoutedEventArgs());
        });

    private void OnVoiceError(object? sender, string message) =>
        Dispatcher.UIThread.Post(async () =>
        {
            ApplyMicListeningVisuals(false);
            await ShowInlineToastAsync($"⚠️ {message}");
        });

    // --- Blazor message surface ---

    // Earliest hook: the platform WebView2 exists but CoreWebView2 does not yet. AllowDrop=false keeps
    // WebView2 out of the drag-drop path so external file drops keep routing to PanelBorder's handler
    // (see OnPanelBorderHandlerChanged).
    // A link inside a bubble opens in the user's browser rather than in the chat. MarkdownRenderer
    // already blanked any URL outside the allowlist, but the scheme is re-checked here because
    // everything that reaches this point came from model output.
    private void OnMarkdownLinkClicked(object? sender, LinkClickedEventArgs e)
    {
        e.Handled = true;
        var href = e.Url;

        Dispatcher.UIThread.Post(async () =>
        {
            if (!Uri.TryCreate(href, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https" or "mailto"))
                return;

            try
            {
                if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
                    await launcher.LaunchUriAsync(uri);
            }
            catch (Exception ex)
            {
                await ShowInlineToastAsync($"⚠️ {ex.Message}");
            }
        });
    }

    // --- Sizing ---

    // Ask the host to grow (or shrink) its window to match the panel's content height. The panel hugs
    // its content - collapsed messages area when empty, expanding up to DefaultListMaxHeight (after
    // which the list scrolls), so the window tracks it without leaving dead space.
    private void OnPanelSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (!IsOpen)
            return;

        RequestHostSize();
    }

    /// <summary>
    /// Everything in the panel that is not the message list, and so must never be squeezed out: the
    /// border's padding, the grip row, the input row, and whichever optional strips are showing.
    /// </summary>
    private double ChromeHeightDip =>
        PanelBorder.Padding.Top + PanelBorder.Padding.Bottom
        + TopGripRow.Bounds.Height
        + InputRow.Bounds.Height
        + (AttachmentChipsPanel.IsVisible ? AttachmentChipsPanel.Bounds.Height : 0)
        + (ExecApprovalPanel.IsVisible ? ExecApprovalPanel.Bounds.Height : 0)
        + (InlineToastPanel.IsVisible ? InlineToastPanel.Bounds.Height : 0)
        + (SlashSuggestionsPanel.IsVisible ? SlashSuggestionsPanel.Bounds.Height : 0)
        + (WindowSuggestionsPanel.IsVisible ? WindowSuggestionsPanel.Bounds.Height : 0);

    /// <summary>
    /// Asks the host for the height this panel wants: its fixed chrome plus however much of the
    /// conversation fits under the list's ceiling.
    /// </summary>
    /// <remarks>
    /// Deliberately computed rather than read from <c>Bounds.Height</c>. The panel is arranged inside
    /// the window, so once it wants more room than the window has, its own bounds are the *clipped*
    /// height - reporting that back would tell the host the panel already fits and the window could
    /// never grow to accommodate it.
    /// </remarks>
    private void RequestHostSize()
    {
        var listHeight = _listMode
            ? ConversationList.Bounds.Height
            : Math.Min(MessagesList.Bounds.Height, MessagesScroller.MaxHeight);

        if (double.IsNaN(listHeight) || double.IsInfinity(listHeight))
            listHeight = 0;

        var wanted = ChromeHeightDip + Math.Max(0, listHeight);
        if (wanted <= 0 || Math.Abs(wanted - _lastPanelHeight) < 1)
            return;

        _lastPanelHeight = wanted;
        _host.RequestPanelSize(_chatWidth, wanted);
    }

    // Size the message area. An ItemsControl inside a ScrollViewer hugs its content up to MaxHeight and
    // scrolls past it, which is the whole of what the Blazor version needed a JS ResizeObserver, a
    // reported content height, a feedback-loop guard and a one-pixel sliver to achieve: a collapsed
    // WebView2 stopped laying out, so it could never be hidden outright. A ScrollViewer has no such
    // problem and is simply collapsed when there is nothing to show.
    private void RefreshMessageAreaHeight()
    {
        var hasMessages = !_listMode && Messages.Count > 0;
        MessagesScroller.IsVisible = hasMessages;

        // Cap the list at whatever the host says still fits on screen, not just at the nominal
        // maximum. Without this the panel keeps asking for a taller window than the work area can
        // hold: the host clamps it, the squeezed list re-measures shorter, and the two oscillate.
        //
        // Chrome is measured from the fixed rows rather than as (panel - list). That subtraction goes
        // wrong exactly when it matters: once the panel is clipped, its bounds no longer include the
        // rows that got pushed out, chrome comes out near zero, and the list is allowed to grow over
        // the input row - which is how the input row ended up off the bottom of the screen.
        var chrome = ChromeHeightDip;
        var ceiling = Math.Min(MaxChatListHeight, _host.AvailableListHeightDip(chrome));
        MessagesScroller.MaxHeight = Math.Clamp(_userListHeight ?? DefaultListMaxHeight, 1, ceiling);

        // The list's ceiling just moved, so the height the panel wants may have moved with it.
        if (IsOpen)
            Dispatcher.UIThread.Post(RequestHostSize, DispatcherPriority.Background);
    }

    // Auto-scroll. The rule is chat.js's: if the view is already within a couple of lines of the
    // bottom, stay pinned there as content arrives; if the user has scrolled up to read, leave them
    // alone. Streaming mutates a message in place, so each item is watched as well as the collection.
    private const double ScrollPinSlackDip = 24;

    private void OnMessagesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (var item in e.OldItems.OfType<ChatMessageVm>())
                item.PropertyChanged -= OnMessageContentChanged;
        }

        if (e.NewItems is not null)
        {
            foreach (var item in e.NewItems.OfType<ChatMessageVm>())
                item.PropertyChanged += OnMessageContentChanged;
        }

        RefreshMessageAreaHeight();
        StickToBottomIfPinned();
    }

    private void OnMessageContentChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        Dispatcher.UIThread.Post(StickToBottomIfPinned, DispatcherPriority.Background);

    private void StickToBottomIfPinned()
    {
        var distanceFromBottom = MessagesScroller.Extent.Height
            - MessagesScroller.Offset.Y
            - MessagesScroller.Viewport.Height;

        if (distanceFromBottom < ScrollPinSlackDip)
            MessagesScroller.ScrollToEnd();
    }

    // Drag the panel's outer top corner to resize it in both axes. Width: the window grows away from the
    // ring (the host anchors the ring's edge); on the left side the grip is on the panel's left, so
    // dragging left (negative X) widens it. Height: the grip is on top and the window's bottom edge is
    // anchored, so dragging up (negative Y) grows it — the drag sets a user height on the lists, the panel
    // grows, and OnPanelSizeChanged asks the host to follow. Both axes are clamped to the space available.
    private void OnResizeCornerPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        _resizing = true;
        _resizeOrigin = e.GetPosition(this);
        // The grip drag routinely leaves the grip's small hit-rect; stay input-opaque until release.
        _host.SetForceInteractive(true);
        _resizeStartWidth = _chatWidth;
        var measuredList = MeasuredListHeightDip();
        _resizeStartListHeight = measuredList > 0 ? measuredList : _userListHeight ?? DefaultListMaxHeight;
        // Fixed chrome (input row, padding, suggestions…) around the list, captured once so it isn't
        // re-measured mid-drag while the layout is in flux.
        _resizeStartChromeDip = Math.Max(0, Bounds.Height - measuredList);
        e.Pointer.Capture(ResizeCornerGrip);
        e.Handled = true;
    }

    private void OnResizeCornerPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_resizing)
            return;

        // Deltas are taken against this control, which does not move under the pointer during a
        // resize - unlike the ring, whose window travels with the drag and needs screen space.
        var position = e.GetPosition(this);
        var totalX = position.X - _resizeOrigin.X;
        var totalY = position.Y - _resizeOrigin.Y;

        var widthDelta = _onLeft ? -totalX : totalX;
        var heightDelta = -totalY;
        _chatWidth = Math.Clamp(_resizeStartWidth + widthDelta, MinChatWidth, _host.AvailableWidthDip());
        var listHeight = Math.Clamp(_resizeStartListHeight + heightDelta,
            MinChatListHeight, _host.AvailableListHeightDip(_resizeStartChromeDip));
        ApplyUserListHeight(listHeight);
        _host.RequestPanelSize(_chatWidth, PanelHeightOrDefault);
    }

    private void OnResizeCornerPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_resizing)
            return;

        _resizing = false;
        _host.SetForceInteractive(false);
        e.Pointer.Capture(null);
    }

    // Drag the panel's own window by its handle (fixed placement). Mirrors the ring's pan handling:
    // per-frame deltas derived from the cumulative totals, input-opaque for the whole gesture.
    private void OnDragBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        _draggingWindow = true;
        // Screen space: the window moves out from under the pointer as it is dragged, so
        // window-relative positions would difference to roughly zero. Same reasoning as the ring.
        _lastDragScreen = this.PointToScreen(e.GetPosition(this));
        _host.SetForceInteractive(true);
        e.Pointer.Capture(DragBar);
        e.Handled = true;
    }

    private void OnDragBarPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_draggingWindow)
            return;

        var now = this.PointToScreen(e.GetPosition(this));
        var dxPx = now.X - _lastDragScreen.X;
        var dyPx = now.Y - _lastDragScreen.Y;
        if (dxPx == 0 && dyPx == 0)
            return;

        _lastDragScreen = now;
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        _host.MoveWindowBy(dxPx / scale, dyPx / scale);
    }

    private void OnDragBarPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_draggingWindow)
            return;

        _draggingWindow = false;
        _host.SetForceInteractive(false);
        e.Pointer.Capture(null);
    }

    /// <summary>
    /// A press in the message list starts a text selection, which needs two things. The window must stay
    /// input-opaque until the release: extending a selection routinely takes the pointer past the panel's
    /// edge, which the click-through poll reads as "not over the panel" and would drop the gesture. And
    /// any selection still highlighted in another bubble has to go - each SelectableTextBlock selects
    /// independently, so leaving two lit would show a selection that Ctrl+C does not copy.
    /// </summary>
    private void OnMessagesPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var pressed = (e.Source as Visual)?.FindAncestorOfType<SelectableTextBlock>(includeSelf: true);

        // Latched only for a press that landed on selectable text, because that is the case where the
        // block captures the pointer and a matching release is guaranteed. A press on the list's empty
        // space captures nothing, and a release outside the window would strand the latch on.
        if (pressed is not null)
        {
            _selecting = true;
            _host.SetForceInteractive(true);
        }

        foreach (var block in MessagesList.GetVisualDescendants().OfType<SelectableTextBlock>())
        {
            if (!ReferenceEquals(block, pressed))
                block.ClearSelection();
        }
    }

    private void OnMessagesPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_selecting)
            return;

        _selecting = false;
        _host.SetForceInteractive(false);
    }

    // Measured height of whichever list is currently visible, or 0 when the chat is empty (both
    // lists collapsed). Used as the drag baseline so the grip tracks the pointer from the real size.
    private double MeasuredListHeightDip()
    {
        if (!_listMode && Messages.Count > 0 && MessagesScroller.Bounds.Height > 0)
            return MessagesScroller.Bounds.Height;
        if (ConversationList.IsVisible && ConversationList.Bounds.Height > 0)
            return ConversationList.Bounds.Height;
        return 0;
    }

    // Apply a user-dragged list height to both surfaces (so the /chats switcher matches the messages
    // view). The message scroller goes through RefreshMessageAreaHeight rather than being set here, so
    // that one method stays the only writer of its height. The conversation list needs both: its XAML
    // maximum of 240 would otherwise cap the drag, and the fixed height makes it track the pointer
    // even when the content is shorter.
    private void ApplyUserListHeight(double heightDip)
    {
        _userListHeight = heightDip;
        ConversationList.MaxHeight = heightDip;
        ConversationList.Height = heightDip;
        RefreshMessageAreaHeight();
    }

    // --- Input parsing: slash commands and @-window mentions ---

    private void OnChatEntryTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressEntryTextChanged)
            return;

        var text = ChatEntry.Text;
        UpdateSlashSuggestions(text);
        UpdateWindowSuggestions(text);
    }

    // The caret from the platform TextBox on Windows: MAUI's CursorPosition lags behind during
    // TextChanged there, and @-parsing needs the position the user is actually typing at.
    private int GetChatEntryCaretIndex(string text)
    {
#if WINDOWS
        var caret = ChatEntry.CaretIndex;
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
            WindowSuggestionsList.ScrollIntoView(selected);
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
            ChatEntry.CaretIndex = Math.Min(atIndex, newText.Length);
        }

        if (_attachments.Any(a => a.Kind == AttachmentKind.Window && a.Hwnd == window.Hwnd))
        {
            _ = ShowInlineToastAsync("Window already attached");
            return;
        }

        // Capture right away (downscaled like auto-history captures) so what the user saw when
        // tagging is what gets sent, even if the window closes before send.
        var vm = new PromptAttachmentVm
        {
            Kind = AttachmentKind.Window,
            Hwnd = window.Hwnd,
            Title = window.Title,
        };
        vm.RemoveCommand = new RelayCommand(() => RemoveAttachment(vm));
        vm.CaptureTask = _captureService.CaptureWindowAsync(window.Hwnd, includeScreenshot: true);
        _attachments.Add(vm);
        RefreshAttachmentChips();

        _ = FinishAttachmentAsync(vm);
    }

    /// <summary>
    /// Attaches dropped files to the pending prompt. Called by whichever surface caught the drop —
    /// the ring (routed through the host) or the panel itself. Safe to call with the panel closed;
    /// the callers open it first.
    /// </summary>
    public void AttachFiles(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return;

        var accepted = 0;
        var duplicates = 0;

        foreach (var path in paths)
        {
            if (accepted >= IFileIngestService.MaxFilesPerDrop)
                break;

            if (_attachments.Any(a => string.Equals(a.SourcePath, path, StringComparison.OrdinalIgnoreCase)))
            {
                duplicates++;
                continue;
            }

            var vm = new PromptAttachmentVm
            {
                Kind = AttachmentKind.File,
                SourcePath = path,
                Title = Path.GetFileName(path),
                Glyph = GlyphForFile(path),
                // The global setting is only the starting position; the chip's toggle is the truth.
                Persist = _settings.Current.RememberDroppedFiles,
            };
            vm.RemoveCommand = new RelayCommand(() => RemoveAttachment(vm));
            vm.TogglePersistCommand = new RelayCommand(() => vm.Persist = !vm.Persist);
            vm.IngestTask = _fileIngest.IngestAsync(path);
            _attachments.Add(vm);
            accepted++;

            _ = FinishFileAttachmentAsync(vm);
        }

        RefreshAttachmentChips();

        if (accepted == 0 && duplicates > 0)
            _ = ShowInlineToastAsync(duplicates == 1 ? "File already attached" : "Files already attached");
        else if (accepted < paths.Count - duplicates)
            _ = ShowInlineToastAsync($"Attached {accepted} of {paths.Count} files");
    }

    /// <summary>
    /// Attaches the text the user had selected in another app when they pressed the summon hotkey.
    /// Routed here by whichever surface is hosting the panel, once it is on screen.
    /// </summary>
    public void AttachSelection(SelectedText selection)
    {
        var text = selection.Text.Trim();
        if (text.Length == 0)
            return;

        // One selection chip at a time. Summoning again means the user is pointing at something new,
        // and quietly sending the previous selection alongside it would be context they think they
        // replaced — unlike files, where every drop is an explicit addition.
        foreach (var stale in _attachments.Where(a => a.Kind == AttachmentKind.Selection).ToList())
            _attachments.Remove(stale);

        var vm = new PromptAttachmentVm
        {
            Kind = AttachmentKind.Selection,
            Title = SelectionPreview(text),
            Glyph = TablerLine.Quote,
            SelectionText = text,
            SourceTitle = selection.SourceTitle,
            // Already plain text: there's no capture or ingest to await, so the chip isn't dimmed.
            IsReady = true,
        };
        vm.RemoveCommand = new RelayCommand(() => RemoveAttachment(vm));
        _attachments.Add(vm);
        RefreshAttachmentChips();
    }

    // The chip shows a one-line taste of the selection, not the selection: newlines and runs of
    // whitespace would otherwise render as a ragged blank stripe inside the chip.
    private const int SelectionPreviewChars = 40;

    private static string SelectionPreview(string text)
    {
        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= SelectionPreviewChars
            ? collapsed
            : collapsed[..SelectionPreviewChars].TrimEnd() + "…";
    }

    /// <summary>
    /// Embeds dropped files straight into memory instead of attaching them to the pending prompt —
    /// the Alt-drop mode. Nothing touches the prompt or the conversation: the files are copied into
    /// <c>~/.floaty/drops</c> and embedded exactly as the persist toggle would have done at send
    /// time, and the only trace in the panel is the inline toast reporting the outcome.
    /// </summary>
    public void MemorizeFiles(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
            return;

        if (!_memoryService.CanRemember)
        {
            _ = ShowInlineToastAsync("Memory needs an embedding provider");
            return;
        }

        _ = MemorizeFilesAsync(paths.Take(IFileIngestService.MaxFilesPerDrop).ToList());
    }

    /// <summary>
    /// Tells the user a dropped folder was ignored. Folders are deliberately not expanded: one
    /// careless drag of a source tree would attach thousands of files.
    /// </summary>
    public void ShowFolderDropHint() => _ = ShowInlineToastAsync("Folders aren't supported — drop files");

    // Ingest + embed sequentially: each file costs a vision call and an embedding call, and running
    // them in parallel would only trade a bounded wait for rate-limit errors.
    private async Task MemorizeFilesAsync(IReadOnlyList<string> paths)
    {
        _ = ShowInlineToastAsync(paths.Count == 1
            ? $"Remembering {Path.GetFileName(paths[0])}…"
            : $"Remembering {paths.Count} files…");

        var remembered = 0;
        foreach (var path in paths)
        {
            try
            {
                var file = await _fileIngest.IngestAsync(path);
                if (file is not null && await MemorizeDropAsync(file))
                    remembered++;
            }
            catch
            {
                // Counted as a failure below; one bad file must not abort the rest of the drop.
            }
        }

        if (remembered == paths.Count)
        {
            await ShowInlineToastAsync(paths.Count == 1
                ? $"Remembered {Path.GetFileName(paths[0])}"
                : $"Remembered {paths.Count} files");
        }
        else if (remembered > 0)
        {
            await ShowInlineToastAsync($"Remembered {remembered} of {paths.Count} files");
        }
        else
        {
            await ShowInlineToastAsync(paths.Count == 1
                ? $"Couldn't remember {Path.GetFileName(paths[0])}"
                : "Couldn't remember those files");
        }
    }

    // Chip glyph by file type, so a mixed drop is readable at a glance.
    private static string GlyphForFile(string path)
    {
        if (MimeTypes.IsImage(path))
            return TablerLine.Photo;

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".pdf" => TablerLine.FileTypePdf,
            ".doc" or ".docx" or ".odt" or ".rtf" => TablerLine.FileTypeDocx,
            ".xls" or ".xlsx" or ".ods" or ".csv" or ".tsv" => TablerLine.FileTypeXls,
            ".ppt" or ".pptx" => TablerLine.FileTypePpt,
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => TablerLine.FileTypeZip,
            ".txt" or ".log" or ".md" or ".markdown" => TablerLine.FileTypeTxt,
            _ => TablerLine.File,
        };
    }

    private void RemoveAttachment(PromptAttachmentVm vm)
    {
        _attachments.Remove(vm);
        RefreshAttachmentChips();
    }

    // The chips row only exists when something is attached.
    private void RefreshAttachmentChips() => AttachmentChipsPanel.IsVisible = _attachments.Count > 0;

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
                RefreshAttachmentChips();
                await ShowInlineToastAsync($"Couldn't capture {vm.Title}");
            }
            return;
        }

        vm.IsReady = true;

        // Same acknowledgement as /capture: the user asked for this window and it has been grabbed.
        _host.SignalCapture();

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

    // Settles a dropped-file chip once ingest completes. Unlike the window path this never persists:
    // whether a drop is remembered is decided by its toggle at send time (see PersistDropsAsync).
    private async Task FinishFileAttachmentAsync(PromptAttachmentVm vm)
    {
        DroppedFile? file = null;
        try
        {
            file = vm.IngestTask is null ? null : await vm.IngestTask;
        }
        catch
        {
            // fall through: treated as an unreadable file below
        }

        if (file is null)
        {
            if (_attachments.Remove(vm))
            {
                RefreshAttachmentChips();
                await ShowInlineToastAsync($"Couldn't read {vm.Title}");
            }
            return;
        }

        vm.IsReady = true;
    }

    private void OnWindowSuggestionsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingWindowSelection)
            return;

        if (e.AddedItems.OfType<object>().FirstOrDefault() is not WindowInfo window)
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
            SlashSuggestionsList.ScrollIntoView(selected);
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
            ConversationList.ScrollIntoView(selected);
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

        if (e.AddedItems.OfType<object>().FirstOrDefault() is not ConversationItemVm item)
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

        ChatEntry.CaretIndex = nextText.Length;
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
                Views.Settings.SettingsWindow.OpenWindow(_services);
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
                    saved ? confirmation : "System: couldn't save (set an embedding provider in Settings).",
                    isSystemNote: true));
                RefreshMessageAreaHeight();
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
                RefreshMessageAreaHeight();
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

        RefreshConversationTitle();
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

        var hasRealMessages = stored.Any(m => !m.IsSystemNote && !string.IsNullOrWhiteSpace(m.Text));
        if (!hasRealMessages && string.IsNullOrWhiteSpace(_currentConversation.Title))
            return;

        if (string.IsNullOrWhiteSpace(_currentConversation.Title) && hasRealMessages)
        {
            var firstUser = Messages.FirstOrDefault(m => m.IsUser && !string.IsNullOrWhiteSpace(m.Text));
            _currentConversation.Title = firstUser is not null ? Ellipsize(firstUser.Text, 40) : "Conversation";
            RefreshConversationTitle();
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
        RefreshMessageAreaHeight();
        RefreshConversationTitle();
    }

    // Show the conversation switcher inside the message list.
    private void ShowConversationList()
    {
        EnsureConversationLoaded();
        PersistCurrentConversation();
        BuildConversationItems();
        _listMode = true;
        ConversationList.ItemsSource = _conversationItems;
        RefreshMessageAreaHeight();
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
            openCommand: new RelayCommand(NewConversation),
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
                openCommand: new RelayCommand(() => OpenConversation(id)),
                deleteCommand: new RelayCommand(() => DeleteConversation(id))));
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
        RefreshConversationTitle();
    }

    private void DeleteConversation(string id)
    {
        _conversationStore.Delete(id);
        if (id == _currentConversation?.Id)
        {
            _currentConversation = new Conversation();
            Messages.Clear();
            RefreshConversationTitle();
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
        RefreshMessageAreaHeight();
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

    // Maps a memory source to a citation with open-commands for whichever of its files exist. The
    // commands now fire from a Blazor click handler, so they hop to the MAUI dispatcher: OpenSourceAsync
    // can animate the inline toast, which is a native Border.
    private CitationVm ToCitationVm(MemoryCitation citation)
    {
        var openImage = string.IsNullOrWhiteSpace(citation.ImagePath)
            ? null
            : new RelayCommand(() => Dispatcher.UIThread.Post(() => _ = OpenSourceAsync(citation.ImagePath!)));
        var openText = string.IsNullOrWhiteSpace(citation.TextPath)
            ? null
            : new RelayCommand(() => Dispatcher.UIThread.Post(() => _ = OpenSourceAsync(citation.TextPath!)));
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

            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            var launcher = TopLevel.GetTopLevel(this)?.Launcher;
            if (storage is null || launcher is null)
                return;

            if (await storage.TryGetFileFromPathAsync(path) is { } file)
                await launcher.LaunchFileAsync(file);
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

            var launcher = TopLevel.GetTopLevel(this)?.Launcher;
            var opened = launcher is not null && await launcher.LaunchUriAsync(homeUri);
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

            // Shutter flourish + sound the moment the pixels are ours, not after the (slower,
            // network-bound) embedding — the feedback is about the capture, not about storing it.
            _host.SignalCapture();

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
                RefreshMessageAreaHeight();
                ScrollToLatest();
                PersistCurrentConversation();
            }
        }
        catch (Exception ex)
        {
            await ShowInlineToastAsync($"⚠️ {ex.Message}");
        }
    }

    // --- File drops on the panel ---

    // Dropping onto the panel itself is the other half of the ring drop target: same payload, same
    // AttachFiles, and it works in both placements because the handlers are on the panel's own Border
    // (the host only differs in which window owns it).
    //
    // Avalonia routes drag events itself, so the whole WinUI escape is gone: no platform view, no
    // AllowDrop plumbing, no transparent-background trick to make an empty Panel hit-testable, and no
    // deferral - IDataTransfer is already materialised, so the drop handler needs no async lifetime
    // games. The one casualty is DragUIOverride's "Add to this chat" / "Remember in Floaty" cursor
    // caption, which Avalonia has no equivalent for; the border highlight still signals the drop.
    private void OnPanelDragEnter(object? sender, DragEventArgs e) => HandlePanelDragOver(e);

    private void OnPanelDragOver(object? sender, DragEventArgs e) => HandlePanelDragOver(e);

    private void HandlePanelDragOver(DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(DataFormat.File))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        e.DragEffects = DragDropEffects.Copy;
        // Same reason as the ring: an OLE drop target can't be click-through.
        _host.KeepInteractiveFor(DragInteractiveGrace);
        BeginPanelDropFeedback();
        e.Handled = true;
    }

    private void OnPanelDragLeave(object? sender, DragEventArgs e) => EndPanelDropFeedback();

    private void OnPanelDrop(object? sender, DragEventArgs e)
    {
        try
        {
            if (!e.DataTransfer.Contains(DataFormat.File))
                return;

            var memorize = IsMemorizeDrop(e);

            var paths = e.DataTransfer.TryGetFiles()?
                .Select(f => f.TryGetLocalPath())
                .Where(p => !string.IsNullOrEmpty(p))
                .Select(p => p!)
                .ToList() ?? [];

            var files = paths.Where(p => !Directory.Exists(p)).ToList();

            if (files.Count == 0)
            {
                if (paths.Count > 0)
                    ShowFolderDropHint();
            }
            else if (memorize)
            {
                MemorizeFiles(files);
            }
            else
            {
                AttachFiles(files);
            }

            e.Handled = true;
        }
        catch
        {
            // A malformed clipboard payload must not take down the panel.
        }
        finally
        {
            EndPanelDropFeedback();
        }
    }

    /// <summary>
    /// Alt held during the drag switches the drop from prompt context to memory. Alt is the one
    /// modifier the shell doesn't already spend on the drop effect (Ctrl copy, Shift move), so it
    /// can't be confused with asking the source app for a different operation.
    /// </summary>
    internal static bool IsMemorizeDrop(DragEventArgs e) => e.KeyModifiers.HasFlag(KeyModifiers.Alt);

    // Highlights the panel's border while a file hovers it. Border-only on purpose: anything that
    // changes the panel's size would trigger a window resize mid-drag.
    private void BeginPanelDropFeedback()
    {
        _panelDropWatchdog ??= CreatePanelDropWatchdog();
        _panelDropWatchdog.Stop();
        _panelDropWatchdog.Start();

        if (_panelDropActive)
            return;

        _panelDropActive = true;
        PanelBorder.BorderBrush = _accentBrush;
        PanelBorder.BorderThickness = new Thickness(2);
    }

    private void EndPanelDropFeedback()
    {
        _panelDropWatchdog?.Stop();

        if (!_panelDropActive)
            return;

        _panelDropActive = false;
        PanelBorder.BorderBrush = DefaultPanelStroke;
        PanelBorder.BorderThickness = new Thickness(1);
    }

    // Anim.OneShot stops the timer on tick: Avalonia's DispatcherTimer has no IsRepeating.
    private DispatcherTimer CreatePanelDropWatchdog() =>
        Anim.OneShot(PanelDropFeedbackTimeoutMs, EndPanelDropFeedback);

    // Inline status toast under the input row. Height + opacity are animated together so the panel
    // grows/shrinks smoothly instead of snapping when short status messages appear.
    private async Task ShowInlineToastAsync(string message)
    {
        var version = ++_inlineToastVersion;
        InlineToastLabel.Text = message;

        InlineToastPanel.IsVisible = true;

        var wasHidden = InlineToastPanel.Opacity <= 0.01 || InlineToastPanel.Height <= 0.5;
        if (wasHidden)
        {
            InlineToastPanel.Opacity = 0;
            InlineToastPanel.Height = 0;
            _inlineToastSlide.Y = -4;

            await Task.WhenAll(
                Anim.RunAsync(0, 1, (int)InlineToastInMs, Anim.CubicOut, v => InlineToastPanel.Opacity = v),
                Anim.RunAsync(-4, 0, (int)InlineToastInMs, Anim.CubicOut, v => _inlineToastSlide.Y = v),
                Anim.RunAsync(0, InlineToastHeightDip, (int)InlineToastInMs, Anim.CubicOut,
                    v => InlineToastPanel.Height = v));
        }
        else
        {
            InlineToastPanel.Opacity = 1;
            InlineToastPanel.Height = InlineToastHeightDip;
            _inlineToastSlide.Y = 0;
        }

        await Task.Delay(InlineToastHoldMs);
        if (version != _inlineToastVersion || !IsOpen)
            return;

        var startHeight = InlineToastPanel.Height;
        await Task.WhenAll(
            Anim.RunAsync(1, 0, (int)InlineToastOutMs, Anim.CubicIn, v => InlineToastPanel.Opacity = v),
            Anim.RunAsync(0, -3, (int)InlineToastOutMs, Anim.CubicIn, v => _inlineToastSlide.Y = v),
            Anim.RunAsync(startHeight, 0, (int)InlineToastOutMs, Anim.CubicIn, v => InlineToastPanel.Height = v));

        if (version != _inlineToastVersion)
            return;

        HideInlineToast(immediate: true);
    }

    private void HideInlineToast(bool immediate)
    {
        // Bumping the version is what cancels an in-flight toast: every await in ShowInlineToastAsync
        // re-checks it. MAUI's CancelAnimations had to be called because its animations were owned by
        // the framework; Anim tweens are plain awaited loops.
        _inlineToastVersion++;

        if (!immediate)
            return;

        InlineToastPanel.IsVisible = false;
        InlineToastPanel.Opacity = 0;
        InlineToastPanel.Height = 0;
        _inlineToastSlide.Y = -4;
        InlineToastLabel.Text = string.Empty;
    }

    private void OnSlashSuggestionsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingSlashSelection)
            return;

        if (e.AddedItems.OfType<object>().FirstOrDefault() is not SlashCommand command)
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

    // Three WinUI escapes used to live here and are all gone:
    //   - a KeyDown hook on the native TextBox (Avalonia raises KeyDown on TextBox itself),
    //   - AllowDrop=false so the input row didn't swallow file drops as pasted text (Avalonia's
    //     TextBox is not a drop target unless asked),
    //   - repointing TextControlBorderBrushFocused / ListViewItemSelectionIndicatorBrush at the
    //     accent, which is now a Style in ChatPanelView.axaml.

    // Shows a diagonal resize cursor over the corner grip, matching the corner it occupies. MAUI had
    // no cursor API at all, so this was reflection into WinUI's protected UIElement.ProtectedCursor.
    private void ApplyResizeGripCursor() =>
        ResizeCornerGrip.Cursor = new Cursor(_onLeft
            ? StandardCursorType.TopLeftCorner
            : StandardCursorType.TopRightCorner);

    // Keyboard handling for the input box. MAUI had no key events on Entry at all, so this used to
    // reach through to the WinUI TextBox and switch on Windows.System.VirtualKey.
    private void OnChatEntryKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Tab:
                if (TryAttachSelectedWindow() || TryAutocompleteSelectedCommand())
                    e.Handled = true;
                return;

            case Key.Down:
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

            case Key.Up:
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

            case Key.Enter:
                if (_listMode)
                {
                    OpenSelectedConversation();
                    e.Handled = true;
                }
                else
                {
                    // MAUI got this from Entry.Completed (ReturnType="Send").
                    e.Handled = true;
                    OnSendClicked(sender, e);
                }
                return;

            case Key.Escape:
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

    private async void OnSendClicked(object? sender, RoutedEventArgs e)
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

        // Attachments ride along on this message only; the chips clear on send.
        var attachments = _attachments.ToList();
        _attachments.Clear();
        RefreshAttachmentChips();

        // Send is the commit point for the per-chip persist toggles: up to here nothing was written,
        // so toggling off is free and there's never anything to un-remember. Fire-and-forget so the
        // embedding round-trip doesn't sit between the user pressing Enter and the model replying.
        _ = PersistDropsAsync(attachments);

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
        RefreshMessageAreaHeight();
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

        // The turn is over — errors included, since "it stopped, come look" is the useful signal.
        _sounds.Play(FloatySound.AssistantDone);

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

        await Dispatcher.UIThread.InvokeAsync(() =>
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

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ExecApprovalPanel.IsVisible = false;
            var note = approved ? $"⚡ Ran in {request.ShellName}: {request.Command}"
                                : $"🚫 Declined: {request.Command}";
            Messages.Add(new ChatMessageVm(isUser: false, note, isSystemNote: true));
            RefreshMessageAreaHeight();
            ScrollToLatest();
        });

        return approved;
    }

    private void OnExecApprovalRunClicked(object? sender, RoutedEventArgs e) => ResolveExecApproval(true);

    private void OnExecApprovalCancelClicked(object? sender, RoutedEventArgs e) => ResolveExecApproval(false);

    private void ResolveExecApproval(bool approved)
    {
        var pending = _pendingExecApproval;
        _pendingExecApproval = null;
        ExecApprovalPanel.IsVisible = false;
        pending?.TrySetResult(approved);
    }

    // The outgoing user message: plain text, or multimodal when windows were tagged with @, files were
    // dropped, or a selection rode in on the summon hotkey — each attachment contributes its text and,
    // for images, the raw bytes.
    private static async Task<ChatMessage> BuildUserMessageAsync(
        string prompt,
        List<PromptAttachmentVm> attachments)
    {
        if (attachments.Count == 0)
            return new ChatMessage(ChatRole.User, prompt);

        var contents = new List<AIContent> { new TextContent(prompt) };

        // Per-attachment caps aren't enough on their own: ten chips at MaxAttachmentChars each would
        // swamp the context window, so the turn gets a shared budget too.
        var remaining = MaxTotalAttachmentChars;

        foreach (var attachment in attachments)
        {
            if (attachment.Kind == AttachmentKind.Selection)
            {
                contents.Add(new TextContent(
                    $"[Selected text from {attachment.SourceTitle}]\n"
                    + TakeBudget(attachment.SelectionText ?? string.Empty, ref remaining)));
                continue;
            }

            if (attachment.Kind == AttachmentKind.File)
            {
                DroppedFile? file = null;
                try
                {
                    file = attachment.IngestTask is null ? null : await attachment.IngestTask;
                }
                catch
                {
                    // Unreadable files were already removed + toasted by FinishFileAttachmentAsync.
                }

                if (file is null)
                    continue;

                var header = $"[Attached file: {file.FileName} ({file.MimeType}, {FormatSize(file.SizeBytes)})]";
                contents.Add(new TextContent(file.TextExtracted
                    ? $"{header}\n{TakeBudget(file.Text, ref remaining)}"
                    // Say so explicitly rather than attaching an empty body: the model can still ask
                    // about the file, and "no text" is information.
                    : $"{header} — text could not be extracted from this file."));

                // Dropped images can be JPEG/WebP/GIF/…, so the mime has to come from the file.
                if (file.ImageBytes is { Length: > 0 })
                    contents.Add(new DataContent(file.ImageBytes, file.MimeType));

                continue;
            }

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

            contents.Add(new TextContent(
                $"[Attached window: {capture.WindowTitle}]\n{TakeBudget(capture.Content, ref remaining)}"));

            if (!string.IsNullOrEmpty(capture.ImagePath) && File.Exists(capture.ImagePath))
            {
                contents.Add(new DataContent(
                    await File.ReadAllBytesAsync(capture.ImagePath),
                    MimeTypes.FromPath(capture.ImagePath)));
            }
        }

        return new ChatMessage(ChatRole.User, contents);
    }

    // Trims one attachment's body to its own cap and to whatever is left of the turn's shared budget.
    private static string TakeBudget(string text, ref int remaining)
    {
        var limit = Math.Min(MaxAttachmentChars, Math.Max(0, remaining));
        if (text.Length <= limit)
        {
            remaining -= text.Length;
            return text;
        }

        remaining -= limit;
        return text[..limit] + "…";
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):0.#} MB",
        >= 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes} B",
    };

    /// <summary>
    /// Writes the dropped files whose chip toggle was on into <c>~/.floaty/drops</c> and embeds them
    /// into memory. Best-effort throughout: this runs after the message has already been sent, so a
    /// failure here must never surface as a chat error.
    /// </summary>
    private async Task PersistDropsAsync(List<PromptAttachmentVm> attachments)
    {
        foreach (var attachment in attachments)
        {
            if (attachment.Kind != AttachmentKind.File || !attachment.Persist)
                continue;

            try
            {
                var file = attachment.IngestTask is null ? null : await attachment.IngestTask;
                if (file is not null)
                    await MemorizeDropAsync(file);
            }
            catch
            {
                // Memory persistence is best-effort; the file already rode along on the prompt.
            }
        }
    }

    // Writes one dropped file into ~/.floaty/drops and embeds it. Returns false when there was
    // nothing worth keeping or memory declined it (no API key). Throws on hard failures so the
    // Alt-drop path can report them; PersistDropsAsync swallows them by design.
    private async Task<bool> MemorizeDropAsync(DroppedFile file)
    {
        var capture = WriteDropToDisk(file);
        if (capture is null)
            return false;

        return await _memoryService.RememberCaptureAsync(capture, IMemoryService.DroppedFileSource);
    }

    // Copies a dropped file into ~/.floaty/drops alongside a .txt holding its extracted text, shaped
    // as a CaptureResult so it flows through the same memory pipeline as screen captures. Returns
    // null when there is nothing worth keeping.
    private static CaptureResult? WriteDropToDisk(DroppedFile file)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var baseName = $"drop-{stamp}-{SanitizeFileName(Path.GetFileNameWithoutExtension(file.FileName))}";

        // Only images need their bytes kept — they're what the vision model describes. For everything
        // else the extracted text is the memory, and copying a 30 MB original buys nothing.
        var imagePath = string.Empty;
        if (file.ImageBytes is { Length: > 0 })
        {
            imagePath = Path.Combine(FloatyPaths.Drops, baseName + Path.GetExtension(file.FileName));
            File.WriteAllBytes(imagePath, file.ImageBytes);
        }

        if (string.IsNullOrWhiteSpace(file.Text) && imagePath.Length == 0)
            return null;

        // The header mirrors the capture .txt format, and carries the original path — "that PDF from
        // my desktop" is exactly how people search for these later.
        var textPath = Path.Combine(FloatyPaths.Drops, baseName + ".txt");
        var body = new StringBuilder()
            .Append("File: ").AppendLine(file.FileName)
            .Append("Source: ").AppendLine(file.SourcePath)
            .Append("Type: ").AppendLine(file.MimeType)
            .Append("Dropped: ").AppendLine(DateTime.Now.ToString("u"))
            .AppendLine("----")
            .AppendLine(file.Text)
            .ToString();
        File.WriteAllText(textPath, body);

        return new CaptureResult(imagePath, textPath, file.FileName, body);
    }

    // Keeps persisted names filesystem-safe and short enough that the ~/.floaty/drops path stays sane.
    private static string SanitizeFileName(string name)
    {
        var cleaned = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        cleaned = cleaned.Trim();
        if (cleaned.Length == 0)
            return "file";
        return cleaned.Length <= 60 ? cleaned : cleaned[..60];
    }

    private void ScrollToLatest()
    {
        if (Messages.Count > 0)
            Dispatcher.UIThread.Post(MessagesScroller.ScrollToEnd, DispatcherPriority.Background);
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
    public void KeepInteractiveFor(TimeSpan duration) { }
    public void MoveWindowBy(double dxDip, double dyDip) { }
    public void CollapseRequested() { }
    public void SetBusy(bool busy) { }
    public void SignalCapture() { }
}
