using System.ComponentModel;
using System.Text;
using Floaty.IconFont;
using Microsoft.Extensions.AI;

namespace Floaty.Services.Tools;

/// <summary>
/// Recurring jobs from chat: list them, read one with its run history, create / update / delete,
/// open the file, run one now, and look up MCP tool names for a job's <c>mcpTools</c>. The jobs
/// themselves are <see cref="JobService"/>'s files; runs are <see cref="JobScheduler"/>'s.
/// </summary>
/// <remarks>
/// Unlike capture rules, creating, changing and deleting a job goes through the approval card: a job is
/// a prompt that later runs unattended with the agent's tools, so a prompt injection from a captured
/// screen that could quietly schedule one would outlive the conversation it came from. Pausing a job is
/// the one write that needs no card - it only ever takes power away. Hidden entirely inside a job's own
/// run (<see cref="JobRunContext"/>).
/// </remarks>
public sealed class JobTools : IChatToolset
{
    private const int RecentRuns = 5;

    private readonly JobService _jobs;
    private readonly JobScheduler _scheduler;
    private readonly ConversationService _conversations;
    private readonly IMcpService _mcp;
    private readonly ISystemIntegrationService _system;

    public JobTools(
        JobService jobs,
        JobScheduler scheduler,
        ConversationService conversations,
        IMcpService mcp,
        ISystemIntegrationService system)
    {
        _jobs = jobs;
        _scheduler = scheduler;
        _conversations = conversations;
        _mcp = mcp;
        _system = system;

        Tools =
        [
            AIFunctionFactory.Create(ListJobs, name: "list_jobs"),
            AIFunctionFactory.Create(GetJob, name: "get_job"),
            AIFunctionFactory.Create(CreateJob, name: "create_job"),
            AIFunctionFactory.Create(UpdateJob, name: "update_job"),
            AIFunctionFactory.Create(DeleteJob, name: "delete_job"),
            AIFunctionFactory.Create(RunJob, name: "run_job"),
            AIFunctionFactory.Create(OpenJob, name: "open_job"),
            AIFunctionFactory.Create(ListMcpTools, name: "list_mcp_tools"),
        ];
    }

    public bool IsAvailable => JobRunContext.Current is null;

    public IReadOnlyList<AITool> Tools { get; }

    public string Guidance =>
        "You can manage recurring jobs: prompts Floaty runs on a cron schedule in the background, with " +
        "your normal tools, even when nobody is chatting. Each job is a Markdown file in ~/.floaty/jobs/<name>.md " +
        "(YAML frontmatter + the prompt as its body). Use list_jobs and get_job to answer questions about " +
        "jobs, their last run, status, errors and next run; create_job / update_job / delete_job to change " +
        "them (the user confirms each on a card, so don't ask them to confirm in chat first); run_job to run " +
        "one now; open_job to open its file. Schedules are standard 5-field cron in the user's local time " +
        "(minute hour day-of-month month day-of-week): \"0 7 * * *\" is daily at 07:00, \"0 9 * * 1-5\" " +
        "weekdays at 09:00, \"*/30 * * * *\" every 30 minutes. Names are lowercase-with-dashes. Write the " +
        "instructions as a self-contained prompt: the run has none of this conversation, and nobody can " +
        "answer questions while it runs. A run has no MCP tools unless the job lists them in mcp_tools as " +
        "mcp__<server>__<tool> - look the names up with list_mcp_tools and bind only what the job needs. " +
        "Each run's result lands in the job's own chat thread (\"⏰ <name>\" under /chats).";

    [Description("List the recurring jobs with their schedule, whether they are enabled, and their last and next run.")]
    private string ListJobs()
    {
        var jobs = _jobs.Jobs;
        if (jobs.Count == 0)
            return $"There are no recurring jobs. Jobs live in {FloatyPaths.Jobs}.";

        var sb = new StringBuilder($"{jobs.Count} job(s):");
        foreach (var job in jobs)
            sb.Append("\n- ").Append(Summary(job));
        return sb.ToString();
    }

    [Description("Get one recurring job in full: its frontmatter fields, prompt, file path and its most recent runs.")]
    private string GetJob(
        [Description("The job name from list_jobs.")] string name)
    {
        var job = _jobs.Find(name);
        if (job is null)
            return NotFound(name);

        var sb = new StringBuilder();
        sb.Append($"Job '{job.Name}' ({job.FilePath})\n");
        sb.Append($"schedule: {job.Schedule} ({job.ScheduleText})\n");
        sb.Append($"enabled: {(job.Enabled ? "true" : "false")}\n");
        if (job.Model is not null)
            sb.Append($"model: {job.Model}\n");
        if (job.McpTools.Count > 0)
            sb.Append($"mcpTools: {string.Join(", ", job.McpTools)}\n");
        sb.Append($"lastRun: {Time(job.LastRun) ?? "never"}\n");
        if (job.LastStatus is not null)
            sb.Append($"lastStatus: {job.LastStatus}{(_scheduler.IsBusy(job.Name) ? " (running now)" : string.Empty)}\n");
        if (job.LastError is not null)
            sb.Append($"lastError: {job.LastError}\n");
        sb.Append($"nextRun: {NextRunText(job)}\n");
        if (job.ParseError is not null)
            sb.Append($"problem: {job.ParseError}\n");
        sb.Append("\nInstructions:\n").Append(job.Instructions);

        var runs = RecentRunsOf(job.Name);
        sb.Append("\n\n");
        if (runs.Count == 0)
        {
            sb.Append("No runs recorded in its thread yet.");
        }
        else
        {
            sb.Append($"Most recent {runs.Count} run(s), newest first:");
            foreach (var (header, outcome) in runs)
                sb.Append("\n- ").Append(header.TrimStart(JobScheduler.RunHeaderPrefix.ToCharArray()).Trim()).Append(": ").Append(outcome);
        }

        return sb.ToString();
    }

    [Description("Create a recurring job: a prompt Floaty runs in the background on a cron schedule. The user " +
                 "confirms it on a card. Returns the next run times.")]
    private async Task<string> CreateJob(
        [Description("Short lowercase-with-dashes name, e.g. 'morning-digest'. Becomes the file name.")] string name,
        [Description("5-field cron in local time, e.g. '0 7 * * *' (daily 07:00) or '0 9 * * 1-5' (weekdays 09:00).")] string schedule,
        [Description("The self-contained prompt to run at each firing.")] string instructions,
        [Description("Start enabled (default true).")] bool enabled = true,
        [Description("Optional model override: 'provider/model' or a model id on the chat provider. Leave empty for the default.")] string? model = null,
        [Description("Optional MCP tools the job may call, as mcp__<server>__<tool> names from list_mcp_tools.")] string[]? mcp_tools = null)
    {
        var jobName = name?.Trim() ?? string.Empty;
        if (!JobService.IsValidName(jobName))
            return $"'{name}' is not a valid job name; try '{JobService.ToJobName(name)}'.";
        if (_jobs.Find(jobName) is not null)
            return $"A job named '{jobName}' already exists. Use update_job to change it.";
        if (!CronText.TryParse(schedule, out _, out var cronError))
            return cronError!;
        if (string.IsNullOrWhiteSpace(instructions))
            return "A job needs instructions: the prompt to run at each firing.";

        var detail = new StringBuilder()
            .Append($"{CronText.Describe(schedule)}  ({schedule.Trim()})")
            .Append(enabled ? string.Empty : " · paused")
            .Append(string.IsNullOrWhiteSpace(model) ? string.Empty : $" · model {model.Trim()}")
            .Append(mcp_tools is { Length: > 0 } ? $"\nMCP tools: {string.Join(", ", mcp_tools)}" : string.Empty)
            .Append("\n\n").Append(instructions.Trim())
            .ToString();

        var decision = await ToolApproval.RequestAsync(new ToolApprovalRequest(
            Header: $"Create the recurring job '{jobName}'?",
            Detail: detail,
            SubDetail: $"Runs unattended with Floaty's tools · {JobService.PathFor(jobName)}",
            ConfirmLabel: "Create job",
            ApprovedNote: $"Created recurring job '{jobName}'",
            DeclinedNote: $"Declined creating recurring job '{jobName}'",
            Icon: TablerLine.CalendarRepeat));
        if (decision is null)
            return "Cannot create a job: no approval channel is available in this context.";
        if (decision == ToolApprovalDecision.Declined)
            return "The user declined creating this job.";

        var job = _jobs.Create(jobName, schedule, instructions, enabled, model, mcp_tools, out var error);
        if (job is null)
            return error ?? "The job could not be created.";

        var sb = new StringBuilder($"Created job '{job.Name}' ({job.ScheduleText}) at {job.FilePath}.");
        AppendNextRuns(sb, job);
        if (!enabled)
            sb.Append(" It is paused; enable it with update_job.");
        return sb.ToString();
    }

    [Description("Change a recurring job. Only the fields you pass change; pass an empty model or an empty " +
                 "mcp_tools list to clear them. Pausing (enabled=false) needs no confirmation; anything else " +
                 "is confirmed by the user on a card.")]
    private async Task<string> UpdateJob(
        [Description("The job name from list_jobs.")] string name,
        [Description("New 5-field cron schedule, local time.")] string? schedule = null,
        [Description("New prompt, replacing the old one entirely.")] string? instructions = null,
        [Description("True to enable, false to pause.")] bool? enabled = null,
        [Description("New model override, or empty to use the default again.")] string? model = null,
        [Description("New MCP tool allowlist (mcp__<server>__<tool>), replacing the old one; empty to clear.")] string[]? mcp_tools = null)
    {
        var job = _jobs.Find(name);
        if (job is null)
            return NotFound(name);

        if (schedule is not null && !CronText.TryParse(schedule, out _, out var cronError))
            return cronError!;

        var onlyPausing = enabled == false && schedule is null && instructions is null && model is null && mcp_tools is null;
        if (!onlyPausing)
        {
            var changes = new StringBuilder();
            if (schedule is not null)
                changes.Append($"schedule: {CronText.Describe(schedule)}  ({schedule.Trim()})\n");
            if (enabled is { } on)
                changes.Append($"enabled: {(on ? "true" : "false")}\n");
            if (model is not null)
                changes.Append($"model: {(string.IsNullOrWhiteSpace(model) ? "(default)" : model.Trim())}\n");
            if (mcp_tools is not null)
                changes.Append($"mcpTools: {(mcp_tools.Length == 0 ? "(none)" : string.Join(", ", mcp_tools))}\n");
            if (instructions is not null)
                changes.Append("\n").Append(instructions.Trim());

            var decision = await ToolApproval.RequestAsync(new ToolApprovalRequest(
                Header: $"Change the recurring job '{job.Name}'?",
                Detail: changes.ToString().TrimEnd(),
                SubDetail: job.FilePath,
                ConfirmLabel: "Update job",
                ApprovedNote: $"Updated recurring job '{job.Name}'",
                DeclinedNote: $"Declined changing recurring job '{job.Name}'",
                Icon: TablerLine.CalendarRepeat));
            if (decision is null)
                return "Cannot change a job: no approval channel is available in this context.";
            if (decision == ToolApprovalDecision.Declined)
                return "The user declined this change.";
        }

        var updated = _jobs.Update(job.Name, schedule, instructions, enabled, model, mcp_tools, out var error);
        if (updated is null)
            return error ?? "The job could not be updated.";

        var sb = new StringBuilder($"Updated job '{updated.Name}': {Summary(updated)}.");
        if (updated.Enabled)
            AppendNextRuns(sb, updated);
        return sb.ToString();
    }

    [Description("Delete a recurring job's file. The user confirms it on a card. Its chat thread of past runs is kept.")]
    private async Task<string> DeleteJob(
        [Description("The job name from list_jobs.")] string name)
    {
        var job = _jobs.Find(name);
        if (job is null)
            return NotFound(name);

        var decision = await ToolApproval.RequestAsync(new ToolApprovalRequest(
            Header: $"Delete the recurring job '{job.Name}'?",
            Detail: $"{job.ScheduleText}\n\n{job.Instructions}",
            SubDetail: job.FilePath,
            ConfirmLabel: "Delete job",
            ApprovedNote: $"Deleted recurring job '{job.Name}'",
            DeclinedNote: $"Declined deleting recurring job '{job.Name}'",
            Icon: TablerLine.Trash));
        if (decision is null)
            return "Cannot delete a job: no approval channel is available in this context.";
        if (decision == ToolApprovalDecision.Declined)
            return "The user declined deleting this job.";

        return _jobs.Delete(job.Name, out var error)
            ? $"Deleted job '{job.Name}'. Its past runs stay in the \"{JobScheduler.RunHeaderPrefix}{job.Name}\" thread."
            : error ?? "The job could not be deleted.";
    }

    [Description("Run a recurring job now, in the background, regardless of its schedule. Its next scheduled run is unchanged.")]
    private string RunJob(
        [Description("The job name from list_jobs.")] string name)
    {
        return _scheduler.RunNow(name?.Trim() ?? string.Empty, out var message)
            ? $"{message} The result will appear in the \"{JobScheduler.RunHeaderPrefix}{_jobs.Find(name)?.Name ?? name}\" thread; get_job shows its status once it finishes."
            : message;
    }

    [Description("Open a recurring job's Markdown file in the user's default editor.")]
    private string OpenJob(
        [Description("The job name from list_jobs.")] string name)
    {
        var job = _jobs.Find(name);
        if (job is null)
            return NotFound(name);

        if (!_system.IsSupported)
            return $"Opening files isn't supported here. The file is {job.FilePath}.";

        var result = _system.ShellOpen(job.FilePath);
        return result.Ok ? $"Opened {job.FilePath}." : result.Message;
    }

    [Description("List the tools of the user's enabled MCP servers as mcp__<server>__<tool> names, for a job's mcp_tools.")]
    private async Task<string> ListMcpTools(
        [Description("Optional: only this server.")] string? server = null,
        CancellationToken cancellationToken = default)
    {
        var servers = _mcp.EnabledServers
            .Where(s => string.IsNullOrWhiteSpace(server) || string.Equals(s.Name, server.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (servers.Count == 0)
            return string.IsNullOrWhiteSpace(server)
                ? "No MCP servers are enabled. They are added in Settings → MCP."
                : $"No enabled MCP server named '{server}'.";

        var sb = new StringBuilder();
        foreach (var config in servers)
        {
            sb.Append($"Server '{config.Name}':");
            try
            {
                var tools = await _mcp.GetToolsAsync(config.Name, cancellationToken);
                if (tools.Count == 0)
                    sb.Append(" no tools.");
                foreach (var tool in tools)
                {
                    sb.Append($"\n- mcp__{config.Name}__{tool.Name}");
                    if (!string.IsNullOrWhiteSpace(tool.Description))
                        sb.Append(" — ").Append(Clip(tool.Description, 160));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                sb.Append($" failed to connect: {ex.Message}");
            }

            sb.Append('\n');
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>"morning-digest · Every day at 07:00 · enabled · last ✓ 10 Oct 07:00 · next Sat 11 Oct 07:00"</summary>
    private string Summary(RecurringJob job)
    {
        var sb = new StringBuilder($"{job.Name} · {job.ScheduleText} ({job.Schedule}) · {(job.Enabled ? "enabled" : "paused")}");
        if (job.ParseError is not null)
            sb.Append($" · problem: {job.ParseError}");
        if (_scheduler.IsBusy(job.Name))
            sb.Append(" · running now");
        sb.Append(job.LastRun is { } last ? $" · last run {last:ddd d MMM HH:mm} ({job.LastStatus ?? "?"})" : " · never run");
        if (job.LastError is not null && job.LastStatus == JobStatus.Error)
            sb.Append($": {job.LastError}");
        sb.Append($" · next {NextRunText(job)}");
        return sb.ToString();
    }

    private string NextRunText(RecurringJob job)
    {
        if (!job.Enabled)
            return "none (paused)";
        if (!job.IsValid)
            return "none (invalid)";
        if (job.NextRun is not { } next)
            return "none";
        var text = Time(next)!;
        return next <= DateTimeOffset.Now ? $"{text} (due now)" : text;
    }

    private static void AppendNextRuns(StringBuilder sb, RecurringJob job)
    {
        if (job.Cron is null)
            return;
        var next = CronText.NextFew(job.Cron, DateTimeOffset.Now, 3);
        if (next.Count > 0)
            sb.Append(" Next runs: ").Append(string.Join(", ", next.Select(t => Time(t)))).Append('.');
    }

    // Newest first: each run's header note and how it ended, read back from the job's thread.
    private List<(string Header, string Outcome)> RecentRunsOf(string jobName)
    {
        var thread = _conversations.Load(JobScheduler.ThreadIdFor(jobName));
        if (thread is null)
            return [];

        var runs = new List<(string, string)>();
        var messages = thread.Messages;
        for (var i = messages.Count - 1; i >= 0 && runs.Count < RecentRuns; i--)
        {
            if (!JobScheduler.IsRunHeader(messages[i]))
                continue;

            var end = messages.FindIndex(i + 1, JobScheduler.IsRunHeader);
            var body = messages.Skip(i + 1).Take((end < 0 ? messages.Count : end) - i - 1).ToList();
            var failure = body.LastOrDefault(m => m.IsSystemNote && m.Text.StartsWith("⚠", StringComparison.Ordinal));
            var reply = body.LastOrDefault(m => !m.IsSystemNote && !m.IsUser);
            var outcome = failure is not null
                ? failure.Text
                : reply is not null ? "✓ " + Clip(ToastText.Preview(reply.Text), 200) : "(no result recorded)";
            runs.Add((messages[i].Text, outcome));
        }

        return runs;
    }

    private static string? Time(DateTimeOffset? t) => t?.ToLocalTime().ToString("ddd d MMM yyyy HH:mm");

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max].TrimEnd() + "…";

    private static string NotFound(string? name) => $"No job named '{name}'. Call list_jobs for the names.";
}
