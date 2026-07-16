namespace Floaty.Services;

/// <summary>
/// Paths to a saved screenshot + accessibility-content pair, the captured window's title, and the
/// flattened accessibility text (<see cref="Content"/>) so callers can embed it without re-reading disk.
/// </summary>
public sealed record CaptureResult(string ImagePath, string TextPath, string WindowTitle, string Content);

/// <summary>
/// Captures the window directly beneath the floating overlay: a screenshot (PNG) plus its
/// accessibility content (TXT), written to <c>~/.floaty/captures</c>.
/// </summary>
public interface IScreenCaptureService
{
    /// <summary>
    /// Captures the top-most application window under the overlay. Returns <c>null</c> when there's
    /// no suitable window (e.g. only the desktop is visible) or the platform doesn't support capture.
    /// </summary>
    Task<CaptureResult?> CaptureUnderlyingWindowAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Captures a specific top-level window (used by automatic screen history). When
    /// <paramref name="includeScreenshot"/> is false only the accessibility text is written and
    /// <see cref="CaptureResult.ImagePath"/> is empty. Returns <c>null</c> when the window is no
    /// longer a valid capture target or the platform doesn't support capture.
    /// </summary>
    Task<CaptureResult?> CaptureWindowAsync(
        nint hwnd,
        bool includeScreenshot,
        CancellationToken cancellationToken = default);
}
