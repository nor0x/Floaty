namespace Floaty.Services;

/// <summary>
/// Extension → MIME mapping for the handful of places that hand bytes to the model or store a file's
/// type alongside it. Deliberately small: it only needs to be right for what Floaty actually attaches
/// (screenshots, dropped images and documents), not to be a full IANA registry.
/// </summary>
public static class MimeTypes
{
    /// <summary>Fallback for anything not in the table — safe to send, tells the model nothing.</summary>
    public const string Unknown = "application/octet-stream";

    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        // Images: the only kinds that ride along as DataContent bytes for the vision model.
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".bmp"] = "image/bmp",
        [".webp"] = "image/webp",
        [".tif"] = "image/tiff",
        [".tiff"] = "image/tiff",

        // Documents.
        [".pdf"] = "application/pdf",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".ppt"] = "application/vnd.ms-powerpoint",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".odt"] = "application/vnd.oasis.opendocument.text",
        [".ods"] = "application/vnd.oasis.opendocument.spreadsheet",
        [".rtf"] = "application/rtf",
        [".epub"] = "application/epub+zip",
        [".eml"] = "message/rfc822",
        [".msg"] = "application/vnd.ms-outlook",

        // Text and code.
        [".txt"] = "text/plain",
        [".log"] = "text/plain",
        [".md"] = "text/markdown",
        [".markdown"] = "text/markdown",
        [".csv"] = "text/csv",
        [".tsv"] = "text/tab-separated-values",
        [".json"] = "application/json",
        [".xml"] = "application/xml",
        [".yml"] = "application/yaml",
        [".yaml"] = "application/yaml",
        [".html"] = "text/html",
        [".htm"] = "text/html",
        [".css"] = "text/css",
        [".js"] = "text/javascript",
        [".ts"] = "text/plain",
        [".cs"] = "text/plain",
        [".py"] = "text/x-python",
        [".sql"] = "text/plain",
        [".sh"] = "text/x-shellscript",
        [".ps1"] = "text/plain",
        [".ini"] = "text/plain",
        [".toml"] = "text/plain",
    };

    /// <summary>MIME type for a path's extension, or <see cref="Unknown"/> when it isn't recognised.</summary>
    public static string FromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Unknown;

        var ext = Path.GetExtension(path);
        return string.IsNullOrEmpty(ext) || !ByExtension.TryGetValue(ext, out var mime) ? Unknown : mime;
    }

    /// <summary>True when the extension names a raster image Floaty can send to a vision model.</summary>
    public static bool IsImage(string? path) =>
        FromPath(path).StartsWith("image/", StringComparison.Ordinal);
}
