using System.Text.RegularExpressions;

namespace Floaty.Services;

/// <summary>
/// Turns a raw UI Automation tree walk into lines worth remembering. An accessibility dump is
/// mostly interface: menu labels, toolbar names, decorative rules, the same button text repeated
/// by every pane that hosts it. Embedding that buries the sentence the user actually read.
///
/// The rule is deliberately blunt — a line survives if it reads like content (four words or more)
/// or if it is short but identifying (a URL, a path, a version string, a ticket id, an email).
/// Everything else goes. Pure and dependency-free, like <see cref="TextChunker"/> and
/// <see cref="CaptureDedupe"/>, so it can be exercised in isolation.
/// </summary>
public static partial class CapturePruner
{
    // Lines shorter than this many words are interface labels unless IsHighValue says otherwise.
    private const int MinWords = 4;

    // Render as nothing but defeat IsNullOrWhiteSpace and length checks, so they're stripped before
    // anything else looks at the line: ZWSP, ZWNJ, ZWJ, word joiner, BOM.
    private static readonly char[] ZeroWidth = ['​', '‌', '‍', '⁠', '﻿'];

    /// <summary>
    /// True when a short line still carries information: a URL, an absolute or home-relative path,
    /// any digit (version strings, ticket ids, prices, dates), or an email address.
    /// </summary>
    public static bool IsHighValue(string line)
    {
        if (line.Contains("://", StringComparison.Ordinal)
            || line.StartsWith('/')
            || line.StartsWith("~/", StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var c in line)
        {
            if (c is >= '0' and <= '9')
                return true;
        }

        return EmailPattern().IsMatch(line);
    }

    /// <summary>
    /// Cleans one line and decides whether to keep it. Returns <c>null</c> for anything that reads
    /// as interface junk: empty, purely decorative (no letters or digits at all), or a short
    /// fragment that isn't <see cref="IsHighValue"/>.
    /// </summary>
    public static string? NormalizeLine(string line)
    {
        if (string.IsNullOrEmpty(line))
            return null;

        if (line.AsSpan().ContainsAny(ZeroWidth))
            line = string.Concat(line.Split(ZeroWidth));

        line = line.Trim();
        if (line.Length == 0)
            return null;

        // Separators, box drawing, "···" and friends: visible, but nothing to remember.
        var hasAlphanumeric = false;
        foreach (var c in line)
        {
            if (char.IsLetterOrDigit(c))
            {
                hasAlphanumeric = true;
                break;
            }
        }

        if (!hasAlphanumeric)
            return null;

        return WordCount(line) >= MinWords || IsHighValue(line) ? line : null;
    }

    /// <summary>
    /// Normalizes every line and drops exact repeats across the whole capture — not merely adjacent
    /// ones. A single window commonly repeats the same label in a tree, a tab strip and a status
    /// bar, and those copies are separated by other lines.
    /// </summary>
    public static List<string> Prune(IEnumerable<string> lines)
    {
        var kept = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in lines)
        {
            var normalized = NormalizeLine(line);
            if (normalized is not null && seen.Add(normalized))
                kept.Add(normalized);
        }

        return kept;
    }

    private static int WordCount(string line)
    {
        var words = 0;
        var inWord = false;

        foreach (var c in line)
        {
            if (char.IsWhiteSpace(c))
            {
                inWord = false;
            }
            else if (!inWord)
            {
                inWord = true;
                words++;
            }
        }

        return words;
    }

    [GeneratedRegex(@"\b\S+@\S+\.\S+\b")]
    private static partial Regex EmailPattern();
}
