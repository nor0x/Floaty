using System.Globalization;
using System.Reflection;
using System.Text;

namespace Floaty.Services;

/// <summary>One captured window, as it appears in the day log.</summary>
/// <param name="LocalTime">When the window was read, in local time — the day log is a human timeline.</param>
/// <param name="AppName">Process name, e.g. <c>chrome</c>.</param>
/// <param name="Title">Window title.</param>
/// <param name="Document">Path to the file the window was showing, when one could be determined.</param>
/// <param name="Url">Address the window was showing, for browsers.</param>
public sealed record CaptureBlock(
    DateTime LocalTime,
    string AppName,
    string Title,
    string? Document,
    string? Url);

/// <summary>
/// Writes text-only screen history as markdown blocks and remembers every line already written
/// today, so a line costs storage and embedding tokens once per day however many windows repeat it.
/// Cross-capture repetition is the bulk of an accessibility dump: the same sidebar, tab strip and
/// status bar come back with every revisit, and <see cref="CaptureDedupe"/> can only accept or
/// reject a capture whole.
///
/// Two-phase on purpose. <see cref="SelectNovel"/> answers "what is new here?" without recording
/// anything, and <see cref="Commit"/> is called only once the capture has actually been stored —
/// otherwise a capture rejected downstream would burn its lines out of the ledger and they would
/// never be written anywhere.
///
/// The ledger is rebuilt from the day file rather than kept in a sidecar index: it survives
/// restarts, and it self-heals if the user edits or deletes the day file.
/// </summary>
public sealed class CaptureDayLog
{
    private const string AgentsFileName = "AGENTS.md";

    // Day-file lines that are structure rather than content, and so aren't part of the ledger.
    private static readonly string[] MetadataPrefixes = ["## ", "file: ", "url: ", "date: ", "captured_by: "];

    private const ulong FnvOffset = 14695981039346656037;
    private const ulong FnvPrime = 1099511628211;

    // The capture pipeline runs on a background task while Reset can arrive from the settings UI.
    private readonly object _gate = new();
    private readonly HashSet<ulong> _seen = [];
    private DateOnly? _date;

    /// <summary><c>~/.floaty/captures/2026-08-25.md</c> for the given day.</summary>
    public static string DayFilePath(DateOnly date) =>
        Path.Combine(FloatyPaths.Captures, $"{date:yyyy-MM-dd}.md");

    /// <summary>
    /// Returns the lines of <paramref name="lines"/> not already written on <paramref name="localNow"/>'s
    /// day, in order. Does not record them — call <see cref="Commit"/> once the capture is stored.
    /// </summary>
    public IReadOnlyList<string> SelectNovel(DateTime localNow, IReadOnlyList<string> lines)
    {
        var date = DateOnly.FromDateTime(localNow);
        var novel = new List<string>();

        lock (_gate)
        {
            RollTo(date);

            // Guard against duplicates inside one capture too: CapturePruner already deduplicates,
            // but SelectNovel must not depend on its caller having done so.
            var batch = new HashSet<ulong>();
            foreach (var line in lines)
            {
                var hash = Hash(line);
                if (!_seen.Contains(hash) && batch.Add(hash))
                    novel.Add(line);
            }
        }

        return novel;
    }

    /// <summary>
    /// Records <paramref name="novel"/> against the day and appends the rendered block to the day
    /// file. Best-effort on the file: the ledger is updated either way, because the lines have by
    /// then been stored in memory and re-writing them would be the duplication this class exists
    /// to prevent.
    /// </summary>
    public void Commit(CaptureBlock block, IReadOnlyList<string> novel)
    {
        var date = DateOnly.FromDateTime(block.LocalTime);

        lock (_gate)
        {
            RollTo(date);

            foreach (var line in novel)
                _seen.Add(Hash(line));

            try
            {
                var path = DayFilePath(date);
                if (!File.Exists(path))
                    File.WriteAllText(path, Frontmatter(date), Encoding.UTF8);

                File.AppendAllText(path, RenderBlock(block, novel), Encoding.UTF8);
                EnsureAgentsFile();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Floaty] Day log append failed: {ex.Message}");
            }
        }
    }

    /// <summary>Forgets the day's lines, e.g. after the user clears screen history.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _date = null;
            _seen.Clear();
        }
    }

    /// <summary>
    /// Deletes every day file in <c>~/.floaty/captures</c> and forgets the ledger. Day files aren't
    /// referenced by any memory node, so "Clear screen history" would otherwise leave them behind.
    /// <c>AGENTS.md</c> is kept: it documents the folder, it isn't history.
    /// </summary>
    public void DeleteDayFiles()
    {
        Reset();

        try
        {
            foreach (var path in Directory.EnumerateFiles(FloatyPaths.Captures, "*.md"))
            {
                if (!IsDayFile(path))
                    continue;

                try
                {
                    File.Delete(path);
                }
                catch
                {
                    // A day file held open by an editor isn't worth failing the clear over.
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Floaty] Day log cleanup failed: {ex.Message}");
        }
    }

    /// <summary>True when <paramref name="path"/> is named <c>yyyy-MM-dd.md</c>.</summary>
    public static bool IsDayFile(string path) =>
        DateOnly.TryParseExact(
            Path.GetFileNameWithoutExtension(path),
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out _);

    // --- Rendering -----------------------------------------------------------------------------

    /// <summary>
    /// One block of the timeline: a heading naming the time, app and window, the document or URL it
    /// was looking at so a reader can open the real thing, then the lines seen there.
    /// </summary>
    public static string RenderBlock(CaptureBlock block, IReadOnlyList<string> lines)
    {
        var sb = new StringBuilder();

        sb.Append("\n## ").Append(block.LocalTime.ToString("HH:mm", CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(block.AppName))
            sb.Append(" · ").Append(block.AppName);
        if (!string.IsNullOrWhiteSpace(block.Title))
            sb.Append(" · ").Append(block.Title);
        sb.Append("\n\n");

        if (!string.IsNullOrWhiteSpace(block.Document))
            sb.Append("file: ").Append(block.Document).Append('\n');
        if (!string.IsNullOrWhiteSpace(block.Url))
            sb.Append("url: ").Append(block.Url).Append('\n');
        if (!string.IsNullOrWhiteSpace(block.Document) || !string.IsNullOrWhiteSpace(block.Url))
            sb.Append('\n');

        foreach (var line in lines)
            sb.Append(line).Append('\n');

        return sb.ToString();
    }

    /// <summary>
    /// The same block as a standalone file — what a citation opens and what <c>read_capture</c>
    /// returns. Carries its own frontmatter so it reads correctly on its own.
    /// </summary>
    public static string RenderCaptureFile(CaptureBlock block, IReadOnlyList<string> lines) =>
        Frontmatter(DateOnly.FromDateTime(block.LocalTime)) + RenderBlock(block, lines);

    private static string Frontmatter(DateOnly date) =>
        $"---\ndate: {date:yyyy-MM-dd}\ncaptured_by: Floaty {AppVersion()}\n---\n";

    private static string AppVersion()
    {
        var informational = typeof(CaptureDayLog).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
            return typeof(CaptureDayLog).Assembly.GetName().Version?.ToString() ?? "0.0.0";

        // Strip the "+<commit sha>" source-revision suffix the SDK appends.
        var plus = informational.IndexOf('+');
        return plus > 0 ? informational[..plus] : informational;
    }

    // --- Ledger --------------------------------------------------------------------------------

    // Caller holds _gate.
    private void RollTo(DateOnly date)
    {
        if (_date == date)
            return;

        _date = date;
        _seen.Clear();

        // Rebuild from today's file so a restart mid-day doesn't re-write everything already logged.
        try
        {
            var path = DayFilePath(date);
            if (!File.Exists(path))
                return;

            foreach (var line in File.ReadLines(path))
            {
                if (IsMetadataLine(line))
                    continue;
                _seen.Add(Hash(line));
            }
        }
        catch (Exception ex)
        {
            // A ledger we couldn't rebuild costs a day of repeated lines, not correctness.
            System.Diagnostics.Debug.WriteLine($"[Floaty] Day log rebuild failed: {ex.Message}");
        }
    }

    private static bool IsMetadataLine(string line)
    {
        if (line.Length == 0 || line == "---")
            return true;

        foreach (var prefix in MetadataPrefixes)
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    // FNV-1a rather than string.GetHashCode(): the latter is randomized per process, and the ledger
    // is rebuilt from a file written by an earlier run.
    private static ulong Hash(string line)
    {
        var hash = FnvOffset;
        foreach (var c in line)
            hash = (hash ^ c) * FnvPrime;
        return hash;
    }

    // --- AGENTS.md -----------------------------------------------------------------------------

    /// <summary>
    /// Seeds the folder's <c>AGENTS.md</c> once, so whatever reads these files knows what they are.
    /// Never overwritten: the user is free to edit it.
    /// </summary>
    public static void EnsureAgentsFile()
    {
        try
        {
            var path = Path.Combine(FloatyPaths.Captures, AgentsFileName);
            if (!File.Exists(path))
                File.WriteAllText(path, AgentsFileContent, Encoding.UTF8);
        }
        catch
        {
            // Documentation is a nicety; never fail a capture over it.
        }
    }

    private const string AgentsFileContent = """
        # Screen history captures

        Floaty records the foreground window while screen history is on. This folder holds two
        views of the same material.

        ## `YYYY-MM-DD.md` — the day log

        One file per day, and the file you usually want. Read it whole: it is kept small enough
        for that on purpose.

            ---
            date: 2026-08-25
            captured_by: Floaty 1.2.0
            ---

            ## 09:41 · chrome · Tauri tray documentation

            url: https://v2.tauri.app/learn/system-tray/

            <text seen in that window, the first time it appeared today>

        Headings are the day's timeline: `## HH:MM · app · window title`. A block may carry a
        `file:` line (the document the window was showing) or a `url:` line (the address). Prefer
        opening those over trusting the captured text — the text is a flattened accessibility
        snapshot, so it can be reordered or partial, while the source is authoritative.

        **Body lines are written once per day.** A line that appeared in an earlier block is not
        repeated in later ones, so a block shows what was *new* on that screen rather than
        everything that was on it. Absence of a line from a block does not mean it wasn't there.

        Text is pruned before it is written: interface labels, menu entries and decorative rules
        are dropped, and short fragments survive only when they carry a URL, a path, a number or
        an email address. Credential-shaped strings are replaced with `[redacted]`, and password
        managers and private-browsing windows are never captured at all.

        ## `capture-*.md` — single blocks

        One file per capture, holding exactly the block that was appended to that day's log. These
        are what Floaty's own search cites, so a chat answer can link to the moment it came from.
        They are redundant if you are reading the day log.

        Times are local. Nothing here is captured while screen history is off.
        """;
}
