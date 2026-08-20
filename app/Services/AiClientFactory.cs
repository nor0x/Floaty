using System.ClientModel;
using Anthropic;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Floaty.Services;

/// <summary>One of the three jobs a configured provider can be assigned to.</summary>
public enum ModelRole
{
    /// <summary>Answers chats. Needs tool-calling support.</summary>
    Chat,

    /// <summary>Vectorizes captures for memory search.</summary>
    Embedding,

    /// <summary>Describes captured screenshots.</summary>
    Vision,
}

/// <summary>
/// The single place Floaty turns configuration into <c>Microsoft.Extensions.AI</c> clients. Every
/// consumer (<see cref="ChatService"/>, <see cref="MemoryService"/>) asks for a role and gets back
/// an <see cref="IChatClient"/> or <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> without
/// knowing which provider is behind it.
///
/// Clients are cached per role and dropped wholesale when settings change, mirroring what each
/// consumer used to do for itself.
/// </summary>
public sealed class AiClientFactory : IDisposable
{
    /// <summary>Fallback for an Anthropic profile whose endpoint (and preset) left the base URL blank.</summary>
    private const string AnthropicBaseUrl = "https://api.anthropic.com";

    private readonly SettingsService _settings;
    private readonly ILocalEmbeddingFactory _localEmbeddings;
    private readonly Lock _gate = new();

    private IChatClient? _chat;
    private IChatClient? _vision;
    private IEmbeddingGenerator<string, Embedding<float>>? _embeddings;

    // Identity of the config each cached client was built from, so a settings save that didn't
    // touch a role doesn't needlessly tear that role's client down.
    private string? _chatKey;
    private string? _visionKey;
    private string? _embeddingsKey;

    public AiClientFactory(SettingsService settings, ILocalEmbeddingFactory localEmbeddings)
    {
        _settings = settings;
        _localEmbeddings = localEmbeddings;
        _settings.Changed += OnSettingsChanged;
    }

    /// <summary>
    /// Raised when a settings change invalidated the embedding client. <see cref="MemoryService"/>
    /// listens so it can notice a dimensionality change rather than writing mismatched vectors.
    /// </summary>
    public event EventHandler? EmbeddingProviderChanged;

    /// <summary>Whether a role points at a provider that is actually usable right now.</summary>
    public bool IsConfigured(ModelRole role) => Resolve(role) is not null;

    /// <summary>
    /// The model id a role currently resolves to, or null when it is unassigned. Stored alongside
    /// each vector so a later provider switch is visible in the data rather than silently mixing
    /// incompatible embeddings.
    /// </summary>
    public string? GetModelId(ModelRole role) => Resolve(role)?.Model;

    /// <summary>
    /// The chat client, with function invocation wired up, or null when the chat role is unassigned
    /// or its provider is missing a key. Callers surface that as "configure a provider in Settings".
    /// </summary>
    public IChatClient? GetChatClient()
    {
        var resolved = Resolve(ModelRole.Chat);
        if (resolved is null)
            return null;

        lock (_gate)
        {
            if (_chat is not null && _chatKey == resolved.CacheKey)
                return _chat;

            // Not disposed: a streaming turn may still be reading from the old client. Dropping
            // the reference lets it finish and be collected, which is what the per-service caches
            // this replaced did too.
            _chatKey = resolved.CacheKey;
            _chat = BuildChatClient(resolved)
                .AsBuilder()
                .UseFunctionInvocation()
                .Build();
            return _chat;
        }
    }

    /// <summary>
    /// The vision client used to describe screenshots, or null when captioning is switched off.
    /// No function invocation: it answers one self-contained "describe this image" turn.
    /// </summary>
    public IChatClient? GetVisionClient()
    {
        var resolved = Resolve(ModelRole.Vision);
        if (resolved is null)
            return null;

        lock (_gate)
        {
            if (_vision is not null && _visionKey == resolved.CacheKey)
                return _vision;

            _visionKey = resolved.CacheKey;
            _vision = BuildChatClient(resolved);
            return _vision;
        }
    }

    /// <summary>The embedding generator, or null when the embedding role is unassigned.</summary>
    public IEmbeddingGenerator<string, Embedding<float>>? GetEmbeddingGenerator()
    {
        var resolved = Resolve(ModelRole.Embedding);
        if (resolved is null)
            return null;

        lock (_gate)
        {
            if (_embeddings is not null && _embeddingsKey == resolved.CacheKey)
                return _embeddings;

            _embeddingsKey = resolved.CacheKey;
            _embeddings = BuildEmbeddingGenerator(resolved);
            return _embeddings;
        }
    }

    /// <summary>
    /// Sends the smallest possible request through <paramref name="profile"/> so the user can find
    /// out a key or endpoint is wrong from the Settings page rather than from a chat that silently
    /// fails. Returns null on success, otherwise a message worth showing.
    /// </summary>
    public async Task<string?> TestAsync(ProviderProfile profile, CancellationToken cancellationToken = default)
    {
        var preset = ProviderPresets.Find(profile.PresetId);
        if (preset is { NeedsKey: true } && string.IsNullOrWhiteSpace(profile.ApiKey))
            return "No API key.";

        try
        {
            if (profile.Kind == ProviderKind.LocalOnnx)
            {
                var model = profile.EmbeddingModel;
                if (string.IsNullOrWhiteSpace(model))
                    return "No model selected.";

                using var generator = _localEmbeddings.Create(model);
                if (generator is null)
                    return "Model is not downloaded.";

                await generator.GenerateAsync(["floaty"], cancellationToken: cancellationToken);
                return null;
            }

            if (!string.IsNullOrWhiteSpace(profile.ChatModel))
            {
                using var client = BuildChatClient(new ResolvedRole(profile, profile.ChatModel, string.Empty));
                var options = new ChatOptions { MaxOutputTokens = 1 };
                await client.GetResponseAsync("Hi", options, cancellationToken);
                return null;
            }

            if (!string.IsNullOrWhiteSpace(profile.EmbeddingModel))
            {
                using var generator = BuildEmbeddingGenerator(new ResolvedRole(profile, profile.EmbeddingModel, string.Empty));
                await generator.GenerateAsync(["floaty"], cancellationToken: cancellationToken);
                return null;
            }

            return "No model configured to test.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// The provider and model a role currently resolves to, or null when the role is unassigned,
    /// its provider vanished, no model is set, or a key is required but missing. Everything that
    /// asks "is this configured?" goes through here so the answer can't drift between callers.
    /// </summary>
    private ResolvedRole? Resolve(ModelRole role)
    {
        var config = _settings.Current;

        var assignment = role switch
        {
            ModelRole.Chat => config.ChatRole,
            ModelRole.Embedding => config.EmbeddingRole,
            _ => config.VisionRole,
        };

        if (!assignment.IsAssigned)
            return null;

        var profile = config.Providers.FirstOrDefault(p =>
            string.Equals(p.Id, assignment.ProviderId, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
            return null;

        // An empty model on the assignment falls back to the profile's default for that role, so
        // switching a role to a provider is one click rather than a click plus retyping a model id.
        var model = assignment.Model;
        if (string.IsNullOrWhiteSpace(model))
        {
            model = role switch
            {
                ModelRole.Chat => profile.ChatModel,
                ModelRole.Embedding => profile.EmbeddingModel,
                _ => profile.VisionModel,
            };
        }

        if (string.IsNullOrWhiteSpace(model))
            return null;

        var preset = ProviderPresets.Find(profile.PresetId);
        if (preset is { NeedsKey: true } && string.IsNullOrWhiteSpace(profile.ApiKey))
            return null;

        // A provider can only serve roles it actually has a transport for. Without this a
        // hand-edited config could bind chat to a local embedding model, and the failure would
        // surface as an exception mid-turn rather than as an unconfigured role.
        if (!CanServe(profile.Kind, role))
            return null;

        // A local model that isn't downloaded reads as unconfigured rather than resolving and then
        // throwing on every capture — screen history swallows its errors, so a silent permanent
        // failure would be invisible.
        if (profile.Kind == ProviderKind.LocalOnnx && !_localEmbeddings.IsAvailable(model))
            return null;

        var cacheKey = string.Join('|',
            profile.Id, profile.Kind, profile.ApiKey, ResolveBaseUrl(profile), model, profile.UseResponsesApi);

        return new ResolvedRole(profile, model, cacheKey);
    }

    /// <summary>
    /// Which roles a transport can serve at all: local ONNX models only embed, and Anthropic has no
    /// embedding API. Everything else is assumed capable — whether a specific model can see images
    /// is between the user and their provider.
    /// </summary>
    private static bool CanServe(ProviderKind kind, ModelRole role) => kind switch
    {
        ProviderKind.LocalOnnx => role == ModelRole.Embedding,
        ProviderKind.Anthropic => role != ModelRole.Embedding,
        _ => true,
    };

    /// <summary>The profile's endpoint, falling back to its preset's default when the user left it blank.</summary>
    private static string ResolveBaseUrl(ProviderProfile profile) =>
        string.IsNullOrWhiteSpace(profile.BaseUrl)
            ? ProviderPresets.Find(profile.PresetId)?.BaseUrl ?? string.Empty
            : profile.BaseUrl.Trim();

    private static IChatClient BuildChatClient(ResolvedRole resolved)
    {
        var profile = resolved.Profile;
        var model = resolved.Model;

        switch (profile.Kind)
        {
            case ProviderKind.Anthropic:
            {
                // BaseUrl is init-only, and the SDK requires it, so it is always set here rather
                // than conditionally: an empty override falls back to the preset's api.anthropic.com.
                var baseUrl = ResolveBaseUrl(profile);
                var client = new AnthropicClient
                {
                    ApiKey = profile.ApiKey,
                    BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? AnthropicBaseUrl : baseUrl,
                };
                return client.AsIChatClient(model);
            }

            case ProviderKind.LocalOnnx:
                throw new NotSupportedException("Local providers only serve embeddings.");

            case ProviderKind.OpenAI when profile.UseResponsesApi:
#pragma warning disable OPENAI001 // The Responses API binding is still marked experimental in the SDK.
                return OpenAiClientFor(profile).GetResponsesClient().AsIChatClient(model);
#pragma warning restore OPENAI001

            default:
                return OpenAiClientFor(profile).GetChatClient(model).AsIChatClient();
        }
    }

    private IEmbeddingGenerator<string, Embedding<float>> BuildEmbeddingGenerator(ResolvedRole resolved)
    {
        var profile = resolved.Profile;

        if (profile.Kind == ProviderKind.LocalOnnx)
        {
            return _localEmbeddings.Create(resolved.Model)
                ?? throw new InvalidOperationException(
                    $"The local embedding model '{resolved.Model}' is not downloaded.");
        }

        if (profile.Kind == ProviderKind.Anthropic)
            throw new NotSupportedException("Anthropic does not offer embedding models.");

        return OpenAiClientFor(profile).GetEmbeddingClient(resolved.Model).AsIEmbeddingGenerator();
    }

    /// <summary>
    /// One OpenAI-shaped client for every provider that speaks that wire format. Azure gets its own
    /// client type (a subclass, so everything downstream is identical); the rest differ only by the
    /// endpoint they point at, which is why a single case covers Gemini, Groq, Ollama, LM Studio…
    /// </summary>
    private static OpenAIClient OpenAiClientFor(ProviderProfile profile)
    {
        var baseUrl = ResolveBaseUrl(profile);

        // Providers that don't authenticate (a local Ollama, an unsecured gateway) still need a
        // non-empty credential to satisfy the SDK; the header is simply ignored on the other end.
        var credential = new ApiKeyCredential(
            string.IsNullOrWhiteSpace(profile.ApiKey) ? "not-required" : profile.ApiKey);

        if (profile.Kind == ProviderKind.AzureOpenAI)
            return new AzureOpenAIClient(new Uri(baseUrl), credential);

        return string.IsNullOrWhiteSpace(baseUrl)
            ? new OpenAIClient(credential)
            : new OpenAIClient(credential, new OpenAIClientOptions { Endpoint = new Uri(baseUrl) });
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        bool embeddingsDropped;

        lock (_gate)
        {
            // Same reasoning as above: drop, don't dispose. A capture or chat turn started before
            // the user hit Save finishes on the client it already has.
            embeddingsDropped = _embeddings is not null;

            _chat = null;
            _vision = null;
            _embeddings = null;
            _chatKey = null;
            _visionKey = null;
            _embeddingsKey = null;
        }

        if (embeddingsDropped)
            EmbeddingProviderChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;

        lock (_gate)
        {
            _chat?.Dispose();
            _vision?.Dispose();
            _embeddings?.Dispose();
            _chat = null;
            _vision = null;
            _embeddings = null;
        }
    }

    /// <summary>A role bound to a concrete provider and model, plus the identity its client caches on.</summary>
    private sealed record ResolvedRole(ProviderProfile Profile, string Model, string CacheKey);
}
