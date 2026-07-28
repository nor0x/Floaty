namespace Floaty.Services;

/// <summary>
/// Pulls plain text out of a document of any format (PDF, Office, e-mail, archives…). This is the one
/// seam around the native document-intelligence library, kept separate from <see cref="IFileIngestService"/>
/// so the size caps, classification and fallbacks stay cross-platform while only the extractor is
/// platform-conditional.
/// </summary>
public interface ITextExtractionService
{
    /// <summary>
    /// Extracts text from <paramref name="path"/>. Returns <c>null</c> when extraction is unavailable
    /// (platform without a native runtime), timed out, or failed for any reason — callers must fall
    /// back rather than surface an error, because a file that can't be read is still worth attaching
    /// by name.
    /// </summary>
    Task<ExtractedText?> ExtractAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>
/// Text pulled from a document, plus the MIME type the extractor sniffed (may be null). Named
/// <c>ExtractedText</c> rather than the obvious <c>TextExtractionResult</c> because Xberg's own SDK
/// exports a type by that name, and the Windows implementation has both in scope.
/// </summary>
public sealed record ExtractedText(string Text, string? MimeType);
