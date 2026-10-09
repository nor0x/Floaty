using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Floaty.IconFont;
using Floaty.Services;
using Floaty.Ui;

namespace Floaty.Views;

/// <summary>
/// The card a <see cref="FloatyToast"/> renders as. It knows nothing about the ring or the window: the
/// overlay places it, slides it and decides when it goes; this only draws the toast, mirrors itself for
/// either side of the ring, runs the countdown and reports the two buttons.
/// </summary>
/// <remarks>
/// Content is built from whichever parts the toast carries (<see cref="Present"/>), so a new part - an
/// action row, a confirm button - is one more block there rather than another toast layout.
/// </remarks>
public partial class ToastView : UserControl
{
    /// <summary>The card's fixed width, in device-independent units.</summary>
    public const double ToastWidth = 300;

    private Bitmap? _image;

    public ToastView()
    {
        InitializeComponent();
        Width = ToastWidth;

        CloseButton.Click += (_, e) =>
        {
            e.Handled = true;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        };
        OpenButton.Click += (_, e) =>
        {
            e.Handled = true;
            OpenRequested?.Invoke(this, EventArgs.Empty);
        };

        // The whole card opens the chat, not just the arrow - the arrow is there to say so. Buttons
        // inside handle their own clicks, so a tap that started on one is ignored here.
        Card.Tapped += (_, e) =>
        {
            if (e.Source is Visual source && source.GetSelfAndVisualAncestors().OfType<Button>().Any())
                return;
            OpenRequested?.Invoke(this, EventArgs.Empty);
        };

        Card.PointerEntered += (_, _) => IsHovered = true;
        Card.PointerExited += (_, _) => IsHovered = false;

        ApplySide(onLeft: false);
    }

    /// <summary>✕ was clicked.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>The card or its ↗ button was clicked: the user wants the message in the chat.</summary>
    public event EventHandler? OpenRequested;

    /// <summary>True while the pointer is over the card; the countdown holds still meanwhile.</summary>
    public bool IsHovered { get; private set; }

    /// <summary>Fills the card from <paramref name="toast"/>. Parts the toast doesn't carry collapse.</summary>
    public void Present(FloatyToast toast)
    {
        Card.Classes.Set("reply", toast.Kind == ToastKind.Reply);
        Card.Classes.Set("info", toast.Kind == ToastKind.Info);
        Card.Classes.Set("success", toast.Kind == ToastKind.Success);
        Card.Classes.Set("warning", toast.Kind == ToastKind.Warning);
        Card.Classes.Set("error", toast.Kind == ToastKind.Error);

        KindGlyph.Text = toast.Kind switch
        {
            ToastKind.Reply => TablerLine.MessageCircle,
            ToastKind.Success => TablerLine.CircleCheck,
            ToastKind.Warning => TablerLine.AlertTriangle,
            ToastKind.Error => TablerLine.AlertTriangle,
            _ => TablerLine.InfoCircle,
        };

        TitleLabel.Text = toast.Title ?? string.Empty;
        TitleLabel.IsVisible = !string.IsNullOrWhiteSpace(toast.Title);

        BodyLabel.Text = toast.Body;
        BodyLabel.IsVisible = !string.IsNullOrWhiteSpace(toast.Body);

        // Fewer lines of text when a picture rides along: the picture is the point then.
        BodyLabel.MaxLines = string.IsNullOrEmpty(toast.ImagePath) ? 3 : 2;

        PresentImage(toast.ImagePath);
        SetProgress(1);
    }

    private void PresentImage(string? path)
    {
        var previous = _image;
        _image = null;

        if (!string.IsNullOrEmpty(path))
        {
            try
            {
                _image = new Bitmap(path);
            }
            catch
            {
                // A picture that won't decode is not worth losing the toast over; it shows text only.
            }
        }

        ToastImage.Source = _image;
        ImageHost.IsVisible = _image is not null;
        previous?.Dispose();
    }

    /// <summary>
    /// Mirrors the card for the side of the ring it sits on: the glyph faces the ring the toast came out
    /// of, the buttons take the outer edge, and the countdown drains toward the ring.
    /// </summary>
    public void ApplySide(bool onLeft)
    {
        Grid.SetColumn(GlyphBadge, onLeft ? 2 : 0);
        Grid.SetColumn(Actions, onLeft ? 0 : 2);
        BodyGrid.Margin = onLeft ? new Thickness(6, 10, 10, 10) : new Thickness(10, 10, 6, 10);
        ContentStack.Margin = onLeft ? new Thickness(6, 0, 10, 0) : new Thickness(10, 0, 6, 0);

        ProgressBar.HorizontalAlignment = onLeft ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    }

    /// <summary>The card's height at <see cref="ToastWidth"/>, measured now (it may not be laid out yet).</summary>
    public double MeasureCardHeight()
    {
        Card.Measure(new Size(ToastWidth, double.PositiveInfinity));
        return Card.DesiredSize.Height;
    }

    /// <summary>
    /// Runs the countdown and completes when it reaches zero. Time spent hovered doesn't count, so a toast
    /// being read never slides away under the pointer. Throws <see cref="OperationCanceledException"/>
    /// when cancelled - the overlay cancels it whenever the toast is replaced or dismissed.
    /// </summary>
    public async Task RunCountdownAsync(TimeSpan duration, CancellationToken cancellationToken)
    {
        var total = Math.Max(1, duration.TotalMilliseconds);
        var remaining = total;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        SetProgress(1);
        while (remaining > 0)
        {
            await Task.Delay(Anim.FrameMs, cancellationToken);

            var elapsed = clock.Elapsed.TotalMilliseconds;
            clock.Restart();
            if (!IsHovered)
                remaining -= elapsed;

            SetProgress(remaining / total);
        }
    }

    private void SetProgress(double fraction) =>
        ProgressBar.Width = Math.Max(0, ToastWidth * Math.Clamp(fraction, 0, 1));

    /// <summary>Drops the picture once the toast is gone, so a big image isn't held while hidden.</summary>
    public void Clear()
    {
        PresentImage(null);
        IsHovered = false;
    }
}
