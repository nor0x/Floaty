using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Floaty.Services;

/// <summary>
/// The catalog of recurring jobs: every <c>~/.floaty/jobs/*.md</c>, read through
/// <see cref="Frontmatter"/> and kept fresh by a <see cref="FileSystemWatcher"/>, so a job dropped in
/// or edited by hand is picked up without registration. Also the only writer of those files - Settings,
/// the chat's job tools and <see cref="JobScheduler"/>'s run bookkeeping all go through here, and every
/// write rewrites only the keys it means to.
/// </summary>
/// <remarks>
/// <para>
/// Owns keeping <c>nextRun</c> honest, because it is what the scheduler fires on and the user can edit
/// the file between ticks. On every scan an enabled job's <c>nextRun</c> is recomputed from now when it
/// is missing, when it isn't an occurrence of the schedule (the schedule was edited), or when the job was
/// just switched back on (so re-enabling doesn't fire a long-missed run). A <c>nextRun</c> that is merely
/// in the past is left alone: that is a firing missed while Floaty was closed, which the scheduler
/// catches up once. The rewrite triggers the watcher, whose rescan then finds nothing to change.
/// </para>
/// <para>
/// Unlike <see cref="CaptureRule"/>s, jobs live in their own files rather than <see cref="FloatyConfig"/>,
/// so a job write never fires <see cref="SettingsService.Changed"/> (which would drop every MCP client
/// mid-run) and never needs adopting by an open Settings window.
/// </para>
/// </remarks>
public sealed class JobService : IDisposable
{
    public const int MaxNameLength = 64;

    private static readonly Regex NamePattern = new("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.Compiled);
    private static readonly Regex McpToolPattern = new("^mcp__[^_].*__.+$", RegexOptions.Compiled);
    private static readonly TimeSpan WatchDebounce = TimeSpan.FromMilliseconds(300);

    private readonly Lock _gate = new();

    // Each job's enabled flag as of the previous scan, to spot a hand edit that switches one back on.
    private readonly Dictionary<string, bool> _seenEnabled = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<RecurringJob>? _cache;
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;

    public JobService()
    {
        StartWatching();
    }

    /// <summary>
    /// Raised after any change to the jobs folder - a write through this service or an edit from
    /// outside. Fires on a background thread; UI consumers marshal.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>All jobs, sorted by name, including invalid ones (with <see cref="RecurringJob.ParseError"/>).</summary>
    public IReadOnlyList<RecurringJob> Jobs
    {
        get
        {
            lock (_gate)
                return _cache ??= Scan();
        }
    }

    public RecurringJob? Find(string? name) =>
        Jobs.FirstOrDefault(j => string.Equals(j.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Drops the cache so the next read rescans the folder (Settings' Rescan button).</summary>
    public void Reload()
    {
        lock (_gate)
            _cache = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public static string PathFor(string name) => Path.Combine(FloatyPaths.Jobs, name + ".md");

    /// <summary>Lowercase letters, digits and dashes, starting with a letter or digit - it becomes a file name.</summary>
    public static bool IsValidName(string? name) => name is not null && NamePattern.IsMatch(name);

    /// <summary>"Morning Digest!" → "morning-digest": the friendliest valid name for what the user typed.</summary>
    public static string ToJobName(string? text)
    {
        var chars = (text ?? string.Empty).Trim().ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')
            .ToArray();
        var name = Regex.Replace(new string(chars), "-{2,}", "-").Trim('-');
        return name.Length > MaxNameLength ? name[..MaxNameLength].TrimEnd('-') : name;
    }

    /// <summary>
    /// Writes a new job file. Returns null with <paramref name="error"/> set (a sentence for the user or
    /// the model) when the name is taken or invalid, the schedule doesn't parse, or the prompt is empty.
    /// </summary>
    public RecurringJob? Create(
        string name,
        string schedule,
        string instructions,
        bool enabled,
        string? model,
        IReadOnlyList<string>? mcpTools,
        out string? error)
    {
        name = name.Trim();
        if (!IsValidName(name))
        {
            var suggestion = ToJobName(name) is { Length: > 0 } n ? n : "morning-digest";
            error = $"'{name}' is not a valid job name: use lowercase letters, digits and dashes (e.g. '{suggestion}').";
            return null;
        }

        if (!CronText.TryParse(schedule, out var cron, out error))
            return null;

        if (string.IsNullOrWhiteSpace(instructions))
        {
            error = "A job needs instructions: the prompt to run at each firing.";
            return null;
        }

        var tools = CleanTools(mcpTools);
        if (InvalidTool(tools) is { } badTool)
        {
            error = $"'{badTool}' is not an MCP tool name; use the form mcp__<server>__<tool>.";
            return null;
        }

        lock (_gate)
        {
            var path = PathFor(name);
            if (File.Exists(path))
            {
                error = $"A job named '{name}' already exists.";
                return null;
            }

            var fm = new Frontmatter();
            fm.Set("schedule", schedule.Trim());
            fm.Set("enabled", enabled);
            if (!string.IsNullOrWhiteSpace(model))
                fm.Set("model", model.Trim());
            fm.SetList("mcpTools", tools);
            if (enabled && CronText.Next(cron!, DateTimeOffset.Now) is { } next)
                fm.Set("nextRun", next);

            try
            {
                File.WriteAllText(path, fm.Render(instructions.Trim()));
            }
            catch (Exception ex)
            {
                error = $"Couldn't write {path}: {ex.Message}";
                return null;
            }

            _seenEnabled[name] = enabled;
            _cache = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        error = null;
        return Find(name);
    }

    /// <summary>
    /// Changes the given fields of an existing job; null leaves a field as it is, and an empty
    /// <paramref name="model"/> or <paramref name="mcpTools"/> clears it. A new schedule, or switching the
    /// job on, recomputes <c>nextRun</c> from now.
    /// </summary>
    public RecurringJob? Update(
        string name,
        string? schedule,
        string? instructions,
        bool? enabled,
        string? model,
        IReadOnlyList<string>? mcpTools,
        out string? error)
    {
        var job = Find(name);
        if (job is null)
        {
            error = $"No job named '{name}'.";
            return null;
        }

        Cronos.CronExpression? cron = job.Cron;
        if (schedule is not null && !CronText.TryParse(schedule, out cron, out error))
            return null;

        if (instructions is not null && string.IsNullOrWhiteSpace(instructions))
        {
            error = "Instructions can't be empty; delete the job instead, or pause it with enabled=false.";
            return null;
        }

        var tools = mcpTools is null ? null : CleanTools(mcpTools);
        if (tools is not null && InvalidTool(tools) is { } badTool)
        {
            error = $"'{badTool}' is not an MCP tool name; use the form mcp__<server>__<tool>.";
            return null;
        }

        var nowEnabled = enabled ?? job.Enabled;
        var recompute = (schedule is not null && schedule.Trim() != job.Schedule) || (enabled == true && !job.Enabled);

        var ok = Edit(job.Name, (fm, body) =>
        {
            if (schedule is not null)
                fm.Set("schedule", schedule.Trim());
            if (enabled is { } on)
                fm.Set("enabled", on);
            if (model is not null)
            {
                if (string.IsNullOrWhiteSpace(model))
                    fm.Remove("model");
                else
                    fm.Set("model", model.Trim());
            }

            if (tools is not null)
                fm.SetList("mcpTools", tools);

            if (recompute && nowEnabled && cron is not null && CronText.Next(cron, DateTimeOffset.Now) is { } next)
                fm.Set("nextRun", next);

            return instructions is null ? body : instructions.Trim();
        }, out error);

        if (!ok)
            return null;

        lock (_gate)
            _seenEnabled[job.Name] = nowEnabled;
        return Find(job.Name);
    }

    public bool SetEnabled(string name, bool enabled, out string? error) =>
        Update(name, null, null, enabled, null, null, out error) is not null;

    /// <summary>Deletes the job's file. Its chat thread stays, like any conversation, until the user deletes it.</summary>
    public bool Delete(string name, out string? error)
    {
        var job = Find(name);
        if (job is null)
        {
            error = $"No job named '{name}'.";
            return false;
        }

        lock (_gate)
        {
            try
            {
                File.Delete(job.FilePath);
            }
            catch (Exception ex)
            {
                error = $"Couldn't delete {job.FilePath}: {ex.Message}";
                return false;
            }

            _seenEnabled.Remove(job.Name);
            _cache = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        error = null;
        return true;
    }

    /// <summary>Marks a run as in progress (<c>lastStatus: running</c>), so a crash mid-run is visible.</summary>
    public void RecordRunStarted(string name) =>
        Edit(name, (fm, body) =>
        {
            fm.Set("lastStatus", JobStatus.Running);
            return body;
        }, out _);

    /// <summary>
    /// The scheduler's bookkeeping after a run: <c>lastRun</c> (when it started), <c>lastStatus</c>,
    /// <c>lastError</c> (cleared on success) and, when <paramref name="advanceNextRun"/>, the next firing
    /// after now. Run-now leaves <c>nextRun</c> alone unless it was already due.
    /// </summary>
    public void RecordRunFinished(string name, DateTimeOffset startedAt, string? error, bool advanceNextRun) =>
        Edit(name, (fm, body) =>
        {
            fm.Set("lastRun", startedAt);
            fm.Set("lastStatus", error is null ? JobStatus.Success : JobStatus.Error);
            if (error is null)
                fm.Remove("lastError");
            else
                fm.Set("lastError", error);

            if (advanceNextRun
                && CronText.TryParse(fm.Get("schedule"), out var cron, out _)
                && CronText.Next(cron!, DateTimeOffset.Now) is { } next)
            {
                fm.Set("nextRun", next);
            }

            return body;
        }, out _);

    /// <summary>
    /// At startup, turns every <c>lastStatus: running</c> left by a run Floaty quit in the middle of into
    /// an error, so the job doesn't look busy forever.
    /// </summary>
    public void MarkInterrupted()
    {
        foreach (var job in Jobs.Where(j => j.LastStatus == JobStatus.Running))
        {
            Edit(job.Name, (fm, body) =>
            {
                fm.Set("lastStatus", JobStatus.Error);
                fm.Set("lastError", "Interrupted: Floaty quit before the run finished.");
                return body;
            }, out _);
        }
    }

    // Read-modify-write of one job file under the lock; the edit returns the (possibly new) body.
    private bool Edit(string name, Func<Frontmatter, string, string> edit, out string? error)
    {
        lock (_gate)
        {
            var path = PathFor(name);
            try
            {
                var (fm, body) = Frontmatter.Parse(File.ReadAllText(path));
                var newBody = edit(fm, body);
                File.WriteAllText(path, fm.Render(newBody));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Jobs] write {name} failed: {ex.Message}");
                error = $"Couldn't update {path}: {ex.Message}";
                return false;
            }

            _cache = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        error = null;
        return true;
    }

    // Called under _gate.
    private IReadOnlyList<RecurringJob> Scan()
    {
        var jobs = new List<RecurringJob>();
        var now = DateTimeOffset.Now;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(FloatyPaths.Jobs, "*.md").ToList();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Jobs] scan failed: {ex.Message}");
            return jobs;
        }

        foreach (var file in files)
        {
            var job = Read(file);
            if (job is null)
                continue;

            job = KeepNextRunHonest(job, now);
            _seenEnabled[job.Name] = job.Enabled;
            jobs.Add(job);
        }

        return jobs.OrderBy(j => j.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static RecurringJob? Read(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Jobs] read {path} failed: {ex.Message}");
            return null;
        }

        var name = Path.GetFileNameWithoutExtension(path);
        var (fm, body) = Frontmatter.Parse(text);
        var schedule = fm.Get("schedule") ?? string.Empty;
        CronText.TryParse(schedule, out var cron, out var cronError);
        var instructions = body.Trim();

        string? parseError = null;
        if (!IsValidName(name))
            parseError = "Rename the file: job names use lowercase letters, digits and dashes.";
        else if (cronError is not null)
            parseError = cronError;
        else if (instructions.Length == 0)
            parseError = "The job has no prompt: write it below the frontmatter.";

        return new RecurringJob
        {
            Name = name,
            FilePath = path,
            Schedule = schedule,
            Cron = cron,
            Enabled = ParseBool(fm.Get("enabled")) ?? true,
            Model = NullIfEmpty(fm.Get("model")),
            McpTools = fm.GetList("mcpTools"),
            Instructions = instructions,
            LastRun = ParseTime(fm.Get("lastRun")),
            LastStatus = NullIfEmpty(fm.Get("lastStatus")),
            LastError = NullIfEmpty(fm.Get("lastError")),
            NextRun = ParseTime(fm.Get("nextRun")),
            ParseError = parseError,
        };
    }

    // Called under _gate, from Scan.
    private RecurringJob KeepNextRunHonest(RecurringJob job, DateTimeOffset now)
    {
        if (!job.Enabled || !job.IsValid)
            return job;

        var justEnabled = _seenEnabled.TryGetValue(job.Name, out var wasEnabled) && !wasEnabled;
        var stored = job.NextRun;
        var isOccurrence = stored is { } at && CronText.Next(job.Cron!, at.AddSeconds(-1)) == at;
        if (stored is not null && isOccurrence && !justEnabled)
            return job;

        var next = CronText.Next(job.Cron!, now);
        try
        {
            var (fm, body) = Frontmatter.Parse(File.ReadAllText(job.FilePath));
            if (next is { } value)
                fm.Set("nextRun", value);
            else
                fm.Remove("nextRun");
            File.WriteAllText(job.FilePath, fm.Render(body));
        }
        catch (Exception ex)
        {
            // Still schedule from the in-memory value; the next scan tries the write again.
            Debug.WriteLine($"[Jobs] nextRun write for {job.Name} failed: {ex.Message}");
        }

        return job with { NextRun = next };
    }

    private void StartWatching()
    {
        try
        {
            _watcher = new FileSystemWatcher(FloatyPaths.Jobs, "*.md")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            _watcher.Created += OnFolderChanged;
            _watcher.Changed += OnFolderChanged;
            _watcher.Deleted += OnFolderChanged;
            _watcher.Renamed += OnFolderChanged;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            // Without a watcher, hand edits show up on the next Rescan or service write instead.
            Debug.WriteLine($"[Jobs] watcher failed: {ex.Message}");
            _watcher = null;
        }
    }

    // Editors save in bursts (truncate, write, rename); one rescan after the burst settles.
    private void OnFolderChanged(object sender, FileSystemEventArgs e)
    {
        lock (_gate)
        {
            _debounce ??= new Timer(_ => Reload(), null, Timeout.Infinite, Timeout.Infinite);
            _debounce.Change(WatchDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    private static List<string> CleanTools(IReadOnlyList<string>? tools) =>
        (tools ?? []).Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal).ToList();

    private static string? InvalidTool(IReadOnlyList<string> tools) =>
        tools.FirstOrDefault(t => !McpToolPattern.IsMatch(t));

    private static bool? ParseBool(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "true" or "yes" or "on" => true,
        "false" or "no" or "off" => false,
        _ => null,
    };

    private static DateTimeOffset? ParseTime(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var t) ? t : null;

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
    }
}
