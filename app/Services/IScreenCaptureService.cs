namespace Floaty.Services;

/// <summary>
/// Paths to a saved screenshot + accessibility-content pair, the captured window's title, and the
/// flattened accessibility text (<see cref="Content"/>) so callers can embed it without re-reading disk.
/// </summary>
/// <remarks>
/// Also used for dropped files that the user chose to persist (see <c>IMemoryService.DroppedFileSource</c>),
/// where <see cref="WindowTitle"/> carries the file name and <see cref="ImagePath"/> the persisted copy —
/// so drops inherit the vision description, the text-file annotation, vector search and citations.
/// </remarks>
public sealed record CaptureResult(
    string ImagePath,
    string TextPath,
    string WindowTitle,
    string Content,
    string? AppName = null,
    string? Url = null);

/// <summary>A top-level application window that can be captured as prompt context.</summary>
public sealed record WindowInfo(nint Hwnd, string Title, string ProcessName);

/// <summary>
/// A window's accessibility text and the metadata that identifies what it was showing, read
/// without touching disk. Screen history's text-only pipeline prunes, redacts and deduplicates
/// <see cref="Lines"/> before deciding whether the capture is worth writing at all, so extraction
/// has to be separable from storage (see <c>CaptureDayLog</c>).
/// </summary>
public sealed record CaptureSnapshot(
    string AppName,
    string WindowTitle,
    string? Url,
    string? Document,
    IReadOnlyList<string> Lines);

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

    /// <summary>
    /// Reads a specific top-level window's accessibility text and metadata without writing anything.
    /// Returns <c>null</c> when the window is no longer a valid capture target, when the platform
    /// doesn't support capture, or when the window is one that must never be read at all — a
    /// password manager or a private-browsing window (see <see cref="CaptureRedactor"/>).
    /// </summary>
    Task<CaptureSnapshot?> ReadWindowAsync(nint hwnd, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the top-level application windows that are valid capture targets, in Z-order front to
    /// back (so the most recently used windows come first). Empty when the platform doesn't
    /// support capture.
    /// </summary>
    Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(CancellationToken cancellationToken = default);
}
