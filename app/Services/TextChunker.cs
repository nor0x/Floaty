namespace Floaty.Services;

/// <summary>
/// Slices capture text for the two places a whole screen doesn't fit: embedding it, and showing it.
///
/// <see cref="Split"/> cuts text into embedding-sized pieces. One vector can't represent a whole
/// screen — an embedding of several thousand characters is diluted to the point of ranking badly,
/// and anything past the model's input limit isn't indexed at all — so each passage gets its own
/// vector and a capture becomes findable by any part of it rather than only by its opening. Cuts
/// fall on line boundaries because accessibility text arrives one UI element per line, and
/// consecutive chunks overlap so a passage straddling a boundary is findable from either side.
///
/// <see cref="BestWindow"/> picks which passage to actually show for a query.
/// </summary>
public static class TextChunker
{
    /// <summary>
    /// Hard ceiling on a chunk's total length, carried overlap included. Roughly 250 tokens — small
    /// enough that the resulting vector stays specific to one passage.
    /// </summary>
    public const int ChunkChars = 1000;

    /// <summary>How much of the previous chunk's tail is repeated at the head of the next.</summary>
    public const int OverlapChars = 150;

    /// <summary>
    /// Splits <paramref name="text"/> into overlapping chunks. Every character of the input appears
    /// in at least one chunk — nothing is dropped, however long the input.
    /// </summary>
    public static IReadOnlyList<string> Split(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<string>();

        text = text.Trim();
        if (text.Length <= ChunkChars)
            return new[] { text };

        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var chunks = new List<string>();
        var current = new List<string>();
        var length = 0;

        foreach (var line in lines)
        {
            // A single line longer than a whole chunk — a minified bundle, a one-line log dump —
            // has no boundary worth preserving, so it gets cut on length.
            if (line.Length > ChunkChars)
            {
                Flush(chunks, ref current, ref length, carryOverlap: false);
                for (var i = 0; i < line.Length; i += ChunkChars)
                    chunks.Add(line.Substring(i, Math.Min(ChunkChars, line.Length - i)));
                continue;
            }

            if (length + line.Length + 1 > ChunkChars && current.Count > 0)
                Flush(chunks, ref current, ref length, carryOverlap: true);

            current.Add(line);
            length += line.Length + 1;
        }

        Flush(chunks, ref current, ref length, carryOverlap: false);
        return chunks;
    }

    /// <summary>
    /// Returns at most <paramref name="max"/> characters of <paramref name="text"/> — the stretch
    /// most likely to answer <paramref name="query"/>, elided with ellipses where it was cut.
    ///
    /// Returning the opening instead is actively misleading: a capture ranks on all of its content,
    /// so it can win on a passage thousands of characters in and then show an opening that never
    /// mentions it. Chunked captures arrive roughly this size already, so this mostly earns its keep
    /// on the long single-vector captures stored before chunking existed.
    /// </summary>
    public static string BestWindow(string text, string query, int max)
    {
        text = text.Trim();
        if (text.Length <= max)
            return text;

        var terms = query
            .Split(default(char[]), StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim(',', '.', '?', '!', '"', '\'', ':', ';', '(', ')').ToLowerInvariant())
            .Where(t => t.Length >= 3)
            .Distinct()
            .ToArray();

        var bestStart = 0;
        var bestScore = 0;

        if (terms.Length > 0)
        {
            var haystack = text.ToLowerInvariant();
            for (var start = 0; start < text.Length; start += Math.Max(1, max / 4))
            {
                var window = haystack.AsSpan(start, Math.Min(max, text.Length - start));

                var score = 0;
                foreach (var term in terms)
                    score += Occurrences(window, term);

                if (score > bestScore)
                {
                    bestScore = score;
                    bestStart = start;
                }
            }
        }

        // Nothing matched anywhere, so one window is as good as another: keep the opening, which at
        // least carries the capture's title.
        if (bestScore == 0)
            return text[..max] + "…";

        var length = Math.Min(max, text.Length - bestStart);
        return (bestStart > 0 ? "…" : string.Empty)
            + text.Substring(bestStart, length)
            + (bestStart + length < text.Length ? "…" : string.Empty);
    }

    private static int Occurrences(ReadOnlySpan<char> haystack, string needle)
    {
        var count = 0;
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            haystack = haystack[(index + needle.Length)..];
            index = haystack.IndexOf(needle, StringComparison.Ordinal);
        }

        return count;
    }

    // Emits the pending lines as a chunk, then optionally seeds the next one with its tail.
    private static void Flush(List<string> chunks, ref List<string> current, ref int length, bool carryOverlap)
    {
        if (current.Count == 0)
            return;

        chunks.Add(string.Join("\n", current));

        if (!carryOverlap)
        {
            current = new List<string>();
            length = 0;
            return;
        }

        // Never carry the whole chunk: that would make no forward progress and loop forever.
        var carried = new List<string>();
        var carriedLength = 0;
        for (var i = current.Count - 1; i > 0 && carriedLength < OverlapChars; i--)
        {
            carried.Insert(0, current[i]);
            carriedLength += current[i].Length + 1;
        }

        current = carried;
        length = carriedLength;
    }
}
