using Cronos;

namespace Floaty.Services;

/// <summary>
/// One recurring job: a prompt in <c>~/.floaty/jobs/&lt;name&gt;.md</c> that <see cref="JobScheduler"/>
/// runs on a cron schedule. The file's frontmatter is the source of truth, read by
/// <see cref="JobService"/>; this is a snapshot of it, rebuilt on every rescan.
/// </summary>
/// <remarks>
/// Frontmatter fields, after the agent-native recurring-jobs spec: <c>schedule</c> (required),
/// <c>enabled</c>, <c>model</c>, <c>mcpTools</c>, and the scheduler-managed <c>lastRun</c> /
/// <c>lastStatus</c> / <c>lastError</c> / <c>nextRun</c>. The spec's hosted-only fields (<c>runAs</c>,
/// <c>createdBy</c>, <c>orgId</c>) are left in the file untouched and otherwise ignored: Floaty runs
/// everything as its one local user.
/// </remarks>
public sealed record RecurringJob
{
    /// <summary>The file name without <c>.md</c>; also the job's id everywhere (tools, thread, Settings).</summary>
    public required string Name { get; init; }

    public required string FilePath { get; init; }

    /// <summary>The raw cron expression, as written.</summary>
    public string Schedule { get; init; } = string.Empty;

    /// <summary>Null when <see cref="Schedule"/> doesn't parse; <see cref="ParseError"/> says why.</summary>
    public CronExpression? Cron { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>Optional model override, <c>provider/model</c> or a bare model id - see <see cref="AiClientFactory.ResolveChat"/>.</summary>
    public string? Model { get; init; }

    /// <summary>MCP tools the run may call, as <c>mcp__server__tool</c>. Empty means no MCP tools at all.</summary>
    public IReadOnlyList<string> McpTools { get; init; } = [];

    /// <summary>The Markdown body: the prompt the agent runs at each firing.</summary>
    public string Instructions { get; init; } = string.Empty;

    public DateTimeOffset? LastRun { get; init; }

    /// <summary><see cref="JobStatus.Success"/>, <see cref="JobStatus.Error"/> or <see cref="JobStatus.Running"/>.</summary>
    public string? LastStatus { get; init; }

    public string? LastError { get; init; }

    public DateTimeOffset? NextRun { get; init; }

    /// <summary>Why this job can't run (bad schedule, empty prompt); null when it can.</summary>
    public string? ParseError { get; init; }

    public bool IsValid => ParseError is null && Cron is not null;

    /// <summary>"Every day at 07:00" - <see cref="CronText.Describe"/> of the schedule.</summary>
    public string ScheduleText => CronText.Describe(Schedule);
}

/// <summary>The values the scheduler writes into <c>lastStatus</c>.</summary>
public static class JobStatus
{
    public const string Success = "success";
    public const string Error = "error";

    /// <summary>Written when a run starts; still there at launch means Floaty quit mid-run.</summary>
    public const string Running = "running";
}
