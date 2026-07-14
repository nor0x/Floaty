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

    /// <summary>
    /// Ring diameter in device-independent units. Defaults to <see cref="SettingsService.RingDefaultSize"/>;
    /// adjustable via the Appearance slider or Ctrl+scroll over the ring. Clamped on load.
    /// </summary>
    public double RingSize { get; set; } = 148;

    /// <summary>Configured MCP servers, each callable from chat via its <c>/name</c> slash command.</summary>
    public List<McpServerConfig> McpServers { get; set; } = new();

    /// <summary>Names of discovered agent skills the user has turned off (excluded from slash commands).</summary>
    public List<string> DisabledSkills { get; set; } = new();
}

/// <summary>
/// A single Model Context Protocol server. Either a local <c>stdio</c> process (Command + Args + Env)
/// or a remote <c>http</c> endpoint (Url + Headers). <see cref="Name"/> is the slash-command slug.
/// </summary>
public sealed class McpServerConfig
{
    /// <summary>Unique slug used for the <c>/name</c> slash command (e.g. "github").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Transport kind: <c>"stdio"</c> (local command) or <c>"http"</c> (remote URL).</summary>
    public string Transport { get; set; } = "stdio";

    /// <summary>Whether this server is active and exposed as a slash command.</summary>
    public bool Enabled { get; set; } = true;

    // --- stdio ---
    /// <summary>Executable to launch for a stdio server, e.g. "npx".</summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>Arguments passed to <see cref="Command"/>.</summary>
    public List<string> Args { get; set; } = new();

    /// <summary>Environment variables for the launched stdio process.</summary>
    public Dictionary<string, string> Env { get; set; } = new();

    // --- http ---
    /// <summary>Endpoint URL for an http server (Streamable HTTP / SSE).</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Additional HTTP headers (e.g. Authorization) for an http server.</summary>
    public Dictionary<string, string> Headers { get; set; } = new();
}
