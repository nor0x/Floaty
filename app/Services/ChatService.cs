using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Floaty.Services;

/// <summary>
/// Sends a conversation to the configured LLM and returns the assistant's reply text.
/// </summary>
public interface IChatService
{
    IAsyncEnumerable<string> GetStreamingResponseAsync(
        IReadOnlyList<ChatMessage> history,
        string? mcpServer = null,
        ICollection<MemoryCitation>? citations = null,
        string? skillInstructions = null,
        Func<ExecApprovalRequest, Task<bool>>? execApproval = null,
        CancellationToken cancellationToken = default);

    Task<string> GetResponseAsync(
        IReadOnlyList<ChatMessage> history,
        string? mcpServer = null,
        ICollection<MemoryCitation>? citations = null,
        string? skillInstructions = null,
        Func<ExecApprovalRequest, Task<bool>>? execApproval = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A pending shell command awaiting the user's approval before the <c>exec</c> tool runs it. Surfaced from
/// the tool back to the overlay via the approval callback threaded through <see cref="IChatService"/>.
/// </summary>
public sealed record ExecApprovalRequest(string Command, string ShellName, string? WorkingDirectory);

/// <summary>
/// Microsoft.Extensions.AI-backed chat service. Builds an <see cref="IChatClient"/> from the
/// OpenAI settings on demand and rebuilds it whenever the configuration changes. Exposes a
/// <c>search_captures</c> tool plus, when scoped via a <c>/server</c> slash command, that MCP
/// server's tools.
/// </summary>
public sealed class ChatService : IChatService
{
    private const string DefaultSystemPrompt =
        "You are Floaty, a desktop assistant that lives in a floating overlay. The user can capture " +
        "what's on their screen, and Floaty may also snapshot windows automatically as the user switches " +
        "between them (screen history); both are stored in local memory. When the user asks about " +
        "something they previously saw, viewed, read, or captured — or about their earlier activity — " +
        "call the search_captures tool to retrieve it before answering, and ground your answer in what " +
        "it returns. When the user asks you to remember a durable fact, call the save_memory tool to " +
        "persist it. Be concise.";

    // Per-turn sink the search_captures tool writes its sources into; flows via the async call chain
    // from GetStreamingResponseAsync into the function-invocation middleware.
    private static readonly AsyncLocal<ICollection<MemoryCitation>?> _citationSink = new();

    // Per-turn callback the exec tool uses to ask the UI to approve a command before running it. Flows via
    // the async call chain just like the citation sink. Null when the caller offers no approval channel
    // (e.g. background summarization), in which case the exec tool refuses to run anything.
    private static readonly AsyncLocal<Func<ExecApprovalRequest, Task<bool>>?> _execApprovalSink = new();

    private readonly SettingsService _settings;
    private readonly IMemoryService _memory;
    private readonly IMcpService _mcp;
    private readonly AIFunction _searchTool;
    private readonly AIFunction _saveTool;
    private readonly AIFunction _execTool;

    private IChatClient? _client;
    private string? _clientKey;
    private string? _clientModel;

    public ChatService(SettingsService settings, IMemoryService memory, IMcpService mcp)
    {
        _settings = settings;
        _memory = memory;
        _mcp = mcp;
        // Drop the cached client when settings change so the next call picks up the new key/model.
        _settings.Changed += (_, _) => _client = null;

        _searchTool = AIFunctionFactory.Create(SearchCaptures, name: "search_captures");
        _saveTool = AIFunctionFactory.Create(SaveMemory, name: "save_memory");
        _execTool = AIFunctionFactory.Create(Exec, name: "exec");
    }

    public async IAsyncEnumerable<string> GetStreamingResponseAsync(
        IReadOnlyList<ChatMessage> history,
        string? mcpServer = null,
        ICollection<MemoryCitation>? citations = null,
        string? skillInstructions = null,
        Func<ExecApprovalRequest, Task<bool>>? execApproval = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var config = _settings.Current;

        if (string.IsNullOrWhiteSpace(config.OpenAiApiKey))
        {
            yield return "Add your OpenAI API key in Settings (⚙) to start chatting.";
            yield break;
        }

        // Expose the sink so the search_captures tool can record which sources it returned this turn.
        _citationSink.Value = citations;

        // Expose the approval callback so the exec tool can gate each command on the user's confirmation.
        _execApprovalSink.Value = execApproval;

        var client = GetOrCreateClient(config);

        var messages = new List<ChatMessage> { new(ChatRole.System, _settings.GetSystemPrompt(DefaultSystemPrompt)) };

        // When invoked via /skill, inject that skill's instructions as additional system guidance.
        if (!string.IsNullOrWhiteSpace(skillInstructions))
            messages.Add(new ChatMessage(ChatRole.System,
                $"You are using a Floaty skill. Follow its instructions:\n\n{skillInstructions}"));

        // Always expose memory search + save; add the scoped MCP server's tools when invoked via /server.
        var tools = new List<AITool> { _searchTool, _saveTool };

        // Only expose the shell tool when the user has opted in. Every command it runs is still gated on an
        // explicit approval prompt in the overlay.
        if (config.ExecEnabled)
        {
            tools.Add(_execTool);
            messages.Add(new ChatMessage(ChatRole.System,
                "You can run shell commands on the user's computer with the exec tool — use it to create, " +
                "read, or edit files, run programs, inspect the system, or automate tasks. Every command is " +
                "shown to the user for approval before it runs, so prefer the smallest, safest command that " +
                "accomplishes the goal and briefly explain anything destructive before running it."));
        }

        if (!string.IsNullOrWhiteSpace(mcpServer))
        {
            var mcpTools = await _mcp.GetToolsAsync(mcpServer, cancellationToken);
            tools.AddRange(mcpTools);
            messages.Add(new ChatMessage(ChatRole.System,
                $"The user invoked the '{mcpServer}' MCP server. Prefer its tools to fulfill the request."));
        }

        messages.AddRange(history);

        var options = new ChatOptions { Tools = tools };

        await foreach (var update in client.GetStreamingResponseAsync(messages, options, cancellationToken)
            .WithCancellation(cancellationToken))
        {
            if (!string.IsNullOrEmpty(update.Text))
                yield return update.Text;
        }
    }

    public async Task<string> GetResponseAsync(
        IReadOnlyList<ChatMessage> history,
        string? mcpServer = null,
        ICollection<MemoryCitation>? citations = null,
        string? skillInstructions = null,
        Func<ExecApprovalRequest, Task<bool>>? execApproval = null,
        CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();
        await foreach (var chunk in GetStreamingResponseAsync(history, mcpServer, citations, skillInstructions, execApproval, cancellationToken)
            .WithCancellation(cancellationToken))
        {
            sb.Append(chunk);
        }

        return sb.Length == 0 ? "(no response)" : sb.ToString();
    }

    [Description("Search the user's captured screen history (screenshots and the on-screen text Floaty " +
                 "saved from them) by meaning. Use whenever the user refers to something they previously " +
                 "saw, viewed, read, or captured on their screen.")]
    private async Task<string> SearchCaptures(
        [Description("What to look for, described in natural language.")] string query,
        [Description("Maximum number of captures to return (default 5).")] int topK = 5)
    {
        var results = await _memory.SearchCapturesAsync(query, topK);
        if (results.Count == 0)
            return "No matching captures found.";

        RecordCitations(results);

        var sb = new StringBuilder();
        sb.AppendLine($"Found {results.Count} capture(s):");

        var index = 1;
        foreach (var r in results)
        {
            var when = r.CapturedUtc is { } utc ? utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "unknown time";
            var score = r.Score is { } s ? $", score {s:F2}" : string.Empty;
            sb.AppendLine();
            sb.AppendLine($"[{index}] {r.Title} ({when}{score})");
            sb.AppendLine(Snippet(r.Content, 600));
            if (!string.IsNullOrWhiteSpace(r.ImagePath))
                sb.AppendLine($"image: {r.ImagePath}");
            index++;
        }

        return sb.ToString();
    }

    [Description("Save a durable fact or note to the user's local memory so it can be recalled later. " +
                 "Use when the user asks you to remember something.")]
    private async Task<string> SaveMemory(
        [Description("The text to store in memory.")] string content)
    {
        var saved = await _memory.RememberTextAsync(content);
        return saved ? "Saved to memory." : "Could not save to memory (no API key configured).";
    }

    [Description("Run a shell command on the user's computer and return its output. Use to create, read, or " +
                 "edit files, run programs, inspect the system, or automate tasks. The user must approve " +
                 "every command before it executes; if they decline, the command does not run.")]
    private async Task<string> Exec(
        [Description("The command to run, exactly as it would be typed into the configured shell.")] string command,
        [Description("Optional working directory for the command; defaults to the user's home folder.")] string? workingDirectory = null)
    {
        var config = _settings.Current;
        if (!config.ExecEnabled)
            return "Shell command execution is disabled. The user can enable it in Settings → Shell.";

        if (string.IsNullOrWhiteSpace(command))
            return "No command was provided.";

        // Refuse rather than run un-approved: the sink is only set on interactive turns that wired an
        // approval channel, so background callers (e.g. summarization) can never trigger execution.
        var approve = _execApprovalSink.Value;
        if (approve is null)
            return "Cannot run a command: no approval channel is available in this context.";

        var shellName = ShellExecutor.ShellDisplayName(config);
        var approved = await approve(new ExecApprovalRequest(command, shellName, workingDirectory));
        if (!approved)
            return "The user declined to run this command.";

        return await ShellExecutor.RunAsync(config, command, workingDirectory, TimeSpan.FromSeconds(60));
    }

    // Records file-backed search hits into the current turn's citation sink (deduped by file path).
    private static void RecordCitations(IReadOnlyList<CaptureSearchResult> results)
    {
        var sink = _citationSink.Value;
        if (sink is null)
            return;

        foreach (var r in results)
        {
            if (string.IsNullOrWhiteSpace(r.ImagePath) && string.IsNullOrWhiteSpace(r.TextPath))
                continue; // notes have no openable source

            var key = r.ImagePath ?? r.TextPath;
            if (sink.Any(c => (c.ImagePath ?? c.TextPath) == key))
                continue;

            sink.Add(new MemoryCitation(r.Title, r.ImagePath, r.TextPath, r.CapturedUtc));
        }
    }

    private static string Snippet(string text, int max)
    {
        text = text.Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }

    private IChatClient GetOrCreateClient(FloatyConfig config)
    {
        if (_client is not null && _clientKey == config.OpenAiApiKey && _clientModel == config.Model)
            return _client;

        _clientKey = config.OpenAiApiKey;
        _clientModel = config.Model;
#pragma warning disable OPENAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        _client = new OpenAIClient(config.OpenAiApiKey)
            .GetResponsesClient()
            .AsIChatClient(config.Model)
            .AsBuilder()
            .UseFunctionInvocation()
            .Build();
#pragma warning restore OPENAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        return _client;
    }
}
