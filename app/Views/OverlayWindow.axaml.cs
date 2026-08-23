using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Floaty.Services;

namespace Floaty.Views;

/// <summary>
/// The floating swim-ring overlay. Unlike the MAUI version this is the top-level <see cref="Window"/>
/// itself rather than a page hosted in one, so there is no window/page pair to keep in sync.
/// </summary>
/// <remarks>
/// Phase 1 scope: the window chrome, the ring image, ring sizing, and drag-to-move. The context menu,
/// summon animation, shutter, wheel resize, drag-and-drop and the chat panel land in Phase 3.
/// </remarks>
public partial class OverlayWindow : Window
{
    // Compact (chat closed) overlay window size, in device-independent units; grows with the ring.
    // The window hugs the ring so it sits flush against both window edges, letting the chat panel
    // open to either side with the ring staying visually put.
    private const double CompactWidthPadding = 2;   // 150 - 148
    private const double CompactHeightExtra = 102;  // 250 - 148

    // The window reports no work area or size until it has been composited, so the position
    // restore polls for a usable answer instead of assuming one is available at startup.
    private const int OverlayRestoreRetryMs = 100;
    private const int OverlayRestoreMaxAttempts = 20;

    private readonly SettingsService _settings;

    private IOverlayWindowController? _windowController;

    private double _ringSize = SettingsService.RingDefaultSize;

    // Drag state. The pointer position Avalonia reports is relative to this window, which is itself
    // moving under the cursor, so deltas are taken in screen space instead.
    private bool _dragging;
    private PixelPoint _lastPointerScreen;
    private DispatcherTimer? _overlayPositionPersistTimer;
    private DispatcherTimer? _overlayPositionRestoreTimer;
    private int _overlayPositionRestoreAttempts;
    private bool _overlayPositionRestored;

    public OverlayWindow(SettingsService settings)
    {
        InitializeComponent();
        _settings = settings;

        ApplyRingImage();
        ApplyRingSize(_settings.Current.RingSize);
        Topmost = _settings.Current.AlwaysOnTop;

        Ring.PointerPressed += OnRingPointerPressed;
        Ring.PointerMoved += OnRingPointerMoved;
        Ring.PointerReleased += OnRingPointerReleased;
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

        // Everything else in the tree (chat panel, chips, popups) is found by hit testing. Note this
        // returns null until the first frame has been composited, which correctly reads as
        // "not interactive" during startup.
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
        if (Ring.TranslatePoint(default, this) is not { } ringOrigin)
            return false;

        var radius = _ringSize / 2;
        var dx = x - (ringOrigin.X + (Ring.Bounds.Width / 2));
        var dy = y - (ringOrigin.Y + (Ring.Bounds.Height / 2));
        return (dx * dx) + (dy * dy) <= radius * radius;
    }

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

        // Summon (Alt+F). Phase 3 adds the glide animation and ring spin; for now the overlay simply
        // shows itself near the cursor.
        controller.SummonRequested += OnSummonRequested;

        BeginOverlayPositionRestore();
    }

    private void OnSummonRequested(int cursorX, int cursorY, nint foregroundHwnd) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_windowController is null)
                return;

            var (workX, workY, workWidth, workHeight) = _windowController.GetWorkArea();
            var (width, height) = _windowController.GetSize();

            // Park the ring just below-right of the cursor, clamped into the work area.
            var x = Math.Clamp(cursorX - (width / 2), workX, workX + workWidth - width);
            var y = Math.Clamp(cursorY - (height / 2), workY, workY + workHeight - height);

            _windowController.MoveTo(x, y);
            _windowController.Activate();
        });

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

    // --- Drag to move ---

    private void OnRingPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        _dragging = true;
        _lastPointerScreen = this.PointToScreen(e.GetPosition(this));
        // A fast drag can outrun the interactive region and drop the gesture, so pin the window
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
        // no ring rotation (Phase 3).
        var now = this.PointToScreen(e.GetPosition(this));
        var dx = now.X - _lastPointerScreen.X;
        var dy = now.Y - _lastPointerScreen.Y;
        if (dx == 0 && dy == 0)
            return;

        _lastPointerScreen = now;
        Position = new PixelPoint(Position.X + dx, Position.Y + dy);
        SchedulePersistOverlayPosition();
    }

    private void OnRingPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging)
            return;

        _dragging = false;
        _windowController?.SetForceInteractive(false);
        e.Pointer.Capture(null);
    }

    // --- Position persistence ---

    // Debounced: a drag produces a position change per frame, and each save rewrites config.json.
    private void SchedulePersistOverlayPosition()
    {
        _overlayPositionPersistTimer ??= CreateOverlayPositionPersistTimer();
        _overlayPositionPersistTimer.Stop();
        _overlayPositionPersistTimer.Start();
    }

    private DispatcherTimer CreateOverlayPositionPersistTimer()
    {
        // Avalonia's DispatcherTimer repeats, so the tick stops it to get one-shot semantics.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_windowController is null)
                return;

            var (x, y) = _windowController.GetPosition();
            var config = _settings.Current;
            if (config.OverlayWindowX == x && config.OverlayWindowY == y)
                return;

            config.OverlayWindowX = x;
            config.OverlayWindowY = y;
            _settings.Save(config);
        };
        return timer;
    }
}
