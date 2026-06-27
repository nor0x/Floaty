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
        CancellationToken cancellationToken = default);

    Task<string> GetResponseAsync(
        IReadOnlyList<ChatMessage> history,
        string? mcpServer = null,
        CancellationToken cancellationToken = default);
}

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
        "what's on their screen; each capture's on-screen text is stored in local memory. When the user " +
        "asks about something they previously saw, viewed, read, or captured, call the search_captures " +
        "tool to retrieve it before answering, and ground your answer in what it returns. Be concise.";

    private readonly SettingsService _settings;
    private readonly IMemoryService _memory;
    private readonly IMcpService _mcp;
    private readonly AIFunction _searchTool;

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
    }

    public async IAsyncEnumerable<string> GetStreamingResponseAsync(
        IReadOnlyList<ChatMessage> history,
        string? mcpServer = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var config = _settings.Current;

        if (string.IsNullOrWhiteSpace(config.OpenAiApiKey))
        {
            yield return "Add your OpenAI API key in Settings (⚙) to start chatting.";
            yield break;
        }

        var client = GetOrCreateClient(config);

        var messages = new List<ChatMessage> { new(ChatRole.System, _settings.GetSystemPrompt(DefaultSystemPrompt)) };

        // Always expose capture search; add the scoped MCP server's tools when invoked via /server.
        var tools = new List<AITool> { _searchTool };
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
        CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();
        await foreach (var chunk in GetStreamingResponseAsync(history, mcpServer, cancellationToken)
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
