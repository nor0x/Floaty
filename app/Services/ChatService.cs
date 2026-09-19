using System.ComponentModel;
using System.Globalization;
using System.Text;
using Anthropic.Models.Messages;
using Floaty.IconFont;
using Floaty.Services.Tools;
using Microsoft.Extensions.AI;
using OpenAI.Responses;

namespace Floaty.Services;

/// <summary>
/// Sends a conversation to the configured LLM and returns the assistant's reply text.
/// </summary>
public interface IChatService
{
    IAsyncEnumerable<ChatChunk> GetStreamingResponseAsync(
        IReadOnlyList<ChatMessage> history,
        string? mcpServer = null,
        ICollection<MemoryCitation>? citations = null,
        string? skillInstructions = null,
        Func<ToolApprovalRequest, Task<bool>>? toolApproval = null,
        ICollection<GeneratedImageFile>? generatedImages = null,
        CancellationToken cancellationToken = default);

    Task<string> GetResponseAsync(
        IReadOnlyList<ChatMessage> history,
        string? mcpServer = null,
        ICollection<MemoryCitation>? citations = null,
        string? skillInstructions = null,
        Func<ToolApprovalRequest, Task<bool>>? toolApproval = null,
        ICollection<GeneratedImageFile>? generatedImages = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One piece of a streaming reply: either a slice of the answer or a slice of the model's reasoning.
/// </summary>
/// <remarks>
/// The stream carries both channels rather than two enumerables because their interleaving is the
/// signal the UI acts on: reasoning collapses the moment the first answer chunk arrives.
/// </remarks>
public readonly record struct ChatChunk(string Text, bool IsReasoning);

/// <summary>
/// Microsoft.Extensions.AI-backed chat service. Gets its <see cref="IChatClient"/> from
/// <see cref="AiClientFactory"/>, which decides — from the chat role in settings — which provider
/// answers. Exposes a <c>search_captures</c> tool plus, when scoped via a <c>/server</c> slash
/// command, that MCP server's tools.
/// </summary>
public sealed class ChatService : IChatService
{
    private const string DefaultSystemPrompt = SettingsService.DefaultSystemPrompt;

    // Characters of a capture shown per search hit. Matches TextChunker.ChunkChars so a chunk lands
    // in the result whole rather than being cut in half a second time.
    private const int SnippetChars = 1000;

    // Characters returned per read_capture call. Generous, because the model only asks for this once
    // it has decided a specific capture is worth reading in full.
    private const int ReadCaptureChars = 6000;

    // Anthropic rejects an extended-thinking budget below this.
    private const int MinThinkingBudgetTokens = 1024;

    // Output tokens reserved for the answer on top of the thinking budget, since the two share max_tokens.
    private const int AnswerTokenHeadroom = 4096;

    // Per-turn sink the search_captures tool writes its sources into; flows via the async call chain
    // from GetStreamingResponseAsync into the function-invocation middleware.
    private static readonly AsyncLocal<ICollection<MemoryCitation>?> _citationSink = new();

    // Per-turn sink the image tools record what they produced into, so the UI can show the picture
    // rather than the model having to describe it back. Same mechanism as the citation sink.
    private static readonly AsyncLocal<ICollection<GeneratedImageFile>?> _imageSink = new();

    private readonly SettingsService _settings;
    private readonly AiClientFactory _clients;
    private readonly IMemoryService _memory;
    private readonly IMcpService _mcp;
    private readonly IImageGenerationService _images;
    private readonly IAppAssets _appAssets;
    private readonly INotificationService _notifications;
    private readonly IReadOnlyList<IChatToolset> _toolsets;
    private readonly AIFunction _searchTool;
    private readonly AIFunction _readCaptureTool;
    private readonly AIFunction _saveTool;
    private readonly AIFunction _execTool;
    private readonly AIFunction _generateImageTool;
    private readonly AIFunction _editImageTool;
    private readonly AIFunction _notifyTool;
    private readonly AIFunction _listNotificationsTool;
    private readonly AIFunction _cancelNotificationTool;

    public ChatService(
        SettingsService settings,
        AiClientFactory clients,
        IMemoryService memory,
        IMcpService mcp,
        IImageGenerationService images,
        IAppAssets appAssets,
        INotificationService notifications,
        IEnumerable<IChatToolset> toolsets)
    {
        _settings = settings;
        _clients = clients;
        _memory = memory;
        _mcp = mcp;
        _images = images;
        _appAssets = appAssets;
        _notifications = notifications;
        _toolsets = toolsets.ToList();

        _searchTool = AIFunctionFactory.Create(SearchCaptures, name: "search_captures");
        _readCaptureTool = AIFunctionFactory.Create(ReadCapture, name: "read_capture");
        _saveTool = AIFunctionFactory.Create(SaveMemory, name: "save_memory");
        _execTool = AIFunctionFactory.Create(Exec, name: "exec");
        _generateImageTool = AIFunctionFactory.Create(GenerateImage, name: "generate_image");
        _editImageTool = AIFunctionFactory.Create(EditImage, name: "edit_image");
        _notifyTool = AIFunctionFactory.Create(Notify, name: "notify");
        _listNotificationsTool = AIFunctionFactory.Create(ListNotifications, name: "list_notifications");
        _cancelNotificationTool = AIFunctionFactory.Create(CancelNotification, name: "cancel_notification");
    }

    public async IAsyncEnumerable<ChatChunk> GetStreamingResponseAsync(
        IReadOnlyList<ChatMessage> history,
        string? mcpServer = null,
        ICollection<MemoryCitation>? citations = null,
        string? skillInstructions = null,
        Func<ToolApprovalRequest, Task<bool>>? toolApproval = null,
        ICollection<GeneratedImageFile>? generatedImages = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var config = _settings.Current;

        var client = _clients.GetChatClient();
        if (client is null)
        {
            yield return new ChatChunk("Set up a model provider in Settings (⚙) to start chatting.", IsReasoning: false);
            yield break;
        }

        // Expose the sink so the search_captures tool can record which sources it returned this turn.
        _citationSink.Value = citations;

        // Expose the approval callback so exec and the other gated tools can ask the user first.
        ToolApproval.Current = toolApproval;

        // Expose the sink the image tools hand their results to, for the UI to render.
        _imageSink.Value = generatedImages;

        var messages = new List<ChatMessage> { new(ChatRole.System, _settings.GetSystemPrompt(DefaultSystemPrompt)) };

        // The model has no other way to know what time it is, and reminders, "what's new since…" and
        // system_info all need it. Stated in the user's own time zone, the frame they ask in.
        var now = DateTimeOffset.Now;
        messages.Add(new ChatMessage(ChatRole.System,
            $"The current local date and time is {now:dddd, d MMMM yyyy, HH:mm} ({TimeZoneInfo.Local.DisplayName})."));

        // When invoked via /skill, inject that skill's instructions as additional system guidance.
        if (!string.IsNullOrWhiteSpace(skillInstructions))
            messages.Add(new ChatMessage(ChatRole.System,
                $"You are using a Floaty skill. Follow its instructions:\n\n{skillInstructions}"));

        // Always expose memory search + read + save; add the scoped MCP server's tools via /server.
        var tools = new List<AITool> { _searchTool, _readCaptureTool, _saveTool };

        // Only expose the shell tool when the user has opted in.
        if (config.ExecEnabled)
        {
            tools.Add(_execTool);
            var requiresApproval = config.ExecApprovalMode == ExecApprovalMode.AlwaysRequire;
            messages.Add(new ChatMessage(ChatRole.System, requiresApproval
                    ? "You can run shell commands on the user's computer with the exec tool — use it to create, " +
                        "read, or edit files, run programs, inspect the system, or automate tasks. Call exec directly " +
                        "when execution is needed; do not ask the user to type an approval keyword. The UI handles " +
                        "approval before execution. Prefer the smallest, safest command that accomplishes the goal " +
                        "and briefly explain anything destructive before running it."
                    : "You can run shell commands on the user's computer with the exec tool — use it to create, " +
                        "read, or edit files, run programs, inspect the system, or automate tasks. Call exec directly " +
                        "when execution is needed; do not ask the user to type an approval keyword. Prefer the " +
                        "smallest, safest command that accomplishes the goal and briefly explain anything destructive " +
                        "before running it."));
        }

        // Only expose the image tools when a provider is actually bound to the image role — an
        // unassigned role reads as "feature off" everywhere else too.
        if (_clients.IsConfigured(ModelRole.Image))
        {
            tools.Add(_generateImageTool);
            tools.Add(_editImageTool);
            messages.Add(new ChatMessage(ChatRole.System,
                "You can create pictures with the generate_image tool and restyle existing ones with " +
                "edit_image. Call them when the user asks for a picture, drawing, illustration, logo, " +
                "icon, avatar or wallpaper; expand a terse request into a detailed visual prompt yourself " +
                "rather than interrogating the user first. Generated images are shown to the user " +
                "automatically — never paste base64, and don't repeat the file name back unless asked. " +
                "To restyle Floaty's floating ring overlay, make a square image with a transparent " +
                "background and pass its file name to set_ring_image."));
        }

        // Notifications are always on where the platform can deliver them: they are additive and
        // non-destructive, unlike exec, so they need no opt-in. Deliberately no config flag either —
        // with no Settings section it would be an invisible switch, and it would drag in the
        // SettingsViewModel.Initialize hand-copy trap for no user benefit. A platform that cannot
        // deliver simply never offers the tools, rather than promising reminders that never arrive.
        if (_notifications.IsSupported)
        {
            tools.Add(_notifyTool);
            tools.Add(_listNotificationsTool);
            tools.Add(_cancelNotificationTool);

            messages.Add(new ChatMessage(ChatRole.System,
                "You can raise notifications on the user's desktop with the notify tool — use it to remind them of something, set an alarm or " +
                "timer, or flag that a long task finished. Leave both time arguments off to notify " +
                "immediately; pass in_seconds for anything relative (\"in 20 minutes\" is 1200) and " +
                "at_local_time only for an explicit clock time, computed from the current time above. " +
                "Prefer in_seconds when either would work. Scheduled notifications are handed to the " +
                "operating system, so they still fire when Floaty is closed. Use list_notifications " +
                "to see what is pending and cancel_notification with an id from that list to remove " +
                "one. Tell the user the time you actually scheduled, so a mistake is visible."));
        }

        foreach (var toolset in _toolsets)
        {
            if (!toolset.IsAvailable)
                continue;

            tools.AddRange(toolset.Tools);
            if (!string.IsNullOrWhiteSpace(toolset.Guidance))
                messages.Add(new ChatMessage(ChatRole.System, toolset.Guidance));
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
        ApplyReasoning(options, _clients.GetProfile(ModelRole.Chat));

        // Reasoning reaches us one of two ways: as TextReasoningContent (Anthropic's thinking blocks,
        // and the reasoning_content field the OpenAI binding already parses out of DeepSeek/Ollama/
        // LM Studio streams), or inlined as <think> tags in the ordinary text. Never both - a provider
        // that reports it properly must not also get tag-scraped, or a code block about <think> would
        // be swallowed on a model that never inlines anything.
        // Null once the provider has proven it reports reasoning properly, and from then on text passes
        // through untouched. Retired by flushing in place, so anything it was holding keeps its position
        // in the stream rather than resurfacing at the end.
        ThinkTagSplitter? splitter = new();

        await foreach (var update in client.GetStreamingResponseAsync(messages, options, cancellationToken)
            .WithCancellation(cancellationToken))
        {
            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    // Not covered by update.Text: that concatenates TextContent only, and
                    // TextReasoningContent deliberately does not derive from it.
                    case TextReasoningContent { Text.Length: > 0 } reasoning:
                        if (splitter is not null)
                        {
                            foreach (var chunk in splitter.Flush())
                                yield return chunk;
                            splitter = null;
                        }

                        yield return new ChatChunk(reasoning.Text, IsReasoning: true);
                        break;

                    case TextContent { Text.Length: > 0 } text:
                        if (splitter is null)
                        {
                            yield return new ChatChunk(text.Text, IsReasoning: false);
                            break;
                        }

                        foreach (var chunk in splitter.Feed(text.Text))
                            yield return chunk;
                        break;
                }
            }
        }

        // Whatever the splitter was still holding back in case it grew into a tag.
        if (splitter is not null)
        {
            foreach (var chunk in splitter.Flush())
                yield return chunk;
        }
    }

    /// <summary>
    /// Applies the provider's reasoning settings: showing the reasoning (only the providers that need
    /// it asked for - every OpenAI-compatible endpoint that reasons at all streams
    /// <c>reasoning_content</c> unasked), how hard to think, and, on OpenAI, how long to answer.
    /// </summary>
    /// <remarks>
    /// Lives here rather than in <see cref="AiClientFactory"/> because the factory also builds the vision
    /// client used for screenshot captioning, which has no business thinking.
    /// </remarks>
    private static void ApplyReasoning(ChatOptions options, ProviderProfile? profile)
    {
        if (profile is null)
            return;

        var showThinking = profile.RequestThinking;
        var effort = profile.ReasoningEffort;
        var verbosity = profile.Kind == ProviderKind.OpenAI ? VerbosityValue(profile.Verbosity) : null;

        if (!showThinking && effort == ReasoningEffortLevel.Default && verbosity is null)
            return;

        switch (profile.Kind)
        {
            case ProviderKind.Anthropic:
            {
                var anthropicEffort = AnthropicEffort(effort);
                if (!showThinking && anthropicEffort is null)
                    return;

                var budget = Math.Max(MinThinkingBudgetTokens, profile.ThinkingBudgetTokens);

                // The thinking budget is spent out of max_tokens, so the ceiling has to cover the
                // budget plus room for the answer itself - otherwise the reply is cut off mid-thought.
                var ceiling = showThinking ? budget + AnswerTokenHeadroom : AnswerTokenHeadroom;
                if (showThinking)
                    options.MaxOutputTokens = ceiling;

                // Model/Messages/MaxTokens are required members the adapter overwrites from the request
                // it is actually building; only Thinking and OutputConfig survive, which is the whole point
                // of the hook. They are still filled in with a real ceiling rather than junk, so this stays
                // correct even if a future SDK stops overwriting one of them.
                options.RawRepresentationFactory = _ =>
                {
                    var raw = new MessageCreateParams
                    {
                        Model = string.Empty,
                        Messages = [],
                        MaxTokens = ceiling,
                    };
                    if (showThinking)
                        raw = raw with { Thinking = new ThinkingConfigEnabled { BudgetTokens = budget } };
                    if (anthropicEffort is { } level)
                        raw = raw with { OutputConfig = new OutputConfig { Effort = level } };
                    return raw;
                };
                break;
            }

            case ProviderKind.OpenAI when profile.UseResponsesApi:
            {
                var openAiEffort = OpenAiEffort(effort);
#pragma warning disable OPENAI001 // Same experimental Responses API surface AiClientFactory opts into.
#pragma warning disable SCME0001 // JsonPatch: the SDK has no typed property for text.verbosity yet.
                options.RawRepresentationFactory = _ =>
                {
                    var raw = new CreateResponseOptions();
                    if (showThinking || openAiEffort is not null)
                    {
                        raw.ReasoningOptions = new ResponseReasoningOptions();
                        if (openAiEffort is not null)
                            raw.ReasoningOptions.ReasoningEffortLevel = new ResponseReasoningEffortLevel(openAiEffort);

                        // o-series and gpt-5 return no visible reasoning at all without a summary asked for.
                        if (showThinking)
                            raw.ReasoningOptions.ReasoningSummaryVerbosity = ResponseReasoningSummaryVerbosity.Auto;
                    }

                    if (verbosity is not null)
                        raw.Patch.Set("$.text.verbosity"u8, verbosity);
                    return raw;
                };
#pragma warning restore SCME0001
#pragma warning restore OPENAI001
                break;
            }

            case ProviderKind.OpenAI or ProviderKind.AzureOpenAI or ProviderKind.OpenAiCompatible:
            {
                // Chat completions: reasoning shows up unasked, so only effort and verbosity go on the wire.
                var openAiEffort = OpenAiEffort(effort);
                if (openAiEffort is null && verbosity is null)
                    return;

#pragma warning disable OPENAI001 // ReasoningEffortLevel is still marked experimental on chat completions.
#pragma warning disable SCME0001 // JsonPatch: the SDK has no typed property for verbosity yet.
                options.RawRepresentationFactory = _ =>
                {
                    var raw = new OpenAI.Chat.ChatCompletionOptions();
                    if (openAiEffort is not null)
                        raw.ReasoningEffortLevel = new OpenAI.Chat.ChatReasoningEffortLevel(openAiEffort);
                    if (verbosity is not null)
                        raw.Patch.Set("$.verbosity"u8, verbosity);
                    return raw;
                };
#pragma warning restore SCME0001
#pragma warning restore OPENAI001
                break;
            }
        }
    }

    /// <summary>OpenAI's wire value for an effort level, or null to leave it to the provider.</summary>
    private static string? OpenAiEffort(ReasoningEffortLevel level) => level switch
    {
        ReasoningEffortLevel.None => "none",
        ReasoningEffortLevel.Minimal => "minimal",
        ReasoningEffortLevel.Low => "low",
        ReasoningEffortLevel.Medium => "medium",
        ReasoningEffortLevel.High => "high",
        ReasoningEffortLevel.Maximum => "xhigh",
        _ => null,
    };

    /// <summary>Anthropic's effort for a level; it has nothing below low, so the lower rungs collapse onto it.</summary>
    private static Effort? AnthropicEffort(ReasoningEffortLevel level) => level switch
    {
        ReasoningEffortLevel.None or ReasoningEffortLevel.Minimal or ReasoningEffortLevel.Low => Effort.Low,
        ReasoningEffortLevel.Medium => Effort.Medium,
        ReasoningEffortLevel.High => Effort.High,
        ReasoningEffortLevel.Maximum => Effort.Max,
        _ => null,
    };

    private static string? VerbosityValue(OutputVerbosity verbosity) => verbosity switch
    {
        OutputVerbosity.Low => "low",
        OutputVerbosity.Medium => "medium",
        OutputVerbosity.High => "high",
        _ => null,
    };

    public async Task<string> GetResponseAsync(
        IReadOnlyList<ChatMessage> history,
        string? mcpServer = null,
        ICollection<MemoryCitation>? citations = null,
        string? skillInstructions = null,
        Func<ToolApprovalRequest, Task<bool>>? toolApproval = null,
        ICollection<GeneratedImageFile>? generatedImages = null,
        CancellationToken cancellationToken = default)
    {
        var sb = new StringBuilder();
        await foreach (var chunk in GetStreamingResponseAsync(
            history, mcpServer, citations, skillInstructions, toolApproval, generatedImages, cancellationToken)
            .WithCancellation(cancellationToken))
        {
            // Reasoning is scaffolding, not the answer; a caller that wanted the whole reply as one
            // string wants what the model actually said.
            if (!chunk.IsReasoning)
                sb.Append(chunk.Text);
        }

        return sb.Length == 0 ? "(no response)" : sb.ToString();
    }

    [Description("Search the user's captured screen history (screenshots and the on-screen text Floaty " +
                 "saved from them) by meaning. Use whenever the user refers to something they previously " +
                 "saw, viewed, read, or captured on their screen.")]
    private async Task<string> SearchCaptures(
        [Description("What to look for, described in natural language.")] string query,
        [Description("Maximum number of captures to return, 1-10 (default 5).")] int topK = 5)
    {
        // Clamp: the model picks this, and every extra hit costs ~1000 characters of context.
        var results = await _memory.SearchCapturesAsync(query, Math.Clamp(topK, 1, 10));
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
            sb.AppendLine(TextChunker.BestWindow(r.Content, query, SnippetChars));

            // Name the saved text file so read_capture can pull the rest when the snippet isn't enough.
            if (!string.IsNullOrWhiteSpace(r.TextPath))
                sb.AppendLine($"file: {Path.GetFileName(r.TextPath)}");
            // The page the user was actually on. Worth opening over trusting the flattened capture text.
            if (!string.IsNullOrWhiteSpace(r.Url))
                sb.AppendLine($"url: {r.Url}");
            if (!string.IsNullOrWhiteSpace(r.ImagePath))
                sb.AppendLine($"image: {r.ImagePath}");
            index++;
        }

        return sb.ToString();
    }

    [Description("Read the full saved text of one capture, for when a search_captures snippet is " +
                 "cut off or lacks the detail needed to answer. Pass the 'file:' value from a " +
                 "search_captures result. Read further into a long capture by raising 'offset'. " +
                 "Screen history is also written as one markdown file per day, named 'YYYY-MM-DD.md' " +
                 "(e.g. '2026-08-25.md') - pass that to read a whole day's activity as a timeline.")]
    private Task<string> ReadCapture(
        [Description("The capture's file name, exactly as given by search_captures.")] string file,
        [Description("Character offset to start reading from (default 0).")] int offset = 0)
    {
        if (string.IsNullOrWhiteSpace(file))
            return Task.FromResult("No capture file was specified.");

        // GetFileName strips any directory the model may have prepended, so this can only ever open
        // something inside Floaty's own capture folders — never an arbitrary path on the machine.
        var name = Path.GetFileName(file.Trim());
        var path = new[] { FloatyPaths.Captures, FloatyPaths.Drops }
            .Select(dir => Path.Combine(dir, name))
            .FirstOrDefault(File.Exists);

        if (path is null)
            return Task.FromResult($"No saved capture named '{name}'. Use the 'file:' value from a search_captures result.");

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            return Task.FromResult($"Could not read '{name}': {ex.Message}");
        }

        offset = Math.Clamp(offset, 0, Math.Max(0, text.Length - 1));
        var window = text.Substring(offset, Math.Min(ReadCaptureChars, text.Length - offset));
        var end = offset + window.Length;

        var header = $"{name} — characters {offset}-{end} of {text.Length}";
        return Task.FromResult(end < text.Length
            ? $"{header}\n\n{window}\n\n[truncated — call read_capture again with offset {end} for more]"
            : $"{header}\n\n{window}");
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
                 "edit files, run programs, inspect the system, or automate tasks. Depending on settings, " +
                 "commands may require user approval before execution.")]
    private async Task<string> Exec(
        [Description("The command to run, exactly as it would be typed into the configured shell.")] string command,
        [Description("Optional working directory for the command; defaults to the user's home folder.")] string? workingDirectory = null)
    {
        var config = _settings.Current;
        if (!config.ExecEnabled)
            return "Shell command execution is disabled. The user can enable it in Settings → Shell.";

        if (string.IsNullOrWhiteSpace(command))
            return "No command was provided.";

        if (config.ExecApprovalMode == ExecApprovalMode.NeverRequire)
            return await ShellExecutor.RunAsync(config, command, workingDirectory, TimeSpan.FromSeconds(60));

        // Refuse rather than run un-approved when approval mode requires a prompt and no callback is wired.
        var shellName = ShellExecutor.ShellDisplayName(config);
        var approved = await ToolApproval.RequestAsync(new ToolApprovalRequest(
            Header: $"Run this command in {shellName}?",
            Detail: command,
            SubDetail: string.IsNullOrWhiteSpace(workingDirectory) ? null : $"in {workingDirectory}",
            ConfirmLabel: "Run",
            ApprovedNote: $"⚡ Ran in {shellName}: {command}",
            DeclinedNote: $"🚫 Declined: {command}",
            Icon: TablerLine.Terminal2));
        if (approved is null)
            return "Cannot run a command: no approval channel is available in this context.";
        if (approved == false)
            return "The user declined to run this command.";

        return await ShellExecutor.RunAsync(config, command, workingDirectory, TimeSpan.FromSeconds(60));
    }

    [Description("Generate an image from a text description and show it to the user in the chat. Use when " +
                 "the user asks for a picture, drawing, illustration, logo, icon, avatar, wallpaper or " +
                 "concept art. Write a detailed visual prompt: subject, setting, art style, composition, " +
                 "lighting and colors.")]
    private async Task<string> GenerateImage(
        [Description("A detailed description of the image to create.")] string prompt,
        [Description("Optional size, e.g. '1024x1024' (square), '1024x1536' (portrait) or '1536x1024' (landscape).")] string? size = null,
        [Description("True for a transparent background, for logos, icons, stickers and the ring. Only gpt-image models honor it.")] bool transparent = false)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return "No prompt was provided.";

        try
        {
            var file = await _images.GenerateAsync(prompt, size, transparent);
            _imageSink.Value?.Add(file);
            return $"Image generated and saved as '{file.FileName}'. It is already displayed to the user. " +
                   "Pass this file name to set_ring_image to make it Floaty's ring.";
        }
        catch (Exception ex)
        {
            // Returned rather than thrown, like exec and save_memory: a provider failure should become a
            // sentence the model can relay, not a dead turn.
            return $"Image generation failed: {ex.Message}";
        }
    }

    [Description("Restyle an existing image according to a description, and show the result to the user. " +
                 "Pass a file name from generate_image, a screen capture, a ring image (ring1.png … " +
                 "ring7.png), or a file the user dropped on Floaty.")]
    private async Task<string> EditImage(
        [Description("The image file name to start from.")] string file,
        [Description("What to change, described as the finished image should look.")] string prompt,
        [Description("Optional output size, e.g. '1024x1024'.")] string? size = null,
        [Description("True for a transparent background. Only gpt-image models honor it.")] bool transparent = false)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return "No prompt was provided.";

        // GetFileName strips any directory the model may have prepended, so an edit can only ever read
        // from Floaty's own folders — never an arbitrary path on the machine.
        var name = Path.GetFileName((file ?? string.Empty).Trim());
        if (string.IsNullOrWhiteSpace(name))
            return "No image file was specified.";

        // A built-in ring ships inside the app, so it has to be unpacked before it can be sent.
        string? temporary = null;
        var source = new[] { FloatyPaths.GeneratedImages, FloatyPaths.Captures, FloatyPaths.Drops, FloatyPaths.RingImages }
            .Select(dir => Path.Combine(dir, name))
            .FirstOrDefault(File.Exists);

        if (source is null && _settings.IsBuiltInRingImage(name))
        {
            try
            {
                using var packaged = _appAssets.Open("Resources/Images", name);
                if (packaged is not null)
                {
                    temporary = Path.Combine(Path.GetTempPath(), $"floaty-{Guid.NewGuid():N}{Path.GetExtension(name)}");
                    await using var target = File.Create(temporary);
                    await packaged.CopyToAsync(target);
                    source = temporary;
                }
            }
            catch
            {
                temporary = null;
            }
        }

        if (source is null)
            return $"No image named '{name}'. Use a file name returned by generate_image, or a built-in ring (ring1.png … ring7.png).";

        try
        {
            var edited = await _images.EditAsync(source, prompt, size, transparent);
            _imageSink.Value?.Add(edited);
            return $"Edited image saved as '{edited.FileName}'. It is already displayed to the user.";
        }
        catch (Exception ex)
        {
            return $"Image editing failed: {ex.Message}";
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); } catch { /* a leftover temp file is not worth failing the turn */ }
            }
        }
    }

    [Description("Show a notification on the user's desktop, now or at a future time. Use it to remind " +
                 "the user of something, set an alarm or a timer, or tell them a long-running task " +
                 "finished. With no time argument it appears immediately. Scheduled notifications are " +
                 "handed to the operating system and still fire when Floaty is closed.")]
    private Task<string> Notify(
        [Description("Short headline, shown in bold on the notification.")] string title,
        [Description("The message body, one or two sentences.")] string body,
        [Description("Delay before showing it, in seconds. Use this for anything relative: " +
                     "'in 20 minutes' is 1200. Leave at 0 to show it right away.")] int in_seconds = 0,
        [Description("Absolute local time instead of a delay, as ISO-8601 without a zone offset, e.g. " +
                     "'2026-09-12T15:00'. A bare time like '15:00' means today, or tomorrow if that " +
                     "time has already passed. Compute it from the current time given in the system " +
                     "message; prefer in_seconds when either would work.")] string? at_local_time = null)
    {
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body))
            return Task.FromResult("A notification needs at least a title or a body.");

        // in_seconds wins: it is the argument models get right, because it needs no knowledge of the
        // current time. at_local_time is only consulted when no delay was given.
        DateTimeOffset? when = null;
        if (in_seconds > 0)
        {
            when = DateTimeOffset.Now.AddSeconds(in_seconds);
        }
        else if (!string.IsNullOrWhiteSpace(at_local_time))
        {
            if (!TryParseLocalTime(at_local_time, out var parsed))
                return Task.FromResult($"Could not read '{at_local_time}' as a time. Use a delay in " +
                                       "seconds, or an ISO-8601 local time like '2026-09-12T15:00'.");
            when = parsed;
        }

        var result = when is null
            ? _notifications.Show(title, body)
            : _notifications.Schedule(title, body, when.Value);

        if (!result.Ok)
            return Task.FromResult(result.Error ?? "The notification could not be shown.");

        // No ISoundService.Play here on purpose: the toast carries the OS notification sound, and
        // layering Floaty's own chime on top would double up.
        if (result.Id is null)
            return Task.FromResult("Notification shown.");

        // Echo both the absolute time and the offset: the absolute time is what the user will check,
        // the offset is what makes the model's own arithmetic error visible if it made one.
        var delivery = when!.Value;
        return Task.FromResult(
            $"Scheduled for {delivery:dddd d MMMM, HH:mm} ({Describe(delivery - DateTimeOffset.Now)} " +
            $"from now). Id: {result.Id} — pass it to cancel_notification to call it off.");
    }

    [Description("List the desktop notifications still scheduled for a future time, with the id needed " +
                 "to cancel each one.")]
    private Task<string> ListNotifications()
    {
        var pending = _notifications.ListScheduled();
        if (pending.Count == 0)
            return Task.FromResult("No notifications are scheduled.");

        var sb = new StringBuilder();
        sb.Append(pending.Count)
          .Append(pending.Count == 1 ? " scheduled notification:" : " scheduled notifications:");

        foreach (var n in pending)
        {
            sb.AppendLine().AppendLine()
              .Append("id: ").Append(n.Id).AppendLine()
              .Append("when: ").Append(n.DeliveryTime.ToLocalTime().ToString("dddd d MMMM, HH:mm"))
              .Append(" (in ").Append(Describe(n.DeliveryTime - DateTimeOffset.Now)).Append(')').AppendLine()
              .Append("title: ").Append(string.IsNullOrWhiteSpace(n.Title) ? "(none)" : n.Title);

            if (!string.IsNullOrWhiteSpace(n.Body))
                sb.AppendLine().Append("body: ").Append(n.Body);
        }

        return Task.FromResult(sb.ToString());
    }

    [Description("Cancel a scheduled desktop notification. Pass the id from notify or " +
                 "list_notifications. A notification that has already fired cannot be cancelled.")]
    private Task<string> CancelNotification(
        [Description("The notification's id, exactly as given by notify or list_notifications.")] string id)
    {
        var result = _notifications.Cancel(id);
        return Task.FromResult(result.Ok
            ? $"Cancelled notification {result.Id}."
            : $"{result.Error} Call list_notifications to see what is still pending — it may have " +
              "already fired or been cancelled.");
    }

    /// <summary>
    /// Reads the model's absolute-time argument. Deliberately forgiving: a bare "15:00" is taken as
    /// today, and rolled to tomorrow when that moment has already passed, which is what a user who
    /// says "remind me at 3" at 4pm means. AssumeLocal because the model is told the time in local
    /// terms, and the invariant culture is tried first so an ISO-8601 string parses the same way
    /// whatever the machine's locale is.
    /// </summary>
    private static bool TryParseLocalTime(string text, out DateTimeOffset value)
    {
        value = default;
        var trimmed = text.Trim();
        const DateTimeStyles styles = DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces;

        if (!DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, styles, out var parsed)
            && !DateTime.TryParse(trimmed, CultureInfo.CurrentCulture, styles, out parsed))
            return false;

        var local = new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Local));

        // A time-only value that has already gone by means the next one, not one in the past. A value
        // carrying a date is taken at face value, so "remind me yesterday" still fails loudly.
        if (local <= DateTimeOffset.Now && !trimmed.Contains('-') && !trimmed.Contains('/'))
            local = local.AddDays(1);

        value = local;
        return true;
    }

    /// <summary>
    /// "2 hours 5 minutes" — the model repeats this back to the user, so it reads as prose rather than
    /// as a TimeSpan.
    /// </summary>
    private static string Describe(TimeSpan span)
    {
        if (span < TimeSpan.FromMinutes(1))
            return "less than a minute";

        var parts = new List<string>();
        if (span.Days > 0)
            parts.Add($"{span.Days} day{(span.Days == 1 ? "" : "s")}");
        if (span.Hours > 0)
            parts.Add($"{span.Hours} hour{(span.Hours == 1 ? "" : "s")}");

        // Minutes are noise once we are days out, and the reader only ever needs two units.
        if (span.Minutes > 0 && parts.Count < 2)
            parts.Add($"{span.Minutes} minute{(span.Minutes == 1 ? "" : "s")}");

        return string.Join(' ', parts);
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
}
