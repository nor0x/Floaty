using System.Text;
using System.Text.RegularExpressions;

namespace Floaty.Services;

/// <summary>
/// Turns a streamed markdown reply into speakable chunks as it arrives, so voice output can start on the
/// first sentence instead of waiting for the whole answer. Pure text logic, no I/O.
/// </summary>
/// <remarks>
/// Markdown is stripped on the way through, because a voice reading "asterisk asterisk" or a
/// thirty-line code block is worse than silence: fenced code is skipped outright, links keep their
/// text, images and bare URLs vanish, and list/heading/quote markers are dropped. Lines without closing
/// punctuation (headings, list items) get a period so the voice pauses where the eye would.
///
/// Chunk sizes grow: the first sentence goes out alone for latency, later ones are merged so a long
/// reply costs a handful of requests rather than one per sentence.
/// </remarks>
public sealed partial class SpeechTextChunker
{
    // How much speakable text to gather before emitting chunk N (the last entry repeats).
    private static readonly int[] ChunkTargets = [1, 80, 180];

    // Comfortably under the speech endpoint's 4096-character input limit.
    private const int MaxChunk = 3500;

    // Words whose trailing period is not a sentence end (compared without that period).
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "e.g", "i.e", "etc", "vs", "cf", "approx", "mr", "mrs", "ms", "dr", "prof", "st", "fig",
    };

    private readonly StringBuilder _line = new();
    private readonly StringBuilder _ready = new();
    private bool _inFence;

    // True once the current line's list/heading marker has been decided and removed, which happens
    // before the newline arrives whenever a sentence ends mid-line.
    private bool _prefixDone;
    private int _emitted;

    /// <summary>Feeds the next streamed delta and returns any chunks that are now complete.</summary>
    public IReadOnlyList<string> Append(string delta)
    {
        if (string.IsNullOrEmpty(delta))
            return [];

        _line.Append(delta);

        int newline;
        while ((newline = IndexOf(_line, '\n')) >= 0)
        {
            var line = _line.ToString(0, newline);
            _line.Remove(0, newline + 1);
            ProcessCompleteLine(line);
        }

        ProcessPartialLine();
        return Drain(final: false);
    }

    /// <summary>Ends the reply: whatever is still buffered is returned, however short.</summary>
    public IReadOnlyList<string> Flush()
    {
        if (_line.Length > 0)
        {
            ProcessCompleteLine(_line.ToString());
            _line.Clear();
        }

        var chunks = Drain(final: true);
        _inFence = false;
        _prefixDone = false;
        _emitted = 0;
        return chunks;
    }

    /// <summary>The whole of <paramref name="markdown"/> as one speakable string — for read-aloud.</summary>
    public static string ToSpeakable(string markdown)
    {
        var chunker = new SpeechTextChunker();
        var parts = chunker.Append(markdown).Concat(chunker.Flush());
        return string.Join(' ', parts).Trim();
    }

    private void ProcessCompleteLine(string line)
    {
        // The start of this line was already consumed mid-stream, so it can be neither a fence nor
        // carry a marker: it is just the rest of a sentence.
        if (_prefixDone)
        {
            _prefixDone = false;
            AppendUnit(CleanInline(line), lineEnd: true);
            return;
        }

        var trimmed = line.Trim();

        if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
        {
            _inFence = !_inFence;
            return;
        }

        if (_inFence || trimmed.Length == 0 || HorizontalRule().IsMatch(trimmed) || TableSeparator().IsMatch(trimmed))
            return;

        var rest = trimmed[LinePrefix().Match(trimmed).Length..];
        AppendUnit(CleanInline(rest), lineEnd: true);
    }

    /// <summary>Speaks the finished sentences of a line whose newline hasn't arrived yet.</summary>
    private void ProcessPartialLine()
    {
        if (_inFence || _line.Length == 0)
            return;

        var text = _line.ToString();

        if (!_prefixDone)
        {
            var start = text.TrimStart();

            // Could still turn into a fence or a table row; only the whole line can tell.
            if (start.Length == 0 || start[0] is '`' or '~' or '|')
                return;

            // "1" might become "1. ", "-" might become "- ": wait until actual words follow the marker.
            var prefix = LinePrefix().Match(start);
            var rest = start[prefix.Length..];
            if (!rest.Any(char.IsLetter))
                return;

            text = rest;
            _line.Clear().Append(rest);
            _prefixDone = true;
        }

        Match? last = null;
        foreach (Match match in SentenceEnd().Matches(text))
        {
            if (!IsAbbreviation(text, match.Index))
                last = match;
        }

        if (last is null)
            return;

        var cut = last.Index + last.Length;
        AppendUnit(CleanInline(text[..cut]), lineEnd: false);
        _line.Remove(0, cut);
    }

    private void AppendUnit(string text, bool lineEnd)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        // A heading or list item ends without punctuation; give the voice somewhere to breathe.
        if (lineEnd && char.IsLetterOrDigit(text[^1]))
            text += ".";

        _ready.Append(text).Append(' ');
    }

    private List<string> Drain(bool final)
    {
        var chunks = new List<string>();
        var text = _ready.ToString().Trim();
        var target = ChunkTargets[Math.Min(_emitted, ChunkTargets.Length - 1)];

        if (text.Length == 0 || (!final && text.Length < target))
            return chunks;

        _ready.Clear();

        while (text.Length > MaxChunk)
        {
            var split = text.LastIndexOf(' ', MaxChunk);
            if (split <= 0)
                split = MaxChunk;

            chunks.Add(text[..split].Trim());
            text = text[split..].Trim();
        }

        if (text.Length > 0)
            chunks.Add(text);

        _emitted += chunks.Count;
        return chunks;
    }

    private static string CleanInline(string text)
    {
        text = Image().Replace(text, string.Empty);
        text = Link().Replace(text, "$1");
        text = LinkTail().Replace(text, string.Empty);   // a link cut in half by a sentence split
        text = BareUrl().Replace(text, string.Empty);
        text = HtmlTag().Replace(text, string.Empty);
        text = Emphasis().Replace(text, string.Empty);
        text = text.Replace("`", string.Empty).Replace('[', ' ').Replace(']', ' ');
        text = TablePipe().Replace(text, ", ");
        text = EmptyParens().Replace(text, string.Empty);
        text = Whitespace().Replace(text, " ");
        return text.Trim().Trim(',').Trim();
    }

    /// <summary>
    /// Whether the period at <paramref name="dot"/> closes an abbreviation rather than a sentence;
    /// cutting at "e.g." would make the voice pause mid-thought.
    /// </summary>
    private static bool IsAbbreviation(string text, int dot)
    {
        if (text[dot] != '.')
            return false;

        var start = dot;
        while (start > 0 && (char.IsLetter(text[start - 1]) || text[start - 1] == '.'))
            start--;

        return Abbreviations.Contains(text[start..dot]);
    }

    private static int IndexOf(StringBuilder builder, char value)
    {
        for (var i = 0; i < builder.Length; i++)
        {
            if (builder[i] == value)
                return i;
        }

        return -1;
    }

    // Heading hashes, bullets, ordered-list numbers and quote markers, possibly nested ("> - ").
    [GeneratedRegex(@"^\s*(?:(?:#{1,6}|[-*+]|\d{1,3}[.)]|>)\s+)*")]
    private static partial Regex LinePrefix();

    // Sentence-ending punctuation, any closing quotes/brackets, then the whitespace that proves it ended.
    // Abbreviations are filtered out in code (IsAbbreviation), not here.
    [GeneratedRegex(@"[.!?…][""'”’)\]]*\s+")]
    private static partial Regex SentenceEnd();

    [GeneratedRegex(@"^([-*_]\s*){3,}$")]
    private static partial Regex HorizontalRule();

    [GeneratedRegex(@"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?$")]
    private static partial Regex TableSeparator();

    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)")]
    private static partial Regex Image();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"\]\([^)]*\)")]
    private static partial Regex LinkTail();

    [GeneratedRegex(@"(?:https?|floaty)://\S+")]
    private static partial Regex BareUrl();

    [GeneratedRegex(@"<[^>\s][^>]*>")]
    private static partial Regex HtmlTag();

    // Bold/italic/strikethrough markers. Underscores only at word edges, so snake_case survives.
    [GeneratedRegex(@"\*+|~~|(?<!\w)_+|_+(?!\w)")]
    private static partial Regex Emphasis();

    [GeneratedRegex(@"\s*\|\s*")]
    private static partial Regex TablePipe();

    [GeneratedRegex(@"\(\s*\)")]
    private static partial Regex EmptyParens();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
