using Floaty.Services;

namespace Floaty;

/// <summary>
/// The floating swim ring: a borderless, always-on-top, click-through window the user drags around.
/// It also hosts the chat panel when <see cref="ChatPanelPlacement.Floating"/> is configured — see
/// <see cref="IChatPanelHost"/> for the seam, and <see cref="ChatWindowHost"/> for the fixed placement.
/// </summary>
public partial class OverlayPage : ContentPage, IChatPanelHost, IRingFeedback
{
    private readonly IOverlayWindowController _windowController;
    private readonly SettingsService _settings;
    private readonly IServiceProvider _services;

    // Current ring diameter in device-independent units, driven by the user's setting (Appearance
    // slider / Ctrl+scroll). All window dimensions below are derived from it so the window keeps
    // hugging the ring as its size changes.
    private double _ringSize = SettingsService.RingDefaultSize;

    // Extras layered on top of the ring diameter to derive window dimensions. Chosen so the historical
    // 148-dip ring reproduces the original 150×250 compact window and 196-dip chat base height.
    private const double CompactWidthPadding = 2;  // 150 - 148
    private const double CompactHeightExtra = 102; // 250 - 148
    private const double ChatBaseExtra = 48;       // 196 - 148

    // Ring image width (matches the Ring's WidthRequest). The compact window hugs the ring so it sits
    // flush against both window edges, letting the chat panel open to either side with the ring
    // staying visually put.
    private double RingWidthDip => _ringSize;

    // Compact (chat closed) overlay window size, in device-independent units; grows with the ring.
    private double CompactWidth => _ringSize + CompactWidthPadding;
    private double CompactHeight => _ringSize + CompactHeightExtra;

    // Compact window size for a given ring diameter, so the initial window (App.CreateWindow) can be
    // sized from the persisted setting before the page exists.
    public static (double Width, double Height) CompactWindowSizeFor(double ringSize) =>
        (ringSize + CompactWidthPadding, ringSize + CompactHeightExtra);

    // Height reserved for the ring + action bar (everything below the chat panel). The chat window
    // height is this plus the chat panel's own measured height, so the window grows with the panel.
    // Grows with the ring so a larger ring still gets the room it needs at the window's base.
    private double ChatBaseHeight => _ringSize + ChatBaseExtra;

    // --- Chat placement ---

    // Where the chat panel currently lives. Read from config at construction and re-applied when the
    // user changes it in Settings (see OnSettingsChanged), without restarting the app.
    private ChatPanelPlacement _placement;

    // Floating placement: the panel shares this window and sits in a side column.
    private ChatPanelView? _panel;

    // Fixed placement: the panel has its own window, managed by this host.
    private ChatWindowHost? _chatWindow;

    // Which side of the ring the chat panel currently occupies (floating placement). Chosen from
    // available screen space when the chat opens, and re-evaluated after a drag/summon.
    private bool _chatOnLeft;

    // Last window height we requested from the panel, to avoid redundant resizes / oscillation.
    private double _lastChatWindowHeight;

    // True while the open/collapse animation is running, so size requests don't fight the animated resize.
    private bool _chatAnimating;

    // How many degrees the ring "rolls" per device-independent unit dragged horizontally.
    private const double RotationPerDip = 0.6;

    // Constant idle spin: a slow, subtle rotation while the ring is otherwise at rest.
    private const double IdleSpinDegPerSecond = 9;
    private const int IdleSpinIntervalMs = 33; // ~30 fps
    private IDispatcherTimer? _idleSpinTimer;

    // Debounces persisting the ring size to config while the user drags/scrolls, so we write once
    // the gesture settles rather than on every wheel notch.
    private IDispatcherTimer? _ringSizePersistTimer;

#if WINDOWS
    // Mouse wheel tuning: each wheel delta unit rotates this many degrees, then idle spin
    // resumes after a short period without wheel activity.
    private const double WheelRotationPerDelta = 0.15;
    // Ctrl+scroll resizes instead of spinning: this many device-independent units per wheel notch.
    private const double RingSizeWheelStep = 10;
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

    // Cumulative pan offset reported on the previous PanUpdated event, used to derive per-frame deltas.
    private double _lastTotalX;
    private double _lastTotalY;

    public OverlayPage(
        IOverlayWindowController windowController,
        SettingsService settings,
        IServiceProvider services)
    {
        InitializeComponent();
        _windowController = windowController;
        _settings = settings;
        _services = services;

        Ring.HandlerChanged += OnRingHandlerChanged;

        _settings.Changed += OnSettingsChanged;
        _settings.RingSizePreviewRequested += OnRingSizePreviewRequested;
        _settings.AccentColorPreviewRequested += OnAccentColorPreviewRequested;

        _placement = _settings.Current.ChatPanelPlacement;
        BuildChatHost();

        ApplyRingImage();
        ApplyRingSize(_settings.Current.RingSize);
        ApplyAccentColor(_settings.Current.AccentColor);
        ApplyAlwaysOnTopMenuState();
        ApplyAlwaysOnTop(_settings.Current.AlwaysOnTop);

        // Summon (Alt+F): glide the window to the mouse with a ring spin.
        _windowController.SummonRequested += OnSummonRequested;

        // Click-through: tell the native window which regions are interactive so mouse input over
        // the transparent rest of the window falls through to the apps behind.
        _windowController.SetInteractiveHitTest(IsInteractiveAt);

        StartIdleSpin();
    }

    // --- Chat host lifecycle (placement) ---

    // Creates the panel for the configured placement: either inline in this window's side column, or
    // in its own window managed by ChatWindowHost. Idempotent per placement — BuildChatHost is only
    // called from the constructor and from a placement change, which tears the previous one down first.
    private void BuildChatHost()
    {
        if (_placement == ChatPanelPlacement.Fixed)
        {
            _chatWindow = new ChatWindowHost(_services, _settings, this);
            return;
        }

        _panel = _services.GetRequiredService<ChatPanelView>();
        _panel.Attach(this, _settings.Current.ChatWindowWidth);
        _panel.IsVisible = false;
        _panel.VerticalOptions = LayoutOptions.Start;
        RootGrid.Children.Add(_panel);
        ApplyChatSide(onLeft: false);
    }

    private void TearDownChatHost()
    {
        if (_chatWindow is not null)
        {
            _chatWindow.Close();
            _chatWindow = null;
        }

        if (_panel is not null)
        {
            _panel.Detach();
            RootGrid.Children.Remove(_panel);
            _panel = null;
        }

        _chatAnimating = false;
        _lastChatWindowHeight = 0;
        ResizeWindowToRing();
    }

    // True when a chat panel is currently open in whichever placement is active.
    private bool IsChatOpen => _placement == ChatPanelPlacement.Fixed
        ? _chatWindow?.IsOpen == true
        : _panel?.IsOpen == true;

    // Padding around the ring's hit-rect so the ~50 ms click-through poll can't eat clicks
    // landing right on its edge while the cursor is still approaching.
    private const double InteractiveEdgeSlopDip = 4;

    // Called from the native click-through poll (UI thread) with window-client DIP coordinates.
    // Anything outside these regions lets mouse input pass through to the windows behind.
    private bool IsInteractiveAt(double x, double y)
    {
        if (_chatAnimating)
            return true; // bounds are in flux mid open/close animation

        var ring = Ring.BoundsInPage();
        ring = new Rect(
            ring.X - InteractiveEdgeSlopDip,
            ring.Y - InteractiveEdgeSlopDip,
            ring.Width + (2 * InteractiveEdgeSlopDip),
            ring.Height + (2 * InteractiveEdgeSlopDip));
        if (ring.Contains(x, y))
            return true;

        return _panel is not null && _panel.IsInteractiveAt(x, y);
    }

    private void OnSettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.Dispatch(() =>
        {
            // A placement change swaps which window owns the chat; do it before the ring resize below
            // so the window ends up at the size the new placement wants.
            var placement = _settings.Current.ChatPanelPlacement;
            if (placement != _placement)
            {
                TearDownChatHost();
                _placement = placement;
                BuildChatHost();
            }

            ApplyRingImage();
            ApplyRingSize(_settings.Current.RingSize);
            ApplyAccentColor(_settings.Current.AccentColor);
            ApplyAlwaysOnTopMenuState();
            ApplyAlwaysOnTop(_settings.Current.AlwaysOnTop);
        });

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

    // Apply a ring diameter (clamped): resize the ring image and the overlay window to match, so the
    // window keeps hugging the ring. Used for the initial size, saved changes, live slider preview,
    // and Ctrl+scroll.
    private void ApplyRingSize(double size)
    {
        _ringSize = SettingsService.ClampRingSize(size);
        Ring.WidthRequest = _ringSize;
        Ring.HeightRequest = _ringSize;
        ResizeWindowToRing();
    }

    // Live preview from the Appearance slider: apply the size without persisting (the settings page
    // reverts to the saved value when it closes without a Save).
    private void OnRingSizePreviewRequested(object? sender, double size) =>
        Dispatcher.Dispatch(() => ApplyRingSize(size));

    // Live preview from the Appearance accent picker: apply without persisting (the settings page
    // reverts to the saved value when it closes without a Save).
    private void OnAccentColorPreviewRequested(object? sender, string hex) =>
        Dispatcher.Dispatch(() => ApplyAccentColor(hex));

    // Recolor accent surfaces: the send button and slash-menu icon resolve via DynamicResource. The
    // keys live on the Application (see App.xaml) rather than this page, so the fixed chat window —
    // a separate window with its own visual tree — resolves the same values.
    private void ApplyAccentColor(string? hex)
    {
        var palette = AccentPalette.From(hex);
        var resources = Application.Current?.Resources ?? Resources;
        resources["AccentColor"] = Color.FromArgb(palette.Base);
        resources["AccentIconOnDarkColor"] = Color.FromArgb(palette.IconOnDark);
    }

    // Keep every Floaty window at the same z-order level.
    private void ApplyAlwaysOnTop(bool alwaysOnTop)
    {
        _windowController.SetAlwaysOnTop(alwaysOnTop);
        _chatWindow?.SetAlwaysOnTop(alwaysOnTop);
    }

    // Resize the overlay window to fit the current ring. While compact the window hugs the ring
    // (grown from its bottom-center so the ring stays put); while a floating chat is open the ring's
    // base region grows with ChatBaseHeight, keeping the ring's flush edge anchored. With the fixed
    // placement the window always stays compact — the panel is in a window of its own.
    private void ResizeWindowToRing()
    {
        if (_chatAnimating)
            return;

        if (_panel is not null && _panel.IsOpen)
        {
            var panelHeight = _panel.Height > 0 ? _panel.Height : 80;
            var target = ChatBaseHeight + panelHeight;
            _lastChatWindowHeight = target;
            _windowController.Resize(_panel.PanelWidth, target, ChatAnchor);
        }
        else
        {
            _windowController.Resize(CompactWidth, CompactHeight, WindowAnchor.Center);
        }
    }

    // Persist the current ring size to config, debounced so a drag/scroll gesture writes once it
    // settles rather than on every wheel notch.
    private void SchedulePersistRingSize()
    {
        _ringSizePersistTimer ??= CreateRingSizePersistTimer();
        _ringSizePersistTimer.Stop();
        _ringSizePersistTimer.Start();
    }

    private IDispatcherTimer CreateRingSizePersistTimer()
    {
        var timer = Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(500);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var config = _settings.Current;
            if (Math.Abs(config.RingSize - _ringSize) < 0.5)
                return;
            config.RingSize = _ringSize;
            _settings.Save(config);
        };
        return timer;
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
                // A fast drag can outrun the ring's hit-rect; keep the window input-opaque until release.
                _windowController.SetForceInteractive(true);
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
                _windowController.SetForceInteractive(false);
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

        // With its own window the panel doesn't travel with the ring: just bring it up where it lives.
        if (_placement == ChatPanelPlacement.Fixed)
        {
            _chatWindow?.Show();
            return;
        }

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

        _ringBusy = true;
        _chatWaitingSpinCts = new CancellationTokenSource();
        _ = RunChatWaitingSpinAsync(_chatWaitingSpinCts.Token);
    }

    private void StopChatWaitingSpin()
    {
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
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        while (stopwatch.ElapsedMilliseconds < durationMs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var t = Math.Clamp(stopwatch.Elapsed.TotalMilliseconds / durationMs, 0, 1);
            Ring.Rotation = start + deltaDegrees * easing.Ease(t);
            await Task.Delay(16, cancellationToken);
        }

        Ring.Rotation = start + deltaDegrees;
    }

    // --- Opening / closing the chat ---

    // Toggle the chat: the slide-out panel in this window, or the standalone chat window.
    private void ToggleChat()
    {
        if (_placement == ChatPanelPlacement.Fixed)
        {
            _chatWindow?.Toggle();
            return;
        }

        if (_panel is null)
            return;

        if (_panel.IsOpen)
            CollapseChat();
        else
            _ = ShowChatAsync();
    }

    // Open the chat panel (idempotent — a no-op if already open). Only the input row shows until
    // messages exist; the panel's size changes grow the window from here as the messages area expands.
    // The side (left/right of the ring) is chosen from available screen space; the window then grows
    // away from the ring (ChatAnchor keeps the ring's edge fixed) so the ring stays put.
    private async Task ShowChatAsync()
    {
        if (_panel is null || _panel.IsOpen)
            return;

        // Decide the side while still compact (the window hugs the ring, so its rect is the ring's).
        ApplyChatSide(ShouldOpenOnLeft());

        _lastChatWindowHeight = 0;
        _panel.BeginOpen();
        _windowController.Resize(_panel.PanelWidth, ChatBaseHeight + 80, ChatAnchor);

        await _panel.AnimateInAsync();
    }

    // Collapse the chat panel: animate the window down to compact, anchored at the ring's edge so
    // the panel slides shut into it while the ring stays fixed in place.
    private void CollapseChat()
    {
        if (_panel is null || !_panel.IsOpen)
            return;

        _panel.BeginClose();
        _chatAnimating = true;

        var startWidth = _panel.PanelWidth;
        var startHeight = _lastChatWindowHeight > 0 ? _lastChatWindowHeight : ChatBaseHeight + 80;

        // Collapse toward the ring: anchor the ring's current edge so the panel slides shut into it.
        var anchor = ChatAnchor;
        var panel = _panel;
        _ = panel.FadeOutAsync();
        new Animation(t => _windowController.Resize(
                startWidth + (CompactWidth - startWidth) * t,
                startHeight + (CompactHeight - startHeight) * t,
                anchor),
            0, 1, Easing.CubicIn)
            .Commit(this, "ChatCollapse", length: 220, finished: (_, _) =>
            {
                panel.EndClose();
                _lastChatWindowHeight = 0;
                _chatAnimating = false;
                _windowController.Resize(CompactWidth, CompactHeight, anchor);
            });
    }

    // --- IChatPanelHost (floating placement: the panel shares the ring's window) ---

    void IChatPanelHost.RequestPanelSize(double widthDip, double heightDip)
    {
        if (_chatAnimating || _panel is null || !_panel.IsOpen)
            return;

        var target = ChatBaseHeight + heightDip;
        _lastChatWindowHeight = target;
        // Anchor the ring's current edge so it stays put as the panel changes size.
        _windowController.Resize(widthDip, target, ChatAnchor);
    }

    double IChatPanelHost.AvailableWidthDip() => AvailableChatWidthDip();

    double IChatPanelHost.AvailableListHeightDip(double chromeDip) => AvailableChatListHeightDip(chromeDip);

    void IChatPanelHost.SetForceInteractive(bool force) => _windowController.SetForceInteractive(force);

    // The floating panel is positioned by the ring, so its drag bar is hidden and this never fires.
    void IChatPanelHost.MoveWindowBy(double dxDip, double dyDip) => _windowController.MoveBy(dxDip, dyDip);

    void IChatPanelHost.CollapseRequested() => CollapseChat();

    // --- IRingFeedback (also serves IChatPanelHost's members of the same shape) ---

    public void SetBusy(bool busy)
    {
        if (busy)
            StartChatWaitingSpin();
        else
            StopChatWaitingSpin();
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
        if (!IsChatOpen || _placement == ChatPanelPlacement.Fixed)
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

        var chatPx = (_panel?.PanelWidth ?? ChatPanelView.DefaultChatWidth) * DisplayScale;
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
            return ChatPanelView.MaxChatWidth;

        var (ringLeft, ringRight) = RingScreenEdgesPx();
        var spacePx = _chatOnLeft ? ringLeft - wa.X : (wa.X + wa.Width) - ringRight;
        return Math.Clamp(spacePx / DisplayScale, ChatPanelView.MinChatWidth, ChatPanelView.MaxChatWidth);
    }

    // The tallest the messages list may grow without pushing the window past the top of the work
    // area. The window's bottom edge is anchored, so the ceiling is the distance from the window's
    // bottom to the work-area top, minus the ring base and the panel's fixed chrome (input row,
    // padding…) captured at drag start. Returns MaxChatListHeight when the work area is unknown.
    private double AvailableChatListHeightDip(double chromeDip)
    {
        var wa = _windowController.GetWorkArea();
        if (wa.Height <= 0)
            return ChatPanelView.MaxChatListHeight;

        var (_, winY) = _windowController.GetPosition();
        var (_, winH) = _windowController.GetSize();
        var maxWindowDip = (winY + winH - wa.Y) / DisplayScale - 8; // small gap below the screen top
        return Math.Clamp(maxWindowDip - ChatBaseHeight - chromeDip,
            ChatPanelView.MinChatListHeight, ChatPanelView.MaxChatListHeight);
    }

    // Place the chat panel on the given side of the ring: swap the star/zero side columns and the
    // panel's column and overlap margin; the panel mirrors its own chevron and corner grip.
    private void ApplyChatSide(bool onLeft)
    {
        _chatOnLeft = onLeft;
        if (_panel is null)
            return;

        if (onLeft)
        {
            LeftSpace.Width = new GridLength(1, GridUnitType.Star);
            RightSpace.Width = new GridLength(0);
            Grid.SetColumn(_panel, 0);
            _panel.Margin = new Thickness(0, 10, -30, 0);
        }
        else
        {
            LeftSpace.Width = new GridLength(0);
            RightSpace.Width = new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(_panel, 2);
            _panel.Margin = new Thickness(-30, 10, 0, 0);
        }

        _panel.ApplyPanelSide(onLeft);
    }

    // After the ring moves with the chat open, flip the panel to the other side only if the current
    // side now overflows the screen and the other side has more room. Staying put unless we must
    // avoids twitchy flips when the ring hovers near the boundary. The window is shifted horizontally
    // so the ring stays visually put through the flip.
    private void ReevaluateChatSide()
    {
        if (_panel is null || !_panel.IsOpen || _chatAnimating)
            return;

        var wa = _windowController.GetWorkArea();
        if (wa.Width <= 0)
            return;

        var ring = RingScreenEdgesPx();
        var chatPx = _panel.PanelWidth * DisplayScale;
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

    // --- Ring platform hooks (Windows) ---

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
        if (!_ringPointerOver || _ringPlatformView is null)
            return;

        var delta = e.GetCurrentPoint(_ringPlatformView).Properties.MouseWheelDelta;
        if (delta == 0)
            return;

        // Ctrl+scroll resizes the ring (persisted, debounced); plain scroll spins it.
        var ctrlDown = (e.KeyModifiers & Windows.System.VirtualKeyModifiers.Control) != 0;
        if (ctrlDown)
        {
            ApplyRingSize(_ringSize + (delta / 120.0) * RingSizeWheelStep);
            SchedulePersistRingSize();
            e.Handled = true;
            return;
        }

        if (_ringBusy)
            return;

        _manualWheelResumeAtUtc = DateTime.UtcNow.AddMilliseconds(ManualWheelResumeDelayMs);

        var rotation = (Ring.Rotation + delta * WheelRotationPerDelta) % 360;
        if (rotation < 0)
            rotation += 360;
        Ring.Rotation = rotation;

        e.Handled = true;
    }
#endif

    // --- Ring context menu ---

    private void OnSettingsClicked(object? sender, EventArgs e) => SettingsPage.OpenWindow(_services);

    private void OnFloatToTaskbarClicked(object? sender, EventArgs e) =>
        _windowController.FloatToTaskbarAndHide();

    private void OnAlwaysOnTopClicked(object? sender, EventArgs e)
    {
        var config = _settings.Current;
        config.AlwaysOnTop = !config.AlwaysOnTop;
        _settings.Save(config);
        ApplyAlwaysOnTop(config.AlwaysOnTop);
        ApplyAlwaysOnTopMenuState();
    }

    private void ApplyAlwaysOnTopMenuState() =>
        AlwaysOnTopMenuItem.IconImageSource = (FontImageSource)Resources[
            _settings.Current.AlwaysOnTop ? "AlwaysOnTopOnIcon" : "AlwaysOnTopOffIcon"];

    private void OnCloseClicked(object? sender, EventArgs e) =>
        Application.Current?.Quit();

    private void OnRingTapped(object sender, TappedEventArgs e) => ToggleChat();

}
