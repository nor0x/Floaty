using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
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

    private readonly SettingsService _settings;

    private double _ringSize = SettingsService.RingDefaultSize;

    // Drag state. The pointer position Avalonia reports is relative to this window, which is itself
    // moving under the cursor, so deltas are taken in screen space instead.
    private bool _dragging;
    private PixelPoint _lastPointerScreen;

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

        if (Ring.IsVisible && Ring.TranslatePoint(default, this) is { } ringOrigin)
        {
            var radius = _ringSize / 2;
            var cx = ringOrigin.X + (Ring.Bounds.Width / 2);
            var cy = ringOrigin.Y + (Ring.Bounds.Height / 2);
            var dx = x - cx;
            var dy = y - cy;
            if ((dx * dx) + (dy * dy) <= radius * radius)
                return true;
        }

        // Everything else in the tree (chat panel, chips, popups) is found by hit testing. Note this
        // returns null until the first frame has been composited, which correctly reads as
        // "not interactive" during startup.
        return this.InputHitTest(point) is Visual hit
            && !ReferenceEquals(hit, ContentRoot)
            && hit.GetSelfAndVisualAncestors().Any(a => ReferenceEquals(a, ContentRoot));
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

    // --- Drag to move ---

    private void OnRingPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        _dragging = true;
        _lastPointerScreen = this.PointToScreen(e.GetPosition(this));
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
    }

    private void OnRingPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging)
            return;

        _dragging = false;
        e.Pointer.Capture(null);
    }
}
