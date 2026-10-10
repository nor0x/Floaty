using System.Text;

namespace Floaty.Services;

/// <summary>
/// The YAML frontmatter of a Markdown file, read and written line by line so that rewriting a few keys
/// leaves everything else exactly as the user wrote it: keys Floaty doesn't know, their order, comments
/// and blank lines. Used by <see cref="JobService"/>, which rewrites a job's <c>lastRun</c> /
/// <c>nextRun</c> after every run and must not mangle the rest of a hand-edited file.
/// </summary>
/// <remarks>
/// Deliberately a subset of YAML rather than a YAML library: top-level <c>key: value</c> pairs, quoted
/// strings, and lists either inline (<c>[a, "b"]</c>) or as indented <c>- item</c> lines. That is all a
/// job file needs, and a full parser could not round-trip a file without reformatting it anyway.
/// Pure and dependency-free, so it can be exercised in isolation.
/// </remarks>
public sealed class Frontmatter
{
    private const string Fence = "---";

    // One per top-level key, plus one per comment/blank line (Key null) so they keep their place.
    private readonly List<Entry> _entries = new();

    private sealed class Entry
    {
        public string? Key;

        /// <summary>The key's line followed by its indented continuation lines (block list items).</summary>
        public List<string> Lines = new();
    }

    /// <summary>
    /// Splits <paramref name="content"/> into its frontmatter and body. A file without a leading
    /// <c>---</c> fence has empty frontmatter and is all body; so is one whose fence never closes.
    /// </summary>
    public static (Frontmatter Frontmatter, string Body) Parse(string content)
    {
        var result = new Frontmatter();
        var normalized = content.Replace("\r\n", "\n").Replace('\r', '\n');
        if (normalized.StartsWith('﻿'))
            normalized = normalized[1..];

        var lines = normalized.Split('\n');
        if (lines.Length == 0 || lines[0].TrimEnd() != Fence)
            return (result, normalized);

        var close = -1;
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].TrimEnd() == Fence)
            {
                close = i;
                break;
            }
        }

        if (close < 0)
            return (result, normalized);

        Entry? current = null;
        for (var i = 1; i < close; i++)
        {
            var line = lines[i];
            var isContinuation = line.Length > 0 && (char.IsWhiteSpace(line[0]) || line.StartsWith("- ", StringComparison.Ordinal));

            if (isContinuation && current?.Key is not null && line.Trim().Length > 0)
            {
                current.Lines.Add(line);
                continue;
            }

            var key = KeyOf(line);
            current = new Entry { Key = key };
            current.Lines.Add(line);
            result._entries.Add(current);
        }

        var body = string.Join('\n', lines[(close + 1)..]).TrimStart('\n');
        return (result, body);
    }

    /// <summary>Top-level keys in file order.</summary>
    public IEnumerable<string> Keys => _entries.Where(e => e.Key is not null).Select(e => e.Key!);

    public bool Has(string key) => Find(key) is not null;

    /// <summary>A scalar value with quotes and any trailing <c> # comment</c> removed; null when absent.</summary>
    public string? Get(string key)
    {
        var entry = Find(key);
        return entry is null ? null : Unquote(StripComment(InlineValue(entry)));
    }

    /// <summary>
    /// A list value, from either form: <c>[a, "b"]</c> on the key's line or <c>- a</c> lines under it.
    /// A plain scalar reads as a one-item list, so <c>mcpTools: mcp__x__y</c> still works. Empty when
    /// absent.
    /// </summary>
    public IReadOnlyList<string> GetList(string key)
    {
        var entry = Find(key);
        if (entry is null)
            return [];

        var inline = StripComment(InlineValue(entry));
        if (inline.StartsWith('['))
        {
            // An inline list may wrap onto continuation lines; join them back up first.
            var joined = string.Join(' ', new[] { inline }.Concat(entry.Lines.Skip(1).Select(l => l.Trim())));
            var end = joined.LastIndexOf(']');
            var inner = end > 0 ? joined[1..end] : joined[1..];
            return SplitInline(inner).Select(Unquote).Where(s => s.Length > 0).ToList();
        }

        if (inline.Length > 0)
            return [Unquote(inline)];

        return entry.Lines.Skip(1)
            .Select(l => l.Trim())
            .Where(l => l.StartsWith('-'))
            .Select(l => Unquote(StripComment(l[1..].Trim())))
            .Where(s => s.Length > 0)
            .ToList();
    }

    /// <summary>Sets a string value, quoting it when YAML would otherwise misread it (a cron's <c>*</c>).</summary>
    public void Set(string key, string value) => SetRaw(key, Quote(value));

    public void Set(string key, bool value) => SetRaw(key, value ? "true" : "false");

    /// <summary>An ISO-8601 timestamp with the local offset, which needs no quotes.</summary>
    public void Set(string key, DateTimeOffset value) =>
        SetRaw(key, value.ToString("yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Sets an inline list, <c>["a", "b"]</c>; an empty list removes the key.</summary>
    public void SetList(string key, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            Remove(key);
            return;
        }

        SetRaw(key, "[" + string.Join(", ", values.Select(v => "\"" + Escape(v) + "\"")) + "]");
    }

    /// <summary>Replaces the key's line (and any list lines under it) in place, or appends a new key.</summary>
    public void SetRaw(string key, string rawValue)
    {
        var entry = Find(key);
        if (entry is null)
        {
            entry = new Entry { Key = key };
            _entries.Add(entry);
        }

        entry.Lines = [$"{key}: {rawValue}"];
    }

    public void Remove(string key) => _entries.RemoveAll(e => string.Equals(e.Key, key, StringComparison.Ordinal));

    /// <summary>The whole file: fences, the entries as they are, a blank line, then <paramref name="body"/>.</summary>
    public string Render(string body)
    {
        var sb = new StringBuilder();
        sb.Append(Fence).Append('\n');
        foreach (var entry in _entries)
        {
            foreach (var line in entry.Lines)
                sb.Append(line).Append('\n');
        }

        sb.Append(Fence).Append('\n');

        var trimmed = body.TrimStart('\n');
        if (trimmed.Length > 0)
            sb.Append('\n').Append(trimmed);
        if (sb[^1] != '\n')
            sb.Append('\n');
        return sb.ToString();
    }

    private Entry? Find(string key) =>
        _entries.FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.Ordinal));

    // "key: value" at column 0 → "key". Comments, blanks and anything without a colon have no key.
    private static string? KeyOf(string line)
    {
        if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line[0] == '#')
            return null;

        var colon = line.IndexOf(':');
        if (colon <= 0)
            return null;

        var key = line[..colon].Trim().Trim('"', '\'');
        return key.Length == 0 ? null : key;
    }

    private static string InlineValue(Entry entry)
    {
        var line = entry.Lines[0];
        var colon = line.IndexOf(':');
        return colon < 0 ? string.Empty : line[(colon + 1)..].Trim();
    }

    // YAML only treats '#' as a comment after whitespace and outside quotes, so "MON#2" survives.
    private static string StripComment(string value)
    {
        char? quote = null;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (quote is not null)
            {
                if (c == '\\' && quote == '"')
                    i++;
                else if (c == quote)
                    quote = null;
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '#' && i > 0 && char.IsWhiteSpace(value[i - 1]))
            {
                return value[..i].TrimEnd();
            }
        }

        return value.Trim();
    }

    private static string Unquote(string value)
    {
        value = value.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            var inner = value[1..^1];
            var sb = new StringBuilder(inner.Length);
            for (var i = 0; i < inner.Length; i++)
            {
                if (inner[i] == '\\' && i + 1 < inner.Length)
                {
                    i++;
                    sb.Append(inner[i] switch { 'n' => '\n', 't' => '\t', var other => other });
                }
                else
                {
                    sb.Append(inner[i]);
                }
            }

            return sb.ToString();
        }

        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            return value[1..^1].Replace("''", "'");

        return value;
    }

    private static IEnumerable<string> SplitInline(string inner)
    {
        var sb = new StringBuilder();
        char? quote = null;
        foreach (var c in inner)
        {
            if (quote is not null)
            {
                if (c == quote)
                    quote = null;
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == ',')
            {
                yield return sb.ToString().Trim();
                sb.Clear();
                continue;
            }

            sb.Append(c);
        }

        if (sb.ToString().Trim().Length > 0)
            yield return sb.ToString().Trim();
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");

    // Plain words, numbers and model ids go out bare; anything YAML could misread is double-quoted.
    private static string Quote(string value)
    {
        var plain = value.Length > 0
            && value.Trim() == value
            && value.IndexOfAny([':', '#', '\n', '"', '\'', ',', '[', ']', '{', '}']) < 0
            && "*&!|>%@`-?".IndexOf(value[0]) < 0
            && !value.Contains('*');
        return plain ? value : "\"" + Escape(value) + "\"";
    }
}
