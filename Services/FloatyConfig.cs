namespace Floaty.Services;

/// <summary>
/// User-editable configuration for Floaty, persisted as JSON in <c>~/.floaty/config.json</c>.
/// Mirrors the local-first design in readme.md. Only the AI provider section exists today;
/// more sections (skills, MCP, memory) will be added as siblings here.
/// </summary>
public sealed class FloatyConfig
{
    /// <summary>The active AI provider. Only "OpenAI" is supported for now.</summary>
    public string Provider { get; set; } = "OpenAI";

    /// <summary>OpenAI API key pasted by the user. Empty until configured.</summary>
    public string OpenAiApiKey { get; set; } = string.Empty;

    /// <summary>Chat model id, e.g. "gpt-4o-mini".</summary>
    public string Model { get; set; } = "gpt-4o-mini";

    /// <summary>Embedding model id used to vectorize captures, e.g. "text-embedding-3-small".</summary>
    public string EmbeddingModel { get; set; } = "text-embedding-3-small";

    /// <summary>
    /// Vision model id used to describe captured screenshots, e.g. "gpt-4o-mini". Blank disables snapshotting.
    /// </summary>
    public string SnapshotModel { get; set; } = "gpt-4o-mini";

    /// <summary>
    /// Selected ring image filename from <c>~/.floaty/ring</c>. Empty uses the built-in default ring.
    /// </summary>
    public string RingImageFileName { get; set; } = string.Empty;
}
