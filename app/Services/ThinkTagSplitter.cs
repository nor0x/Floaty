using System.Text;

namespace Floaty.Services;

/// <summary>
/// Peels <c>&lt;think&gt;…&lt;/think&gt;</c> out of a text stream into the reasoning channel, for the
/// local runtimes (llama.cpp's server, some Ollama builds) that inline their reasoning in the ordinary
/// content stream instead of reporting it as <c>reasoning_content</c>.
/// </summary>
/// <remarks>
/// Two things make this less trivial than a string replace. Tags straddle chunk boundaries, so a trailing
/// run that could still grow into a tag has to be held back rather than emitted; and a model writing
/// *about* the tag - in a fenced code block, say - must not have its answer eaten. The second is handled
/// by only ever entering reasoning mode from the very start of the reply: an opening tag that is not the
/// first non-whitespace thing in the message is just text.
/// </remarks>
public sealed class ThinkTagSplitter
{
    private static readonly string[] OpenTags = { "<think>", "<thinking>" };
    private static readonly string[] CloseTags = { "</think>", "</thinking>" };

    // Longest tag; nothing longer than this ever needs holding back.
    private static readonly int MaxTagLength = CloseTags.Max(t => t.Length);

    private enum State
    {
        /// <summary>Before any content: an opening tag here starts reasoning.</summary>
        Scanning,

        /// <summary>Inside a thinking block, looking for the close.</summary>
        InReasoning,

        /// <summary>The opening window is past; everything from here is answer text.</summary>
        PassThrough,
    }

    private readonly StringBuilder _held = new();
    private State _state = State.Scanning;

    /// <summary>Splits one streamed piece of text, holding back anything that might still become a tag.</summary>
    public IEnumerable<ChatChunk> Feed(string text)
    {
        if (string.IsNullOrEmpty(text))
            yield break;

        _held.Append(text);

        while (_held.Length > 0)
        {
            var buffer = _held.ToString();

            if (_state == State.PassThrough)
            {
                _held.Clear();
                yield return new ChatChunk(buffer, IsReasoning: false);
                yield break;
            }

            if (_state == State.Scanning)
            {
                var start = FirstNonWhitespace(buffer);

                // Nothing but whitespace so far - keep it; it is either the gap before a tag or the
                // start of a plain answer, and we cannot tell yet which.
                if (start < 0)
                    yield break;

                if (buffer[start] != '<')
                {
                    _state = State.PassThrough;
                    continue;
                }

                var open = MatchAt(buffer, start, OpenTags);
                if (open is not null)
                {
                    // Drop the tag and the whitespace before it; reasoning starts after.
                    _held.Remove(0, start + open.Length);
                    _state = State.InReasoning;
                    continue;
                }

                // Still short enough to grow into a tag: wait for the next chunk.
                if (CouldStillBecomeTag(buffer, start, OpenTags))
                    yield break;

                _state = State.PassThrough;
                continue;
            }

            // InReasoning: everything up to the closing tag is reasoning.
            var (closeIndex, closeTag) = FindEarliest(buffer, CloseTags);
            if (closeTag is not null)
            {
                _held.Remove(0, closeIndex + closeTag.Length);
                _state = State.PassThrough;
                if (closeIndex > 0)
                    yield return new ChatChunk(buffer[..closeIndex], IsReasoning: true);
                continue;
            }

            var holdBack = PartialTagSuffixLength(buffer, CloseTags);
            if (holdBack >= buffer.Length)
                yield break;

            _held.Remove(0, buffer.Length - holdBack);
            yield return new ChatChunk(buffer[..^holdBack], IsReasoning: true);
            yield break;
        }
    }

    /// <summary>
    /// Emits whatever is still held once the stream ends. An unterminated <c>&lt;think&gt;</c> counts as
    /// reasoning; anything else was never a tag and is answer text.
    /// </summary>
    public IEnumerable<ChatChunk> Flush()
    {
        if (_held.Length == 0)
            yield break;

        var rest = _held.ToString();
        _held.Clear();
        yield return new ChatChunk(rest, _state == State.InReasoning);
    }

    private static int FirstNonWhitespace(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
                return i;
        }

        return -1;
    }

    // The tag sitting exactly at `index`, or null if none does.
    private static string? MatchAt(string text, int index, string[] tags)
    {
        foreach (var tag in tags)
        {
            if (text.AsSpan(index).StartsWith(tag.AsSpan(), StringComparison.Ordinal))
                return tag;
        }

        return null;
    }

    // Whether everything from `index` on is a proper prefix of some tag, and so might still become one.
    private static bool CouldStillBecomeTag(string text, int index, string[] tags)
    {
        foreach (var tag in tags)
        {
            if (tag.AsSpan().StartsWith(text.AsSpan(index)))
                return true;
        }

        return false;
    }

    private static (int Index, string? Tag) FindEarliest(string text, string[] tags)
    {
        var bestIndex = -1;
        string? bestTag = null;

        foreach (var tag in tags)
        {
            var index = text.IndexOf(tag, StringComparison.Ordinal);
            if (index < 0 || (bestIndex >= 0 && index >= bestIndex))
                continue;

            bestIndex = index;
            bestTag = tag;
        }

        return (bestIndex, bestTag);
    }

    // How many trailing characters could still grow into one of the tags, and so must not be emitted yet.
    private static int PartialTagSuffixLength(string text, string[] tags)
    {
        var max = Math.Min(MaxTagLength - 1, text.Length);
        for (var length = max; length > 0; length--)
        {
            foreach (var tag in tags)
            {
                if (tag.AsSpan().StartsWith(text.AsSpan(text.Length - length)))
                    return length;
            }
        }

        return 0;
    }
}
