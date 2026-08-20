namespace Floaty.Services;

/// <summary>
/// A starting point for a <see cref="ProviderProfile"/>: the transport, the endpoint, and sensible
/// default model ids. Everything here is copied into the profile when the user adds it and can be
/// edited afterwards — a preset is never consulted again except for placeholder text and hints.
/// </summary>
/// <param name="Id">Stable slug, also the default <see cref="ProviderProfile.Id"/> for single-instance presets.</param>
/// <param name="BaseUrl">Default endpoint. Empty means "whatever the SDK defaults to".</param>
/// <param name="KeyUrl">Where to get a key, linked from the provider tab. Null when not applicable.</param>
public sealed record ProviderPreset(
    string Id,
    string DisplayName,
    ProviderKind Kind,
    string BaseUrl,
    string ChatModel,
    string EmbeddingModel,
    string VisionModel,
    string Blurb,
    string? KeyUrl = null,
    bool NeedsKey = true,
    bool AllowMultiple = false);

/// <summary>
/// The built-in catalog of providers offered by the "+ Add" button in Settings → Model Provider.
/// Mirrors <see cref="SttModelCatalog"/>: a static table of records, ids stable across releases so
/// a saved <see cref="ProviderProfile.PresetId"/> keeps resolving.
/// </summary>
public static class ProviderPresets
{
    /// <summary>Preset id of the profile the legacy single-provider config migrates into.</summary>
    public const string OpenAiId = "openai";

    /// <summary>Preset id of the on-device embedding provider.</summary>
    public const string LocalId = "local";

    /// <summary>Preset id of the Ollama profile, which gets a live model list instead of text fields.</summary>
    public const string OllamaId = "ollama";

    public static IReadOnlyList<ProviderPreset> All { get; } =
    [
        new(
            Id: OpenAiId,
            DisplayName: "OpenAI",
            Kind: ProviderKind.OpenAI,
            BaseUrl: "",
            ChatModel: "gpt-4o-mini",
            EmbeddingModel: "text-embedding-3-small",
            VisionModel: "gpt-4o-mini",
            Blurb: "Chat, embeddings and vision from one key.",
            KeyUrl: "https://platform.openai.com/api-keys"),
        new(
            Id: "anthropic",
            DisplayName: "Anthropic",
            Kind: ProviderKind.Anthropic,
            BaseUrl: "https://api.anthropic.com",
            ChatModel: "claude-sonnet-4-5",
            EmbeddingModel: "",
            VisionModel: "claude-haiku-4-5",
            Blurb: "Claude for chat and captioning. No embedding models — pair it with Local or OpenAI.",
            KeyUrl: "https://console.anthropic.com/settings/keys"),
        new(
            Id: "gemini",
            DisplayName: "Google Gemini",
            Kind: ProviderKind.OpenAiCompatible,
            BaseUrl: "https://generativelanguage.googleapis.com/v1beta/openai/",
            ChatModel: "gemini-2.5-flash",
            EmbeddingModel: "text-embedding-004",
            VisionModel: "gemini-2.5-flash",
            Blurb: "Reached through Gemini's OpenAI-compatible endpoint.",
            KeyUrl: "https://aistudio.google.com/apikey"),
        new(
            Id: "azure-openai",
            DisplayName: "Azure OpenAI",
            Kind: ProviderKind.AzureOpenAI,
            BaseUrl: "https://your-resource.openai.azure.com",
            ChatModel: "",
            EmbeddingModel: "",
            VisionModel: "",
            Blurb: "Model ids here are your deployment names, not the underlying model names.",
            AllowMultiple: true),
        new(
            Id: "openrouter",
            DisplayName: "OpenRouter",
            Kind: ProviderKind.OpenAiCompatible,
            BaseUrl: "https://openrouter.ai/api/v1",
            ChatModel: "openai/gpt-4o-mini",
            EmbeddingModel: "",
            VisionModel: "openai/gpt-4o-mini",
            Blurb: "One key, most models. Ids are namespaced, e.g. anthropic/claude-sonnet-4.5.",
            KeyUrl: "https://openrouter.ai/keys"),
        new(
            Id: "groq",
            DisplayName: "Groq",
            Kind: ProviderKind.OpenAiCompatible,
            BaseUrl: "https://api.groq.com/openai/v1",
            ChatModel: "llama-3.3-70b-versatile",
            EmbeddingModel: "",
            VisionModel: "",
            Blurb: "Very fast open-weight chat models.",
            KeyUrl: "https://console.groq.com/keys"),
        new(
            Id: "mistral",
            DisplayName: "Mistral",
            Kind: ProviderKind.OpenAiCompatible,
            BaseUrl: "https://api.mistral.ai/v1",
            ChatModel: "mistral-small-latest",
            EmbeddingModel: "mistral-embed",
            VisionModel: "pixtral-12b-latest",
            Blurb: "Chat, embeddings and vision (Pixtral).",
            KeyUrl: "https://console.mistral.ai/api-keys"),
        new(
            Id: "deepseek",
            DisplayName: "DeepSeek",
            Kind: ProviderKind.OpenAiCompatible,
            BaseUrl: "https://api.deepseek.com/v1",
            ChatModel: "deepseek-chat",
            EmbeddingModel: "",
            VisionModel: "",
            Blurb: "Inexpensive chat and reasoning models.",
            KeyUrl: "https://platform.deepseek.com/api_keys"),
        new(
            Id: "xai",
            DisplayName: "xAI",
            Kind: ProviderKind.OpenAiCompatible,
            BaseUrl: "https://api.x.ai/v1",
            ChatModel: "grok-4-fast",
            EmbeddingModel: "",
            VisionModel: "grok-4-fast",
            Blurb: "Grok models.",
            KeyUrl: "https://console.x.ai"),
        new(
            Id: OllamaId,
            DisplayName: "Ollama",
            Kind: ProviderKind.OpenAiCompatible,
            BaseUrl: "http://localhost:11434/v1",
            ChatModel: "",
            EmbeddingModel: "",
            VisionModel: "",
            Blurb: "Models you have pulled locally. Nothing leaves this machine.",
            NeedsKey: false),
        new(
            Id: LocalId,
            DisplayName: "Local (on-device)",
            Kind: ProviderKind.LocalOnnx,
            BaseUrl: "",
            ChatModel: "",
            EmbeddingModel: "",
            VisionModel: "",
            Blurb: "Embedding models running in-process. Free and offline — the cheap way to keep "
                 + "screen history vectorized.",
            NeedsKey: false),
        new(
            Id: "custom",
            DisplayName: "Custom endpoint",
            Kind: ProviderKind.OpenAiCompatible,
            BaseUrl: "",
            ChatModel: "",
            EmbeddingModel: "",
            VisionModel: "",
            Blurb: "Anything speaking the OpenAI API: LM Studio, llama.cpp's server, vLLM, a gateway.",
            NeedsKey: false,
            AllowMultiple: true),
    ];

    public static ProviderPreset? Find(string? id) =>
        string.IsNullOrEmpty(id) ? null : All.FirstOrDefault(p => p.Id == id);

    /// <summary>
    /// Builds a profile from a preset, giving it an id unique within <paramref name="existing"/>.
    /// Presets that may appear more than once (custom endpoints, several Azure resources) get a
    /// numeric suffix; the rest keep the preset id so migration and role bindings stay predictable.
    /// </summary>
    public static ProviderProfile CreateProfile(ProviderPreset preset, IEnumerable<ProviderProfile> existing)
    {
        var taken = existing.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var id = preset.Id;
        for (var n = 2; taken.Contains(id); n++)
            id = $"{preset.Id}-{n}";

        return new ProviderProfile
        {
            Id = id,
            PresetId = preset.Id,
            DisplayName = preset.DisplayName,
            Kind = preset.Kind,
            BaseUrl = preset.BaseUrl,
            ChatModel = preset.ChatModel,
            EmbeddingModel = preset.EmbeddingModel,
            VisionModel = preset.VisionModel,
        };
    }
}
