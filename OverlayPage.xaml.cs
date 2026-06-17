using System.Diagnostics;
using Floaty.Services;

namespace Floaty;

public partial class OverlayPage : ContentPage
{
    private readonly IOverlayWindowController _windowController;

    // How many degrees the ring "rolls" per device-independent unit dragged horizontally.
    private const double RotationPerDip = 0.6;

    // Cumulative pan offset reported on the previous PanUpdated event, used to derive per-frame deltas.
    private double _lastTotalX;
    private double _lastTotalY;

    public OverlayPage(IOverlayWindowController windowController)
    {
        InitializeComponent();
        _windowController = windowController;
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

    // --- Placeholder actions: wired now, implemented in later milestones (see readme.md). ---

    private void OnScreenshotClicked(object? sender, EventArgs e) =>
        Debug.WriteLine("[Floaty] TODO: capture screenshot");

    private void OnReadScreenClicked(object? sender, EventArgs e) =>
        Debug.WriteLine("[Floaty] TODO: read screen content via accessibility APIs");

    private void OnOpenChatClicked(object? sender, EventArgs e) =>
        Debug.WriteLine("[Floaty] TODO: open chat window (Blazor)");

    private void OnSettingsClicked(object? sender, EventArgs e) =>
        Debug.WriteLine("[Floaty] TODO: open settings");
}
