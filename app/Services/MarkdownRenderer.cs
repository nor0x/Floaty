using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Floaty.Services;

/// <summary>
/// Turns an assistant message into the HTML shown inside a chat bubble. Pure and dependency-free
/// (like <see cref="TextChunker"/>) so it can be exercised in isolation.
/// </summary>
/// <remarks>
/// This renders untrusted text — model output, which routinely quotes documents the user dropped in —
/// into a document that holds a live JS-interop reference back into the app. The pipeline is therefore
/// built to be *less* capable than the default, not more:
/// <list type="bullet">
/// <item><c>DisableHtml</c> makes raw HTML render as visible escaped text instead of being parsed.</item>
/// <item><c>UseAdvancedExtensions</c> is deliberately avoided: it enables generic attributes, which let
/// markdown attach arbitrary attributes (<c>{onclick=...}</c>) and would survive <c>DisableHtml</c>.</item>
/// <item>Link and image URLs are filtered to an allowlist of schemes, which markdown alone does not do.</item>
/// </list>
/// </remarks>
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseSoftlineBreakAsHardlineBreak() // chat convention: a single newline is a line break
        .UsePipeTables()
        .UseAutoLinks()
        .UseTaskLists()
        .UseEmphasisExtras()
        .DisableHtml()
        .Build();

    /// <summary>Renders markdown to HTML, with unclosed code fences repaired and unsafe URLs stripped.</summary>
    /// <remarks>Not memoized here: each bubble already skips re-parsing when its text is unchanged, and a
    /// shared cache would churn through an entry per streaming repaint for no benefit.</remarks>
    public static string ToHtml(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return string.Empty;

        var document = Markdown.Parse(RepairFences(markdown), Pipeline);
        foreach (var link in document.Descendants<LinkInline>())
        {
            var allowed = link.IsImage ? IsAllowedImage(link.Url) : IsAllowedLink(link.Url);
            if (!allowed)
                link.Url = string.Empty;
        }

        return Markdown.ToHtml(document, Pipeline);
    }

    /// <summary>
    /// A reply that is still streaming routinely stops mid code block, and everything after the opening
    /// fence then renders as one giant code block that un-renders when the closing fence arrives. Closing
    /// the fence on the copy handed to Markdig avoids that flicker; the caller's text is never modified,
    /// because <c>ChatMessageVm.Text</c> is the raw markdown that persistence and the model history use.
    /// Unclosed emphasis and links need no repair — CommonMark already degrades them to literal characters.
    /// </summary>
    private static string RepairFences(string markdown)
    {
        if (!markdown.Contains("```", StringComparison.Ordinal)
            && !markdown.Contains("~~~", StringComparison.Ordinal))
            return markdown;

        var backticks = 0;
        var tildes = 0;
        foreach (var line in markdown.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
                backticks++;
            else if (trimmed.StartsWith("~~~", StringComparison.Ordinal))
                tildes++;
        }

        if (backticks % 2 == 0 && tildes % 2 == 0)
            return markdown;

        var suffix = backticks % 2 != 0 ? "\n```" : "\n~~~";
        return markdown + suffix;
    }

    // Only schemes that make sense to hand to the OS launcher. Notably excludes javascript: and file:.
    private static bool IsAllowedLink(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" or "mailto";

    // Images are restricted to what the app itself serves: the localfiles interceptor and inline data
    // URIs. A remote <img> would let a reply phone home from the overlay on every render.
    private static bool IsAllowedImage(string? url)
    {
        if (string.IsNullOrEmpty(url))
            return false;

        if (url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            return true;

        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme == "https"
            && uri.Host.Equals("localfiles", StringComparison.OrdinalIgnoreCase);
    }
}
