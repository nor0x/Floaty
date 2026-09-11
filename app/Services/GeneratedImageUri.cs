namespace Floaty.Services;

/// <summary>
/// The <c>floaty://image/&lt;file&gt;</c> pseudo-scheme a generated image is referenced by in chat markdown.
///
/// Chosen over an inline <c>data:</c> URI so a saved conversation stays a few hundred bytes rather than
/// carrying a megabyte of base64 per picture, and over <c>file://</c> so nothing outside
/// <see cref="FloatyPaths.GeneratedImages"/> can ever be addressed — the URL in a bubble is model output,
/// and the markdown renderer must not become a way to read arbitrary files off the machine.
/// Lives in Services because both <c>MarkdownRenderer</c> (allowlist) and <c>MarkdownPresenter</c>
/// (decoding) have to agree on it.
/// </summary>
public static class GeneratedImageUri
{
    public const string Prefix = "floaty://image/";

    /// <summary>The markdown URL that displays a file from <see cref="FloatyPaths.GeneratedImages"/>.</summary>
    public static string For(string fileName) => Prefix + fileName;

    /// <summary>
    /// Validates a URL and extracts the bare file name it names. Deliberately pure — no disk access —
    /// because the markdown allowlist calls it on every repaint.
    /// </summary>
    public static bool TryGetFileName(string? url, out string fileName)
    {
        fileName = string.Empty;

        if (string.IsNullOrEmpty(url) || !url.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var name = url[Prefix.Length..];
        if (name.Length == 0)
            return false;

        // GetFileName strips any directory, so comparing back to the original is what rejects
        // "../config.json" and friends rather than trusting a substring check.
        if (!string.Equals(name, Path.GetFileName(name), StringComparison.Ordinal))
            return false;

        if (!ImageExtensions.Contains(Path.GetExtension(name)))
            return false;

        fileName = name;
        return true;
    }

    /// <summary>The file on disk this URL names, or null when it is malformed or no longer exists.</summary>
    public static string? ResolvePath(string? url)
    {
        if (!TryGetFileName(url, out var fileName))
            return null;

        var path = Path.Combine(FloatyPaths.GeneratedImages, fileName);
        return File.Exists(path) ? path : null;
    }

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg",
        ".webp",
    };
}
