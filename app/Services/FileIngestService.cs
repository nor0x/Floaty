using System.Text;

namespace Floaty.Services;

/// <summary>
/// Cross-platform ingest policy for dropped files. The one platform-specific piece — pulling text out
/// of rich documents — sits behind <see cref="ITextExtractionService"/>, so the caps and the fallback
/// ladder below behave identically everywhere.
/// </summary>
public sealed class FileIngestService : IFileIngestService
{
    // Hard reject above this: a drop is prompt context, and a multi-hundred-MB file is a mistake, not
    // a question. Images are capped lower because their bytes go to the model verbatim.
    private const long MaxFileBytes = 32L * 1024 * 1024;
    private const long MaxImageBytes = 10L * 1024 * 1024;

    // Ceiling on extracted text kept in memory. The send path trims much harder (MaxAttachmentChars),
    // but the persist path writes the full body to disk, so a 500-page PDF still stays bounded.
    private const int MaxIngestChars = 200_000;

    // Bytes sniffed to decide whether an unknown extension is really text.
    private const int SniffBytes = 4096;

    // Extensions that are text by definition, so a failed extractor doesn't cost them their content.
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".json", ".xml", ".csv", ".tsv", ".log", ".yml", ".yaml",
        ".ini", ".toml", ".cfg", ".conf", ".env", ".gitignore", ".editorconfig",
        ".cs", ".fs", ".vb", ".js", ".mjs", ".ts", ".tsx", ".jsx", ".py", ".rb", ".go", ".rs",
        ".java", ".kt", ".swift", ".c", ".h", ".cpp", ".hpp", ".php", ".lua", ".r", ".pl",
        ".html", ".htm", ".css", ".scss", ".sql", ".sh", ".bash", ".zsh", ".ps1", ".psm1",
        ".bat", ".cmd", ".razor", ".xaml", ".csproj", ".props", ".targets", ".sln", ".slnx",
    };

    private readonly ITextExtractionService _extractor;

    public FileIngestService(ITextExtractionService extractor) => _extractor = extractor;

    public async Task<DroppedFile?> IngestAsync(string path, CancellationToken cancellationToken = default)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFileBytes)
                return null;
        }
        catch
        {
            // An unreadable path (permissions, a UNC share that just went away) is the same as missing.
            return null;
        }

        var fileName = Path.GetFileName(path);
        var mime = MimeTypes.FromPath(path);

        // 1. Images ride along as bytes for the vision model. Extraction is skipped deliberately: the
        //    multimodal path already carries far more than OCR would, and it keeps drops instant.
        if (MimeTypes.IsImage(path))
        {
            if (info.Length > MaxImageBytes)
                return null;

            try
            {
                var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
                return new DroppedFile(path, fileName, mime, info.Length, string.Empty, bytes, TextExtracted: false);
            }
            catch
            {
                return null;
            }
        }

        // 2. Rich documents: PDF, Office, e-mail, archives, …
        var extracted = await _extractor.ExtractAsync(path, cancellationToken);
        if (extracted is not null && !string.IsNullOrWhiteSpace(extracted.Text))
        {
            var resolvedMime = string.IsNullOrWhiteSpace(extracted.MimeType) ? mime : extracted.MimeType!;
            return new DroppedFile(
                path, fileName, resolvedMime, info.Length, Cap(extracted.Text), ImageBytes: null, TextExtracted: true);
        }

        // 3. Plain text, either by extension or because the file sniffs as text. This is what keeps a
        //    dropped .md or .cs useful when the extractor is unavailable or doesn't know the format.
        if (TextExtensions.Contains(Path.GetExtension(path)) || await SniffsAsTextAsync(path, cancellationToken))
        {
            try
            {
                using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var text = await reader.ReadToEndAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(text))
                    return new DroppedFile(path, fileName, mime, info.Length, Cap(text), ImageBytes: null, TextExtracted: true);
            }
            catch
            {
                // fall through to the filename-only attachment
            }
        }

        // 4. Nothing readable. The chip still appears — the model is told the file exists and why it's
        //    empty, which is far more useful than silently dropping it.
        return new DroppedFile(path, fileName, mime, info.Length, string.Empty, ImageBytes: null, TextExtracted: false);
    }

    private static string Cap(string text) =>
        text.Length <= MaxIngestChars ? text : text[..MaxIngestChars];

    // A NUL byte in the first few KB is the classic "this is binary" tell; anything else that decodes
    // as UTF-8 without replacement characters is worth attaching as text.
    private static async Task<bool> SniffsAsTextAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            var buffer = new byte[SniffBytes];
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
                return false;

            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == 0)
                    return false;
            }

            // The sniff window can slice a multi-byte character in half, which strict UTF-8 rejects.
            // Back off over any trailing non-ASCII bytes so a legitimate UTF-8 file isn't misjudged.
            if (read == buffer.Length)
            {
                var trimmed = 0;
                while (read > 0 && trimmed < 3 && buffer[read - 1] >= 0x80)
                {
                    read--;
                    trimmed++;
                }
            }

            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            strict.GetString(buffer, 0, read);
            return true;
        }
        catch
        {
            // Invalid UTF-8 throws out of GetString; treat anything unreadable as "not text".
            return false;
        }
    }
}
