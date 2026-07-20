using System.Text.Json.Serialization;

namespace Floaty.Services;

/// <summary>
/// What Floaty records into memory when the foreground window (or its title) changes.
/// </summary>
public enum ScreenHistoryMode
{
    /// <summary>Nothing is recorded.</summary>
    Disabled,

    /// <summary>Only the window's accessibility text is captured and embedded.</summary>
    TextOnly,

    /// <summary>Accessibility text plus a PNG of the window (described by the snapshot model, if set).</summary>
    TextAndScreenshot,
}

/// <summary>
/// How dictated text is sent once it lands in the chat entry.
/// </summary>
public enum VoiceSendMode
{
    /// <summary>Recognized text fills the entry; the user presses send themselves.</summary>
    Manual,

    /// <summary>After a long silence following speech, the message is sent automatically.</summary>
    AutoSendOnPause,
}

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

    /// <summary>
    /// Accent hex color ("#rrggbb") used for buttons, chat bubbles, and highlights.
    /// Invalid values fall back to <see cref="AccentPalette.DefaultHex"/> on use.
    /// </summary>
    public string AccentColor { get; set; } = AccentPalette.DefaultHex;

    /// <summary>
    /// What gets auto-captured into memory when the user switches windows (or tabs, via title
    /// changes). Stored as a string ("TextOnly") so config.json stays hand-editable.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ScreenHistoryMode ScreenHistoryMode { get; set; } = ScreenHistoryMode.TextOnly;

    /// <summary>
    /// When a window is attached to a prompt via @, also save that capture into memory
    /// (like <c>/capture</c>) so it can be recalled later.
    /// </summary>
    public bool RememberTaggedCaptures { get; set; } = true;

    /// <summary>Keeps the ring window above other windows. Enabled by default.</summary>
    public bool AlwaysOnTop { get; set; } = true;

    /// <summary>Configured MCP servers, each callable from chat via its <c>/name</c> slash command.</summary>
    public List<McpServerConfig> McpServers { get; set; } = new();

    /// <summary>Names of discovered agent skills the user has turned off (excluded from slash commands).</summary>
    public List<string> DisabledSkills { get; set; } = new();

    /// <summary>
    /// Selected local speech-to-text model id from <see cref="SttModelCatalog"/>. Null until the user
    /// picks a downloaded model; the mic button only shows once this points at one that is on disk.
    /// </summary>
    public string? SttSelectedModelId { get; set; }

    /// <summary>Whether dictation auto-sends after a silence pause or waits for a manual send.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public VoiceSendMode VoiceSendMode { get; set; } = VoiceSendMode.Manual;

    /// <summary>Silence length (seconds) that triggers auto-send. Clamped to 1–10 on use.</summary>
    public double AutoSendPauseSeconds { get; set; } = 2.0;
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
