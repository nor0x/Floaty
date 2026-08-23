using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Floaty.IconFont;
using Floaty.Services;
using Floaty.Ui;

namespace Floaty.Views;

/// <summary>
/// The floating swim-ring overlay. Unlike the MAUI version this is the top-level <see cref="Window"/>
/// itself rather than a page hosted in one, so there is no window/page pair to keep in sync.
/// </summary>
/// <remarks>
/// Phase 3 scope: everything the ring itself does - drag with roll, wheel spin and Ctrl+wheel resize,
/// idle spin, the summon flourish, the capture shutter, the context menu and file drops. The chat
/// panel it hosts arrives in phase 4 and the settings window in phase 5; those call sites are marked
/// TODO below.
/// </remarks>
public partial class OverlayWindow : Window, IRingFeedback
{
    // Compact (chat closed) overlay window size, in device-independent units; grows with the ring.
    // The window hugs the ring so it sits flush against both window edges, letting the chat panel
    // open to either side with the ring staying visually put.
    private const double CompactWidthPadding = 2;   // 150 - 148
    private const double CompactHeightExtra = 102;  // 250 - 148

    // The window reports no work area or size until it has been composited, so the position restore
    // polls for a usable answer instead of assuming one is available at startup.
    private const int OverlayRestoreRetryMs = 100;
    private const int OverlayRestoreMaxAttempts = 20;

    // How many degrees the ring "rolls" per device-independent unit dragged horizontally.
    private const double RotationPerDip = 0.6;

    // Constant idle spin: a slow, subtle rotation while the ring is otherwise at rest.
    private const double IdleSpinDegPerSecond = 9;
    private const int IdleSpinIntervalMs = 33; // ~30 fps

    // Mouse wheel tuning: each wheel notch rotates this many degrees, then the idle spin resumes
    // after a short period without wheel activity.
    private const double WheelRotationPerNotch = 18;
    // Ctrl+scroll resizes instead of spinning: this many device-independent units per wheel notch.
    private const double RingSizeWheelStep = 10;
    private const int ManualWheelResumeDelayMs = 400;

    // The window glides for SummonMoveMs; the ring keeps spinning longer (SummonSpinMs) and
    // decelerates to rest, so it carries momentum after the window has arrived.
    private const int SummonMoveMs = 480;
    private const int SummonSpinMs = 1000;
    private const int SummonRevealDelayMs = 180;

    // Capture shutter: wind up, snap closed with a twist, hold, open, settle.
    private const double ShutterWindUpScale = 1.06;
    private const double ShutterClosedScale = 0.82;
    private const double ShutterOpenScale = 1.05;
    private const double ShutterClosedOpacity = 0.65;
    private const double ShutterFlashOpacity = 0.85;
    private const double ShutterTwistDegrees = 26;
    private const int ShutterWindUpMs = 110;
    private const int ShutterCloseMs = 90;
    private const int ShutterHoldMs = 60;
    private const int ShutterOpenMs = 150;
    private const int ShutterSettleMs = 120;

    // How long each drag-over event keeps the window input-opaque. Comfortably longer than the 50ms
    // click-through poll, short enough that an abandoned drag frees the window almost immediately.
    private static readonly TimeSpan DragInteractiveGrace = TimeSpan.FromMilliseconds(400);

    // How much the ring swells while a file hovers it, and how long the watchdog waits before undoing
    // that swell - a drag that ends in another app never delivers a leave event.
    private const double RingDropScale = 1.14;
    private const int RingDropFeedbackTimeoutMs = 600;

    private readonly SettingsService _settings;
    private readonly ISelectionCaptureService _selectionCapture;
    private readonly ISoundService _sounds;

    private IOverlayWindowController? _windowController;

    // Ring transforms. Avalonia has no Scale/Rotation properties on controls, so the ring's angle and
    // scale live on RenderTransform objects that are mutated in place.
    private readonly ScaleTransform _ringScale = new(1, 1);
    private readonly RotateTransform _ringRotate = new(0);
    private readonly ScaleTransform _flashScale = new(1, 1);

    private double _ringSize = SettingsService.RingDefaultSize;

    // True while a drag, summon spin, shutter or drop is driving the ring, so the idle spin yields.
    private bool _ringBusy;
    private DateTime _manualWheelResumeAtUtc = DateTime.MinValue;
    private DispatcherTimer? _idleSpinTimer;

    // Drag state. The pointer position Avalonia reports is relative to this window, which is itself
    // moving under the cursor, so deltas are taken in screen space instead.
    private bool _dragging;
    private PixelPoint _lastPointerScreen;

    private DispatcherTimer? _ringSizePersistTimer;
    private DispatcherTimer? _overlayPositionPersistTimer;
    private DispatcherTimer? _overlayPositionRestoreTimer;
    private int _overlayPositionRestoreAttempts;
    private bool _overlayPositionRestored;

    private CancellationTokenSource? _shutterCts;
    private double _shutterRestRotation;

    private bool _ringDropActive;
    private DispatcherTimer? _ringDropWatchdog;

    // The selection captured on this summon, handed to the chat panel once it is on screen.
    private SelectedText? _pendingSelection;

    public OverlayWindow(
        SettingsService settings,
        ISelectionCaptureService selectionCapture,
        ISoundService sounds)
    {
        InitializeComponent();
        _settings = settings;
        _selectionCapture = selectionCapture;
        _sounds = sounds;

        Ring.RenderTransform = new TransformGroup { Children = { _ringScale, _ringRotate } };
        ShutterFlash.RenderTransform = _flashScale;

        _settings.Changed += OnSettingsChanged;
        _settings.RingSizePreviewRequested += OnRingSizePreviewRequested;
        _settings.AccentColorPreviewRequested += OnAccentColorPreviewRequested;

        ApplyRingImage();
        ApplyRingSize(_settings.Current.RingSize);
        ApplyAlwaysOnTopMenuState();
        Topmost = _settings.Current.AlwaysOnTop;

        Ring.PointerPressed += OnRingPointerPressed;
        Ring.PointerMoved += OnRingPointerMoved;
        Ring.PointerReleased += OnRingPointerReleased;
        Ring.PointerWheelChanged += OnRingPointerWheelChanged;

        // File drops. Avalonia routes these natively, so the whole WinUI Handler.PlatformView escape
        // (AllowDrop plus four platform events, and the deferral dance) is gone.
        DragDrop.SetAllowDrop(Ring, true);
        Ring.AddHandler(DragDrop.DragEnterEvent, OnRingDragEnter);
        Ring.AddHandler(DragDrop.DragOverEvent, OnRingDragOver);
        Ring.AddHandler(DragDrop.DragLeaveEvent, OnRingDragLeave);
        Ring.AddHandler(DragDrop.DropEvent, OnRingDrop);

        StartIdleSpin();
    }

    /// <summary>Ring angle in degrees. Replaces MAUI's <c>Ring.Rotation</c>.</summary>
    private double RingRotation
    {
        get => _ringRotate.Angle;
        set => _ringRotate.Angle = value;
    }

    /// <summary>Compact window size for a given ring diameter.</summary>
    public static (double Width, double Height) CompactWindowSizeFor(double ringSize) =>
        (ringSize + CompactWidthPadding, ringSize + CompactHeightExtra);

    /// <summary>
    /// Whether the point (window client coordinates, device-independent units) is over an interactive
    /// part of the overlay. Everything else lets mouse input fall through to the window behind.
    /// </summary>
    /// <remarks>
    /// Two things make the obvious <c>InputHitTest(p) is not null</c> wrong here. Avalonia's window
    /// template owns a chrome <c>Panel</c> with a Transparent background that is hit at every point in
    /// the window no matter what <c>Window.Background</c> is, so the result has to be checked for
    /// ancestry under <c>ContentRoot</c>. And <see cref="Image"/> hit-tests by its layout rect rather
    /// than by alpha, so the ring's transparent corners would otherwise count as interactive - hence
    /// the explicit circle test. (Shapes such as <c>Ellipse</c> do hit-test by geometry, so anything
    /// built from those needs no special handling.)
    /// </remarks>
    public bool IsInteractiveAt(double x, double y)
    {
        var point = new Point(x, y);

        // Note InputHitTest returns null until the first frame has been composited, which correctly
        // reads as "not interactive" during startup.
        if (this.InputHitTest(point) is not Visual hit
            || ReferenceEquals(hit, ContentRoot)
            || !hit.GetSelfAndVisualAncestors().Any(a => ReferenceEquals(a, ContentRoot)))
        {
            return false;
        }

        // The ring is the one element whose hit must not be taken at face value: an Image reports a
        // hit anywhere in its layout rect, so the ring's transparent corners would read as
        // interactive and the ring would grab clicks meant for the desktop behind it.
        if (ReferenceEquals(hit, Ring))
            return IsWithinRing(x, y);

        return true;
    }

    /// <summary>True when the point falls inside the ring's drawn disc rather than its bounding box.</summary>
    private bool IsWithinRing(double x, double y)
    {
        // Translate the ring's *centre*, not its origin. The ring carries a live RotateTransform (the
        // idle spin), and TranslatePoint applies render transforms - so the top-left corner orbits as
        // the ring turns, which would make the computed centre wander. With RenderTransformOrigin at
        // 50%,50% the centre is the fixed point of both the rotate and the scale, so it stays put.
        if (Ring.TranslatePoint(new Point(Ring.Bounds.Width / 2, Ring.Bounds.Height / 2), this)
            is not { } centre)
        {
            return false;
        }

        // The scale transform grows the disc about that centre without changing Bounds, so the drop
        // swell and the shutter pulse have to be folded into the radius or the ring loses its edges
        // exactly when it is largest.
        var radius = _ringSize / 2 * _ringScale.ScaleX;
        var dx = x - centre.X;
        var dy = y - centre.Y;
        return (dx * dx) + (dy * dy) <= radius * radius;
    }

    // --- Settings ---

    private void OnSettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            ApplyRingImage();
            ApplyRingSize(_settings.Current.RingSize);
            ApplyAlwaysOnTop(_settings.Current.AlwaysOnTop);
            ApplyAlwaysOnTopMenuState();
        });

    // Live preview from the Appearance slider: apply without persisting (the settings page reverts to
    // the saved value when it closes without a Save).
    private void OnRingSizePreviewRequested(object? sender, double size) =>
        Dispatcher.UIThread.Post(() => ApplyRingSize(size));

    private void OnAccentColorPreviewRequested(object? sender, string hex) =>
        Dispatcher.UIThread.Post(() => (Application.Current as App)?.ApplyAccentColor(hex));

    /// <summary>Loads the configured ring image, falling back to the first built-in.</summary>
    public void ApplyRingImage()
    {
        var selected = _settings.Current.RingImageFileName;

        if (_settings.IsBuiltInRingImage(selected))
        {
            Ring.Source = LoadBuiltInRing(selected!);
            return;
        }

        var selectedPath = _settings.GetRingImageFullPath(selected);
        if (selectedPath is null)
        {
            Ring.Source = LoadBuiltInRing("ring1.png");
            return;
        }

        try
        {
            Ring.Source = new Bitmap(selectedPath);
        }
        catch
        {
            // A custom ring that no longer decodes must not take the overlay down with it.
            Ring.Source = LoadBuiltInRing("ring1.png");
        }
    }

    private static Bitmap LoadBuiltInRing(string fileName) =>
        new(AssetLoader.Open(new Uri($"avares://Floaty/Resources/Images/{fileName}")));

    /// <summary>
    /// Applies a ring diameter (clamped), resizing the ring image and the window to match so the
    /// window keeps hugging the ring.
    /// </summary>
    public void ApplyRingSize(double size)
    {
        _ringSize = SettingsService.ClampRingSize(size);
        Ring.Width = _ringSize;
        Ring.Height = _ringSize;
        // The flash disc is only ever seen on top of the ring, so it tracks the same diameter.
        ShutterFlash.Width = _ringSize;
        ShutterFlash.Height = _ringSize;

        var (width, height) = CompactWindowSizeFor(_ringSize);
        Width = width;
        Height = height;
    }

    private void ApplyAlwaysOnTop(bool alwaysOnTop)
    {
        Topmost = alwaysOnTop;
        _windowController?.SetAlwaysOnTop(alwaysOnTop);
    }

    // MAUI needed a fresh FontImageSource here because mutating a realised native menu icon's glyph
    // didn't propagate; an Avalonia TextBlock is just a control, so the glyph is set directly.
    private void ApplyAlwaysOnTopMenuState() =>
        AlwaysOnTopIcon.Text = _settings.Current.AlwaysOnTop ? TablerLine.Pinned : TablerLine.PinnedOff;

    // --- Window controller binding ---

    /// <summary>
    /// Hands the overlay its native window controller once the window exists (an HWND is only
    /// available after <c>Show()</c>). Replaces MAUI's <c>OnWindowCreated</c> lifecycle hook.
    /// </summary>
    public void BindWindowController(IOverlayWindowController controller)
    {
        _windowController = controller;

        // Click-through: tell the native window which regions are interactive so mouse input over the
        // transparent rest of the window falls through to the apps behind.
        controller.SetInteractiveHitTest(IsInteractiveAt);
        controller.SetAlwaysOnTop(_settings.Current.AlwaysOnTop);
        controller.SummonRequested += OnSummonRequested;

        BeginOverlayPositionRestore();
    }

    // --- Idle spin ---

    private void StartIdleSpin()
    {
        _idleSpinTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(IdleSpinIntervalMs) };
        _idleSpinTimer.Tick += (_, _) =>
        {
            if (_ringBusy || DateTime.UtcNow < _manualWheelResumeAtUtc)
                return;
            RingRotation = (RingRotation + (IdleSpinDegPerSecond * IdleSpinIntervalMs / 1000.0)) % 360;
        };
        _idleSpinTimer.Start();
    }

    // --- Drag to move, rolling the ring ---

    private void OnRingPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        _dragging = true;
        _ringBusy = true; // pause the idle spin while dragging
        _lastPointerScreen = this.PointToScreen(e.GetPosition(this));
        // A fast drag can outrun the ring's interactive region and drop the gesture, so pin the window
        // input-opaque until the pointer is released.
        _windowController?.SetForceInteractive(true);
        e.Pointer.Capture(Ring);
        e.Handled = true;
    }

    private void OnRingPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging)
            return;

        // Screen space, because this window moves out from under the cursor as we drag it: differencing
        // window-relative positions would report deltas of roughly zero. BeginMoveDrag is not usable
        // either - it hands the drag to the OS modal move loop, which yields no per-frame delta and so
        // no ring roll.
        var now = this.PointToScreen(e.GetPosition(this));
        var dxPx = now.X - _lastPointerScreen.X;
        var dyPx = now.Y - _lastPointerScreen.Y;
        if (dxPx == 0 && dyPx == 0)
            return;

        _lastPointerScreen = now;

        var scale = RenderScaling;
        var dxDip = dxPx / scale;
        _windowController?.MoveBy(dxDip, dyPx / scale);

        // Roll the ring naturally in the direction of horizontal travel.
        RingRotation += dxDip * RotationPerDip;
    }

    private void OnRingPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging)
            return;

        _dragging = false;
        _windowController?.SetForceInteractive(false);
        e.Pointer.Capture(null);

        // Let the roll carry past the release, then resume the idle spin from there.
        _ = SettleRingAsync();
        SchedulePersistOverlayPosition();
    }

    private async Task SettleRingAsync()
    {
        await Anim.RunAsync(RingRotation, Random.Shared.Next(0, 360), 350, Anim.SinOut, v => RingRotation = v);
        _ringBusy = false;
    }

    // --- Wheel: spin, or Ctrl+wheel to resize ---

    private void OnRingPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        // Avalonia reports deltas in notches, so MAUI's delta/120 normalisation is unnecessary. It
        // also routes the wheel to the element under the cursor, so the hover tracking the WinUI
        // version needed (PointerEntered/Exited plus a platform-view reference) is gone.
        var notches = e.Delta.Y;
        if (notches == 0)
            return;

        // Ctrl+scroll resizes the ring (persisted, debounced); plain scroll spins it.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            ApplyRingSize(_ringSize + (notches * RingSizeWheelStep));
            SchedulePersistRingSize();
            e.Handled = true;
            return;
        }

        if (_ringBusy)
            return;

        _manualWheelResumeAtUtc = DateTime.UtcNow.AddMilliseconds(ManualWheelResumeDelayMs);

        var rotation = (RingRotation + (notches * WheelRotationPerNotch)) % 360;
        RingRotation = rotation < 0 ? rotation + 360 : rotation;
        e.Handled = true;
    }

    // --- Summon (Alt+F): glide the window to the mouse cursor with a ring spin ---

    private void OnSummonRequested(int cursorX, int cursorY, nint foregroundHwnd)
    {
        if (!_settings.Current.AttachSelectionOnSummon)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _ = SpinRingAsync();
                AnimateSummon(cursorX, cursorY);
            });
            return;
        }

        // Started here, off the UI thread, while the app the user was in still owns keyboard focus:
        // AnimateSummon's Activate() takes the foreground away and would leave us reading Floaty.
        var capture = _selectionCapture.TryCaptureAsync(foregroundHwnd);

        Dispatcher.UIThread.Post(async () =>
        {
            // Spin first so the hotkey feels instant even when the capture has to wait on the app.
            _ = SpinRingAsync();
            _pendingSelection = await capture;
            AnimateSummon(cursorX, cursorY);
        });
    }

    private void AnimateSummon(int cursorX, int cursorY)
    {
        if (_windowController is null)
            return;

        _windowController.Activate();

        var (startX, startY) = _windowController.GetPosition();
        var (width, height) = _windowController.GetSize();

        // Centre the window (and thus the ring) on the cursor.
        double dx = (cursorX - (width / 2)) - startX;
        double dy = (cursorY - (height / 2)) - startY;

        _ = AnimateSummonAsync(startX, startY, dx, dy);
    }

    private async Task AnimateSummonAsync(int startX, int startY, double dx, double dy)
    {
        await Anim.RunAsync(SummonMoveMs, Anim.CubicInOut, t =>
            _windowController?.MoveTo(
                (int)Math.Round(startX + (dx * t)),
                (int)Math.Round(startY + (dy * t))));

        // Once it lands, reveal the chat input after a subtle beat.
        SchedulePersistOverlayPosition();
        await RevealChatAfterSummonAsync();
    }

    private async Task RevealChatAfterSummonAsync()
    {
        await Task.Delay(SummonRevealDelayMs);

        // Taken unconditionally: a selection that couldn't be delivered is stale by the next summon.
        var selection = _pendingSelection;
        _pendingSelection = null;

        // TODO(Phase 4): hand `selection` to the chat panel and open it.
        _ = selection;
    }

    private async Task SpinRingAsync()
    {
        _ringBusy = true; // take over from the idle spin for the summon flourish
        // CubicOut decelerates: the ring spins fast through the glide, then eases to a stop afterwards.
        // Whole turns plus a random offset, so it settles somewhere natural rather than snapping back.
        var from = RingRotation;
        await Anim.RunAsync(from, from + 720 + Random.Shared.Next(0, 360), SummonSpinMs, Anim.CubicOut,
            v => RingRotation = v % 360);
        _ringBusy = false;
    }

    // --- Capture shutter ---

    /// <summary>
    /// Acknowledges a capture: the ring runs its camera-shutter flourish and the configured capture
    /// sound plays.
    /// </summary>
    public void SignalCapture()
    {
        _sounds.Play(FloatySound.Capture);
        Dispatcher.UIThread.Post(() => _ = RunShutterAsync());
    }

    /// <summary>Runs (or stops) the ring's "waiting for the first model token" spin loader.</summary>
    public void SetBusy(bool busy)
    {
        // TODO(Phase 4): the spin-pause-spin loader loop, driven by the chat panel.
        _ = busy;
    }

    private async Task RunShutterAsync()
    {
        // Only cancel here; each run disposes its own token source in its finally.
        var restarted = _shutterCts is not null;
        _shutterCts?.Cancel();

        var cts = new CancellationTokenSource();
        _shutterCts = cts;
        var token = cts.Token;

        // The ring's resting angle: the twist is applied relative to it and unwound back to it.
        if (!restarted)
            _shutterRestRotation = RingRotation;
        var restRotation = _shutterRestRotation;

        _ringBusy = true; // take over from the idle spin for the duration
        ShutterFlash.Opacity = 0;
        ShutterFlash.IsVisible = true;

        try
        {
            await AnimateShutterAsync(ShutterWindUpScale, restRotation, 1, 0, ShutterWindUpMs, Anim.CubicOut, token);
            await AnimateShutterAsync(ShutterClosedScale, restRotation - ShutterTwistDegrees,
                ShutterClosedOpacity, ShutterFlashOpacity, ShutterCloseMs, Anim.CubicIn, token);
            await Task.Delay(ShutterHoldMs, token);
            await AnimateShutterAsync(ShutterOpenScale, restRotation, 1, 0, ShutterOpenMs, Anim.CubicOut, token);
            await AnimateShutterAsync(1.0, restRotation, 1, 0, ShutterSettleMs, Anim.CubicInOut, token);
        }
        catch (OperationCanceledException)
        {
            // A newer shutter took over; it owns the reset below via its own finally.
        }
        finally
        {
            // Never leave the ring shrunken, dimmed or twisted, whatever went wrong - the same
            // defensiveness as the drop-feedback watchdog.
            if (_shutterCts == cts)
            {
                SetRingScale(1);
                Ring.Opacity = 1;
                RingRotation = restRotation;
                ShutterFlash.Opacity = 0;
                ShutterFlash.IsVisible = false;
                _ringBusy = false;
                _shutterCts = null;
            }

            cts.Dispose();
        }
    }

    /// <summary>
    /// Drives one beat of the shutter. The ring and the flash disc have to scale in lockstep (the
    /// flash sits on top of the ring), so a single tween moves both.
    /// </summary>
    private Task AnimateShutterAsync(
        double scale, double rotation, double ringOpacity, double flashOpacity,
        int durationMs, Avalonia.Animation.Easings.Easing easing, CancellationToken cancellationToken)
    {
        var startScale = _ringScale.ScaleX;
        var startRotation = RingRotation;
        var startRingOpacity = Ring.Opacity;
        var startFlashOpacity = ShutterFlash.Opacity;

        return Anim.RunAsync(durationMs, easing, t =>
        {
            SetRingScale(startScale + ((scale - startScale) * t));
            RingRotation = startRotation + ((rotation - startRotation) * t);
            Ring.Opacity = startRingOpacity + ((ringOpacity - startRingOpacity) * t);
            ShutterFlash.Opacity = startFlashOpacity + ((flashOpacity - startFlashOpacity) * t);
        }, cancellationToken);
    }

    private void SetRingScale(double scale)
    {
        _ringScale.ScaleX = _ringScale.ScaleY = scale;
        _flashScale.ScaleX = _flashScale.ScaleY = scale;
    }

    // --- File drops on the ring ---

    private void OnRingDragEnter(object? sender, DragEventArgs e) => HandleRingDragOver(e);

    private void OnRingDragOver(object? sender, DragEventArgs e) => HandleRingDragOver(e);

    private void HandleRingDragOver(DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(DataFormat.File))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        e.DragEffects = DragDropEffects.Copy;
        // An OLE drop target must not be click-through, and the hit-test poll would otherwise drop the
        // drag the moment the pointer strays a few pixels off the ring.
        _windowController?.KeepInteractiveFor(DragInteractiveGrace);
        BeginRingDropFeedback();
        e.Handled = true;
    }

    private void OnRingDragLeave(object? sender, DragEventArgs e) => EndRingDropFeedback();

    private void OnRingDrop(object? sender, DragEventArgs e)
    {
        EndRingDropFeedback();

        if (!e.DataTransfer.Contains(DataFormat.File))
            return;

        var paths = e.DataTransfer.TryGetFiles()?
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => p!)
            .ToList() ?? [];

        // Alt-drop memorises the files instead of attaching them to the pending prompt.
        var memorize = e.KeyModifiers.HasFlag(KeyModifiers.Alt);

        // Unlike WinUI there is no deferral to take: Avalonia's IDataTransfer is already materialised,
        // so folders can be filtered inline rather than through an async StorageFolder probe.
        var files = paths.Where(p => !Directory.Exists(p)).ToList();
        var hadFolders = files.Count != paths.Count;

        e.Handled = true;

        // TODO(Phase 4): route into the chat panel (attach, or memorise on Alt-drop).
        _ = (files, hadFolders, memorize);
    }

    private void BeginRingDropFeedback()
    {
        _ringDropWatchdog ??= Anim.OneShot(RingDropFeedbackTimeoutMs, EndRingDropFeedback);
        _ringDropWatchdog.Stop();
        _ringDropWatchdog.Start();

        if (_ringDropActive)
            return;

        _ringDropActive = true;
        _ringBusy = true;
        _ = Anim.RunAsync(_ringScale.ScaleX, RingDropScale, 140, Anim.CubicOut, SetRingScale);
    }

    private void EndRingDropFeedback()
    {
        _ringDropWatchdog?.Stop();

        if (!_ringDropActive)
            return;

        _ringDropActive = false;
        _ = Anim.RunAsync(_ringScale.ScaleX, 1.0, 160, Anim.CubicIn, SetRingScale);
        _ringBusy = false;
    }

    // --- Context menu ---

    private void OnDockedWindowClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // TODO(Phase 4): toggle between the floating and fixed chat placements.
    }

    private void OnAlwaysOnTopClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var config = _settings.Current;
        config.AlwaysOnTop = !config.AlwaysOnTop;
        _settings.Save(config);

        ApplyAlwaysOnTop(config.AlwaysOnTop);
        ApplyAlwaysOnTopMenuState();
    }

    private void OnSettingsClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // TODO(Phase 5): open the native settings window.
    }

    private void OnFloatToTaskbarClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        _windowController?.FloatToTaskbarAndHide();

    private void OnCloseClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();

    // --- Position and size persistence ---

    /// <summary>
    /// Starts restoring the persisted native window position. This is retried rather than done once:
    /// straight after <c>Show()</c> the window has no composited frame, so the work area and client
    /// size the clamp depends on both still report zero.
    /// </summary>
    private void BeginOverlayPositionRestore()
    {
        if (_overlayPositionRestored)
            return;

        _overlayPositionRestoreAttempts = 0;
        _overlayPositionRestoreTimer ??= CreateOverlayPositionRestoreTimer();
        _overlayPositionRestoreTimer.Stop();
        _overlayPositionRestoreTimer.Start();
    }

    private DispatcherTimer CreateOverlayPositionRestoreTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(OverlayRestoreRetryMs) };
        timer.Tick += (_, _) =>
        {
            if (_overlayPositionRestored)
            {
                timer.Stop();
                return;
            }

            _overlayPositionRestoreAttempts++;
            if (TryRestoreOverlayPosition() || _overlayPositionRestoreAttempts >= OverlayRestoreMaxAttempts)
            {
                _overlayPositionRestored = true;
                timer.Stop();
            }
        };
        return timer;
    }

    /// <summary>
    /// Clamps the saved position into the current work area - a screen that has since been unplugged
    /// must not strand the ring off-display - and applies it. Returns false while the window cannot
    /// yet report a work area or size, so the caller retries.
    /// </summary>
    private bool TryRestoreOverlayPosition()
    {
        if (_windowController is null)
            return false;

        var savedX = _settings.Current.OverlayWindowX;
        var savedY = _settings.Current.OverlayWindowY;
        if (!savedX.HasValue || !savedY.HasValue)
            return true;

        var (workX, workY, workWidth, workHeight) = _windowController.GetWorkArea();
        var (width, height) = _windowController.GetSize();
        if (workWidth <= 0 || workHeight <= 0 || width <= 0 || height <= 0)
            return false;

        var x = Math.Clamp(savedX.Value, workX, workX + Math.Max(0, workWidth - width));
        var y = Math.Clamp(savedY.Value, workY, workY + Math.Max(0, workHeight - height));
        _windowController.MoveTo(x, y);

        // The clamp moved it, so the saved value is stale for this display layout.
        if (x != savedX.Value || y != savedY.Value)
            SchedulePersistOverlayPosition();

        return true;
    }

    // Debounced: a drag produces a position change per frame, and each save rewrites config.json.
    private void SchedulePersistOverlayPosition()
    {
        _overlayPositionPersistTimer ??= Anim.OneShot(600, () =>
        {
            if (_windowController is null)
                return;

            var (_, _, workWidth, workHeight) = _windowController.GetWorkArea();
            if (workWidth <= 0 || workHeight <= 0)
                return;

            var (x, y) = _windowController.GetPosition();
            var config = _settings.Current;
            if (config.OverlayWindowX == x && config.OverlayWindowY == y)
                return;

            config.OverlayWindowX = x;
            config.OverlayWindowY = y;
            _settings.Save(config);
        });

        _overlayPositionPersistTimer.Stop();
        _overlayPositionPersistTimer.Start();
    }

    // Debounced the same way: Ctrl+scroll fires per notch, and each save rewrites config.json.
    private void SchedulePersistRingSize()
    {
        _ringSizePersistTimer ??= Anim.OneShot(600, () =>
        {
            var config = _settings.Current;
            if (Math.Abs(config.RingSize - _ringSize) < 0.01)
                return;

            config.RingSize = _ringSize;
            _settings.Save(config);
        });

        _ringSizePersistTimer.Stop();
        _ringSizePersistTimer.Start();
    }
}
