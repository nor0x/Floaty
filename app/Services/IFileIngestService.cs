namespace Floaty.Services;

/// <summary>
/// A file the user dropped on Floaty, resolved into prompt-ready content. Held entirely in memory:
/// a one-shot drop never touches disk, and only the persist path copies it into
/// <see cref="FloatyPaths.Drops"/>.
/// </summary>
/// <param name="SourcePath">Where the file was dragged from. Also the de-duplication key for chips.</param>
/// <param name="Text">Extracted text, already capped. Empty when nothing could be extracted.</param>
/// <param name="ImageBytes">Raw bytes for image drops, sent as a <c>DataContent</c>; null otherwise.</param>
/// <param name="TextExtracted">
/// False when every extraction route failed, so the send path can tell the model the file exists but
/// couldn't be read rather than silently attaching an empty body.
/// </param>
public sealed record DroppedFile(
    string SourcePath,
    string FileName,
    string MimeType,
    long SizeBytes,
    string Text,
    byte[]? ImageBytes,
    bool TextExtracted);

/// <summary>
/// Turns a dropped file path into <see cref="DroppedFile"/> prompt context: enforces the size/type
/// caps, then walks a fallback ladder (images → <see cref="ITextExtractionService"/> → UTF-8 decode →
/// filename only) so a drop is never rejected just because its format is exotic.
/// </summary>
public interface IFileIngestService
{
    /// <summary>Most files accepted from a single drop; the rest are ignored with a toast.</summary>
    const int MaxFilesPerDrop = 10;

    /// <summary>
    /// Reads and text-extracts a dropped file. Returns <c>null</c> only when the file can't be used at
    /// all — missing, unreadable, or over the size cap — in which case the caller drops the chip.
    /// </summary>
    Task<DroppedFile?> IngestAsync(string path, CancellationToken cancellationToken = default);
}
