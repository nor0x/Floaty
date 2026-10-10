using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Floaty.Services.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Floaty.Services;

/// <summary>A recurring job's run just finished; its messages are already in the job's thread on disk.</summary>
/// <param name="JobName">The job that ran.</param>
/// <param name="ConversationId">The job's thread, <see cref="JobScheduler.ThreadIdFor"/>.</param>
/// <param name="Error">Why the run failed; null on success.</param>
/// <param name="Messages">What this run appended to the thread, for a chat panel showing it to add live.</param>
public sealed record JobRunResult(
    string JobName,
    string ConversationId,
    string? Reply,
    string? Error,
    IReadOnlyList<StoredMessage> Messages)
{
    public bool Succeeded => Error is null;
}

/// <summary>
/// What a job toast opens: the job's thread, rather than a message in whatever conversation the panel
/// has up. Resolved by <c>ChatPanelView.RevealMessage</c>.
/// </summary>
public sealed record JobThreadTarget(string ConversationId);

/// <summary>
/// Fires recurring jobs (<see cref="JobService"/>) when they are due and runs each as a background chat
/// turn with the agent's normal tools. Each run is appended to the job's own thread in
/// <c>~/.floaty/conversations</c>, its outcome is written back into the job file's frontmatter, and the
/// user hears about it through a ring toast and/or a sound when Settings → Jobs says so.
/// </summary>
/// <remarks>
/// <para>
/// A 30 s tick rather than a timer per job, like the spec's scheduler: it is what makes a missed firing
/// (Floaty closed, the PC asleep) catch up exactly once - <c>nextRun</c> is simply in the past on the next
/// tick, and the run moves it past now. Runs are serialized: a model turn can take minutes and two at once
/// would compete for the same provider quota and the same MCP clients.
/// </para>
/// <para>
/// Nobody is watching a run, so the approval callback it passes declines everything that would show a
/// card (exec in the default mode, <c>update_system_prompt</c>, <c>install_update</c>) and only notes what
/// ran pre-approved. <see cref="JobRunContext"/> hides the job tools from the run, so a job can't schedule
/// more jobs. MCP tools are only those the job lists in <c>mcpTools</c>; a listed tool that can't be found
/// fails the run rather than running with less than the job asked for.
/// </para>
/// </remarks>
public sealed class JobScheduler : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    // Give startup (screen history hooks, the first chat) room before a catch-up run competes with it.
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(15);

    /// <summary>Runs kept in a job's thread; older ones are trimmed when a new one is appended.</summary>
    public const int MaxRunsPerThread = 50;

    /// <summary>Starts the system note that heads each run in a job thread; also how runs are counted.</summary>
    public const string RunHeaderPrefix = "⏰ ";

    private const int MaxErrorChars = 300;

    private readonly JobService _jobs;
    // Lazy because the chat service folds in every IChatToolset, JobTools among them, and JobTools needs
    // this scheduler: resolving it in the constructor would be a dependency cycle.
    private readonly Lazy<IChatService> _chat;
    private readonly AiClientFactory _clients;
    private readonly IMcpService _mcp;
    private readonly SkillService _skills;
    private readonly ConversationService _conversations;
    private readonly ToastService _toasts;
    private readonly ISoundService _sounds;
    private readonly SettingsService _settings;

    private readonly SemaphoreSlim _runGate = new(1, 1);

    // Jobs queued or running, so a tick (or a double-clicked Run now) can't queue the same job twice.
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);

    private readonly CancellationTokenSource _shutdown = new();
    private bool _started;

    public JobScheduler(
        JobService jobs,
        IServiceProvider services,
        AiClientFactory clients,
        IMcpService mcp,
        SkillService skills,
        ConversationService conversations,
        ToastService toasts,
        ISoundService sounds,
        SettingsService settings)
    {
        _jobs = jobs;
        _chat = new Lazy<IChatService>(services.GetRequiredService<IChatService>);
        _clients = clients;
        _mcp = mcp;
        _skills = skills;
        _conversations = conversations;
        _toasts = toasts;
        _sounds = sounds;
        _settings = settings;
    }

    /// <summary>Raised on a background thread when a job starts running.</summary>
    public event EventHandler<string>? RunStarted;

    /// <summary>
    /// Raised on a background thread once a run is recorded in the job file and appended to its thread,
    /// and before the toast, so a chat panel showing that thread has it by the time the toast is clicked.
    /// </summary>
    public event EventHandler<JobRunResult>? RunCompleted;

    /// <summary>The conversation id of a job's thread.</summary>
    public static string ThreadIdFor(string jobName) => $"job-{jobName}";

    /// <summary>Whether the job is queued or running right now.</summary>
    public bool IsBusy(string jobName) => _pending.ContainsKey(jobName);

    public void Start()
    {
        if (_started)
            return;
        _started = true;

        var token = _shutdown.Token;
        _ = Task.Run(() => RunLoopAsync(token), token);
    }

    /// <summary>
    /// Queues the job to run as soon as no other job is running, whatever its schedule or enabled flag
    /// (and even with jobs switched off globally) - the spec's "Run now". Returns false with a reason
    /// when it can't.
    /// </summary>
    public bool RunNow(string jobName, out string message)
    {
        var job = _jobs.Find(jobName);
        if (job is null)
        {
            message = $"No job named '{jobName}'.";
            return false;
        }

        if (!job.IsValid)
        {
            message = $"'{job.Name}' can't run: {job.ParseError}";
            return false;
        }

        if (!Enqueue(job.Name, scheduled: false))
        {
            message = $"'{job.Name}' is already running.";
            return false;
        }

        message = $"'{job.Name}' started.";
        return true;
    }

    private async Task RunLoopAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(StartupDelay, token);
            _jobs.MarkInterrupted();

            using var timer = new PeriodicTimer(TickInterval);
            do
            {
                try
                {
                    Tick();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Debug.WriteLine($"[Jobs] tick failed: {ex.Message}");
                }
            }
            while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private void Tick()
    {
        if (!_settings.Current.JobsEnabled)
            return;

        var now = DateTimeOffset.Now;
        foreach (var job in _jobs.Jobs)
        {
            if (job is { Enabled: true, IsValid: true, NextRun: { } due } && due <= now)
                Enqueue(job.Name, scheduled: true);
        }
    }

    private bool Enqueue(string jobName, bool scheduled)
    {
        if (!_pending.TryAdd(jobName, 0))
            return false;

        _ = Task.Run(async () =>
        {
            try
            {
                await _runGate.WaitAsync(_shutdown.Token);
                try
                {
                    await RunAsync(jobName, scheduled);
                }
                finally
                {
                    _runGate.Release();
                }
            }
            catch (OperationCanceledException)
            {
                // Shutting down while queued.
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Jobs] {jobName} failed outside the run: {ex.Message}");
            }
            finally
            {
                _pending.TryRemove(jobName, out _);
            }
        });
        return true;
    }

    private async Task RunAsync(string jobName, bool scheduled)
    {
        // Re-read: the file may have changed (or gone) while the run sat in the queue.
        var job = _jobs.Find(jobName);
        if (job is null || !job.IsValid || (scheduled && !job.Enabled))
            return;

        var startedAt = DateTimeOffset.Now;
        _jobs.RecordRunStarted(job.Name);
        RunStarted?.Invoke(this, job.Name);

        var notes = new List<StoredMessage>();
        var images = new List<GeneratedImageFile>();
        string? reply = null;
        string? error = null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        timeout.CancelAfter(RunTimeout);

        JobRunContext.Current = job.Name;
        try
        {
            IChatClient? client = null;
            ProviderProfile? profile = null;
            if (job.Model is { } model)
            {
                if (_clients.ResolveChat(model, out var modelError) is not { } resolved)
                    throw new JobRunException($"Model '{model}': {modelError}");
                (client, profile) = resolved;
            }
            else if (!_clients.IsConfigured(ModelRole.Chat))
            {
                throw new JobRunException("No chat model is configured. Set up a model provider in Settings.");
            }

            var mcpTools = await ResolveMcpToolsAsync(job.McpTools, timeout.Token);
            var (prompt, skillInstructions) = ResolveSkill(job.Instructions);

            reply = await _chat.Value.GetResponseAsync(
                [new ChatMessage(ChatRole.User, prompt)],
                skillInstructions: skillInstructions,
                toolApproval: request => DecideUnattended(request, notes),
                generatedImages: images,
                overrides: new ChatTurnOverrides(client, profile, mcpTools, BuildRunPrompt(job)),
                cancellationToken: timeout.Token);

            reply = AppendImages(reply, images);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            error = "Interrupted: Floaty quit before the run finished.";
        }
        catch (OperationCanceledException)
        {
            error = $"Timed out after {RunTimeout.TotalMinutes:0} minutes.";
        }
        catch (Exception ex)
        {
            error = OneLine(ex.Message);
        }
        finally
        {
            JobRunContext.Current = null;
        }

        // A scheduled run always moves on to the next firing; Run now only when the job was due anyway,
        // so testing a job doesn't skip its next real run.
        var advance = scheduled || job.NextRun is not { } nextRun || nextRun <= DateTimeOffset.Now;
        _jobs.RecordRunFinished(job.Name, startedAt, error, advance);

        var messages = AppendToThread(job, startedAt, scheduled, notes, reply, error);
        var result = new JobRunResult(job.Name, ThreadIdFor(job.Name), reply, error, messages);
        RunCompleted?.Invoke(this, result);
        Notify(result, images);
    }

    /// <summary>
    /// The run's extra system message: which job this is and that nobody is there to answer questions.
    /// The last run is included so a prompt like "everything new since the last run" has its anchor.
    /// </summary>
    private static string BuildRunPrompt(RecurringJob job)
    {
        var sb = new StringBuilder();
        sb.Append($"This is a scheduled run of the recurring job '{job.Name}' ({job.ScheduleText}). ");
        sb.Append(job.LastRun is { } last
            ? $"Its previous run was {last:dddd d MMMM yyyy, HH:mm} and ended with '{(job.LastStatus == JobStatus.Running ? JobStatus.Error : job.LastStatus)}'. "
            : "This is its first run. ");
        sb.Append("Nobody is watching: don't ask questions or wait for confirmation, and don't offer follow-ups. ");
        sb.Append("Do the task with your tools, then reply with a concise result - it is shown to the user later in this job's thread. ");
        sb.Append("Tools that need the user's approval are unavailable in a scheduled run; if one is declined, say what was skipped.");
        return sb.ToString();
    }

    // Approval cards need someone to click them. Pre-approved requests (exec in automatic mode) ran
    // anyway and are noted, the rest are declined - the same notes the chat panel leaves, so the thread
    // shows what happened either way.
    private static Task<ToolApprovalDecision> DecideUnattended(ToolApprovalRequest request, List<StoredMessage> notes)
    {
        lock (notes)
        {
            notes.Add(new StoredMessage
            {
                IsSystemNote = true,
                Text = request.PreApproved
                    ? request.ApprovedNote
                    : $"{request.DeclinedNote} (scheduled runs can't ask for approval)",
                Detail = request.Detail,
            });
        }

        return Task.FromResult(request.PreApproved ? ToolApprovalDecision.Once : ToolApprovalDecision.Declined);
    }

    /// <summary>
    /// The job's <c>mcpTools</c> allowlist as functions. Names are <c>mcp__server__tool</c>; servers are
    /// connected once each. Anything missing fails the run, per the spec: a job never runs with less (or
    /// more) access than it declared.
    /// </summary>
    private async Task<IReadOnlyList<AITool>> ResolveMcpToolsAsync(IReadOnlyList<string> names, CancellationToken token)
    {
        if (names.Count == 0)
            return [];

        var tools = new List<AITool>();
        foreach (var group in names.Select(ParseMcpToolName).GroupBy(n => n.Server, StringComparer.OrdinalIgnoreCase))
        {
            var server = group.Key;
            if (!_mcp.EnabledServers.Any(s => string.Equals(s.Name, server, StringComparison.OrdinalIgnoreCase)))
                throw new JobRunException($"MCP server '{server}' is not configured or is disabled (needed for {string.Join(", ", group.Select(g => g.Full))}).");

            IReadOnlyList<AIFunction> available;
            try
            {
                available = await _mcp.GetToolsAsync(server, token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new JobRunException($"MCP server '{server}' failed to connect: {OneLine(ex.Message)}");
            }

            foreach (var (_, tool, full) in group)
            {
                var match = available.FirstOrDefault(f => string.Equals(f.Name, tool, StringComparison.Ordinal))
                    ?? throw new JobRunException($"MCP tool '{full}' was not found on server '{server}'.");
                tools.Add(match);
            }
        }

        return tools;
    }

    /// <summary>
    /// <c>mcp__meeting-notes__get_transcript</c> → (meeting-notes, get_transcript). The server is
    /// everything up to the first <c>__</c> after the prefix, so tool names may contain underscores.
    /// </summary>
    public static (string Server, string Tool, string Full) ParseMcpToolName(string name)
    {
        const string prefix = "mcp__";
        var rest = name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..] : name;
        var split = rest.IndexOf("__", StringComparison.Ordinal);
        return split <= 0
            ? (rest, string.Empty, name)
            : (rest[..split], rest[(split + 2)..], name);
    }

    // A prompt starting with "/skill-name" runs with that skill's instructions, as it would in chat.
    private (string Prompt, string? SkillInstructions) ResolveSkill(string instructions)
    {
        if (!instructions.StartsWith('/'))
            return (instructions, null);

        var end = instructions.IndexOfAny([' ', '\n', '\r', '\t']);
        var token = end < 0 ? instructions[1..] : instructions[1..end];
        if (_skills.GetEnabled(token) is not { } skill)
            return (instructions, null);

        var remainder = end < 0 ? string.Empty : instructions[end..].Trim();
        return (remainder.Length == 0 ? "Run this skill." : remainder, skill.Instructions);
    }

    // Same treatment the chat panel gives generated images: a floaty://image reference in the reply,
    // unless the model already embedded it.
    private static string AppendImages(string reply, IReadOnlyList<GeneratedImageFile> images)
    {
        foreach (var image in images)
        {
            if (reply.Contains(image.FileName, StringComparison.Ordinal))
                continue;

            var markdown = $"![generated image]({GeneratedImageUri.For(image.FileName)})";
            reply = string.IsNullOrWhiteSpace(reply) || reply == "(no response)" ? markdown : reply + "\n\n" + markdown;
        }

        return reply;
    }

    /// <summary>
    /// Appends this run to the job's thread - a header note with the prompt folded under it, any approval
    /// notes, then the reply or the error - trims the thread to <see cref="MaxRunsPerThread"/> runs, and
    /// returns what was appended.
    /// </summary>
    private List<StoredMessage> AppendToThread(
        RecurringJob job, DateTimeOffset startedAt, bool scheduled, List<StoredMessage> notes, string? reply, string? error)
    {
        var appended = new List<StoredMessage>
        {
            new()
            {
                IsSystemNote = true,
                Text = $"{RunHeaderPrefix}{(scheduled ? "Scheduled run" : "Run now")} · {startedAt:ddd d MMM, HH:mm}",
                Detail = job.Instructions,
            },
        };

        lock (notes)
            appended.AddRange(notes);

        appended.Add(error is null
            ? new StoredMessage { IsUser = false, Text = reply ?? string.Empty }
            : new StoredMessage { IsSystemNote = true, Text = $"⚠ Run failed: {error}" });

        var id = ThreadIdFor(job.Name);
        try
        {
            var thread = _conversations.Load(id) ?? new Conversation { Id = id, CreatedUtc = DateTime.UtcNow };
            thread.Title = $"{RunHeaderPrefix}{job.Name}";
            thread.JobName = job.Name;
            thread.Messages.AddRange(appended);
            TrimRuns(thread.Messages);
            thread.UpdatedUtc = DateTime.UtcNow;
            _conversations.Save(thread);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Jobs] thread save for {job.Name} failed: {ex.Message}");
        }

        return appended;
    }

    private static void TrimRuns(List<StoredMessage> messages)
    {
        var headers = messages
            .Select((m, i) => (m, i))
            .Where(x => IsRunHeader(x.m))
            .Select(x => x.i)
            .ToList();

        if (headers.Count > MaxRunsPerThread)
            messages.RemoveRange(0, headers[headers.Count - MaxRunsPerThread]);
    }

    /// <summary>Whether a thread message is the note that starts a run.</summary>
    public static bool IsRunHeader(StoredMessage message) =>
        message.IsSystemNote && message.Text.StartsWith(RunHeaderPrefix, StringComparison.Ordinal);

    private void Notify(JobRunResult result, IReadOnlyList<GeneratedImageFile> images)
    {
        var config = _settings.Current;
        if (config.JobNotifyOnlyOnFailure && result.Succeeded)
            return;

        if (config.JobToastEnabled)
        {
            var body = result.Succeeded ? ToastText.Preview(result.Reply) : result.Error!;
            if (body.Length == 0)
                body = "Finished.";

            var toast = new FloatyToast(body)
            {
                Title = result.Succeeded ? $"{RunHeaderPrefix}{result.JobName}" : $"{result.JobName} failed",
                Kind = result.Succeeded ? ToastKind.Success : ToastKind.Error,
                ImagePath = images.Select(i => i.FullPath).FirstOrDefault(File.Exists),
                Target = new JobThreadTarget(result.ConversationId),
            };

            _ = ShowToastAsync(toast);
        }

        if (config.JobSoundEnabled)
            _sounds.Play(FloatySound.JobDone);
    }

    private async Task ShowToastAsync(FloatyToast toast)
    {
        try
        {
            await _toasts.ShowAsync(toast);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Jobs] toast failed: {ex.Message}");
        }
    }

    private static string OneLine(string text)
    {
        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length > MaxErrorChars ? line[..MaxErrorChars].TrimEnd() + "…" : line;
    }

    public void Dispose()
    {
        _shutdown.Cancel();
    }

    /// <summary>A run that failed for a reason worth showing as is (no model, a missing MCP tool).</summary>
    private sealed class JobRunException(string message) : Exception(message);
}
