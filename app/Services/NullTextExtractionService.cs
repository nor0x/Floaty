namespace Floaty.Services;

/// <summary>
/// Fallback for platforms without the native document-intelligence runtime. Returning <c>null</c> puts
/// <see cref="FileIngestService"/> onto its UTF-8 / filename-only ladder, so dropping files still works
/// — plain-text and image drops behave identically, only rich documents lose their text.
/// </summary>
public sealed class NullTextExtractionService : ITextExtractionService
{
    public Task<ExtractedText?> ExtractAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromResult<ExtractedText?>(null);
}
