namespace Floaty.Services;

/// <summary>What a toast is about; drives its glyph and tint.</summary>
public enum ToastKind
{
    Reply,   // an assistant reply that arrived while the chat was closed
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>Where a toast ended up, so the <c>show_toast</c> tool can tell the model.</summary>
public enum ToastOutcome
{
    Shown,         // slid out of the ring
    ShownInChat,   // the chat was open, so it went to the panel's inline status strip instead
    OverlayHidden, // the ring is hidden (floated to the taskbar); nothing was shown
    Unavailable,   // no presenter yet - the overlay hasn't been built
}

/// <summary>
/// One toast. Every part except <see cref="Body"/> is optional and <c>ToastView</c> builds itself from
/// whichever are set, so a new part (an action row, a confirm button) is a new property here plus one
/// block in the view, not a redesign.
/// </summary>
public sealed record FloatyToast(string Body)
{
    public string? Title { get; init; }

    public ToastKind Kind { get; init; } = ToastKind.Info;

    /// <summary>
    /// A local image to show under the text. Callers must have validated it already - the reply path
    /// and the tool only ever pass files resolved through <see cref="GeneratedImageUri.ResolvePath"/>.
    /// </summary>
    public string? ImagePath { get; init; }

    /// <summary>
    /// What "open in chat" should land on, resolved by the chat panel: the reply's message for reply
    /// toasts, null (the newest message) for the model's own. Opaque here so Services stays free of
    /// view-model types.
    /// </summary>
    public object? Target { get; init; }
}

/// <summary>Turns a markdown reply into the few plain-text lines a toast has room for.</summary>
public static class ToastText
{
    // Long enough for a few lines of preview; the toast clamps to three lines on top of this.
    public const int PreviewMaxChars = 180;

    /// <summary>
    /// A plain-text preview of <paramref name="markdown"/>: the read-aloud stripper drops markdown, code
    /// and image syntax, whitespace collapses, and anything past <see cref="PreviewMaxChars"/> is cut.
    /// Empty when nothing speakable is left.
    /// </summary>
    public static string Preview(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return string.Empty;

        var preview = string.Join(' ', SpeechTextChunker.ToSpeakable(markdown)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return preview.Length > PreviewMaxChars
            ? preview[..PreviewMaxChars].TrimEnd() + "…"
            : preview;
    }
}

/// <summary>
/// Routes toasts to whatever can show them. The overlay window registers itself as the presenter at
/// construction, because the ring - which toasts slide out of - lives there under both chat
/// placements. Callers (the chat panel's reply path, the <c>show_toast</c> tool) only ever see this.
/// </summary>
/// <remarks>
/// Not the usual interface/Windows/Null triple: nothing here is platform-specific, the toast is drawn
/// by Avalonia like the rest of the overlay. The presenter marshals to the UI thread itself, so this can
/// be called from a tool running on a worker thread.
/// </remarks>
public sealed class ToastService
{
    private Func<FloatyToast, Task<ToastOutcome>>? _presenter;

    /// <summary>True once something can show a toast; gates the <c>show_toast</c> tool.</summary>
    public bool IsAvailable => _presenter is not null;

    public void SetPresenter(Func<FloatyToast, Task<ToastOutcome>>? presenter) => _presenter = presenter;

    public Task<ToastOutcome> ShowAsync(FloatyToast toast) =>
        _presenter?.Invoke(toast) ?? Task.FromResult(ToastOutcome.Unavailable);
}
