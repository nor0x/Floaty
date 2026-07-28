namespace Floaty.Services;

/// <summary>
/// Text the user had selected in another app when they summoned Floaty, plus the title of the window
/// it came from (used to label the attachment chip's context in the outgoing message).
/// </summary>
public sealed record SelectedText(string Text, string SourceTitle);

/// <summary>
/// Reads whatever text is selected in another application. The one seam around the platform's
/// accessibility / input APIs, kept behind an interface so the summon path stays cross-platform.
/// </summary>
public interface ISelectionCaptureService
{
    /// <summary>
    /// Best-effort read of the selection in <paramref name="foregroundHwnd"/>. Returns <c>null</c> when
    /// nothing is selected, the app exposes no usable selection, or the attempt ran out of time —
    /// callers treat all three the same and simply summon without an attachment.
    /// </summary>
    /// <remarks>
    /// Must be called <em>before</em> the overlay takes foreground: every implementation depends on the
    /// source app still owning keyboard focus, and Floaty's own window would otherwise be read instead.
    /// Implementations are expected to return within a few hundred milliseconds — the summon animation
    /// waits on this — and to never throw.
    /// </remarks>
    Task<SelectedText?> TryCaptureAsync(nint foregroundHwnd, CancellationToken cancellationToken = default);
}
