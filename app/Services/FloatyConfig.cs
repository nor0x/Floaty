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
/// Whether (and how) Floaty launches automatically when the user signs in to the OS.
/// </summary>
public enum AutostartMode
{
    /// <summary>Floaty only runs when launched manually.</summary>
    Disabled,

    /// <summary>Starts hidden in the notification area; summoned via the tray icon or Alt+F.</summary>
    Minimized,

    /// <summary>Starts with the floating ring visible.</summary>
    Visible,
}

/// <summary>
/// Which shell the <c>exec</c> agent tool runs commands through. Stored as a string so config.json
/// stays hand-editable. <see cref="ExecShellKind.Custom"/> uses the user-supplied executable + args.
/// </summary>
public enum ExecShellKind
{
    /// <summary>Windows PowerShell (<c>powershell.exe</c>).</summary>
    PowerShell,

    /// <summary>PowerShell Core (<c>pwsh</c>), cross-platform.</summary>
    Pwsh,

    /// <summary>Windows Command Prompt (<c>cmd.exe</c>).</summary>
    Cmd,

    /// <summary>Bourne-again shell (<c>bash</c>).</summary>
    Bash,

    /// <summary>Z shell (<c>zsh</c>).</summary>
    Zsh,

    /// <summary>POSIX shell (<c>sh</c>).</summary>
    Sh,

    /// <summary>A user-specified executable and argument template.</summary>
    Custom,
}

/// <summary>
/// Whether shell commands run immediately or require explicit user confirmation first.
/// </summary>
public enum ExecApprovalMode
{
    /// <summary>Every shell command must be approved by the user in the chat overlay.</summary>
    AlwaysRequire,

    /// <summary>Shell commands run immediately without a human-in-the-loop confirmation step.</summary>
    NeverRequire,
}

/// <summary>
/// Where the chat panel lives on screen.
/// </summary>
public enum ChatPanelPlacement
{
    /// <summary>The panel opens flush against the ring and travels with it (the classic behavior).</summary>
    Floating,

    /// <summary>
    /// The panel is its own borderless window, placed independently of the ring (bottom-left of the
    /// work area until the user drags it elsewhere).
    /// </summary>
    Fixed,
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
    /// Whether the shutter sound plays when the user captures a window (<c>/capture</c> or an
    /// <c>@</c> attachment). The ring's shutter animation is not affected by this — only the audio.
    /// </summary>
    public bool CaptureSoundEnabled { get; set; } = true;

    /// <summary>
    /// Sound played on capture: a built-in name or a filename from <c>~/.floaty/sounds</c>. Empty
    /// uses <see cref="SettingsService.DefaultCaptureSound"/>, mirroring <see cref="RingImageFileName"/>.
    /// </summary>
    public string CaptureSoundFileName { get; set; } = string.Empty;

    /// <summary>Whether a sound plays once an assistant reply has finished streaming.</summary>
    public bool AssistantDoneSoundEnabled { get; set; } = true;

    /// <summary>
    /// Sound played when an assistant reply finishes. Empty uses
    /// <see cref="SettingsService.DefaultAssistantDoneSound"/>. Draws from the same pool as
    /// <see cref="CaptureSoundFileName"/>.
    /// </summary>
    public string AssistantDoneSoundFileName { get; set; } = string.Empty;

    /// <summary>Playback volume for Floaty's own sounds, 0–1. Clamped on use.</summary>
    public double SoundVolume { get; set; } = 0.7;

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

    /// <summary>
    /// Default for files dropped on the ring or the chat panel: when true they are also copied into
    /// <c>~/.floaty/drops</c> and embedded into memory, when false they are one-shot context for the
    /// message they ride on. Off by default — a dropped file is usually a question, not an archive —
    /// and every attachment chip carries its own toggle that overrides this for that one file.
    /// </summary>
    public bool RememberDroppedFiles { get; set; }

    /// <summary>
    /// When true, the summon hotkey (Alt+F) also picks up whatever text was selected in the app the
    /// user was in and attaches it to the pending prompt as a removable chip. Read through UI
    /// Automation where the app exposes its selection, otherwise by briefly borrowing the clipboard
    /// (see <c>WindowsSelectionCaptureService</c>). On by default: summoning Floaty while something is
    /// selected is nearly always a question about that selection, and the chip is one click to drop.
    /// </summary>
    public bool AttachSelectionOnSummon { get; set; } = true;

    /// <summary>Keeps the ring window above other windows. Enabled by default.</summary>
    public bool AlwaysOnTop { get; set; } = true;

    /// <summary>
    /// Top-left corner of the floating ring overlay window in physical screen pixels. Null until
    /// the ring has been moved at least once; restored on startup and clamped into a visible work
    /// area so monitor changes cannot strand it off-screen.
    /// </summary>
    public int? OverlayWindowX { get; set; }

    /// <inheritdoc cref="OverlayWindowX"/>
    public int? OverlayWindowY { get; set; }

    /// <summary>
    /// Whether the chat panel is glued to the ring or lives in its own independently placed window.
    /// Stored as a string ("Floating") so config.json stays hand-editable.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ChatPanelPlacement ChatPanelPlacement { get; set; } = ChatPanelPlacement.Floating;

    /// <summary>
    /// Top-left corner of the fixed chat window in physical screen pixels. Null until the window has
    /// been placed once; it then defaults to the bottom-left of the work area. Clamped back into a
    /// visible work area on show, so a display that went away can't strand it off-screen.
    /// </summary>
    public int? ChatWindowX { get; set; }

    /// <inheritdoc cref="ChatWindowX"/>
    public int? ChatWindowY { get; set; }

    /// <summary>
    /// Size of the fixed chat panel in device-independent units, set by the corner resize grip.
    /// Clamped to the panel's own min/max on use.
    /// </summary>
    public double ChatWindowWidth { get; set; } = 360;

    /// <inheritdoc cref="ChatWindowWidth"/>
    public double ChatWindowHeight { get; set; } = 420;

    /// <summary>
    /// Whether Floaty starts automatically on OS sign-in, and if so whether it starts hidden or
    /// visible. Windows-only; mirrored into the HKCU Run registry key on save.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AutostartMode AutostartMode { get; set; } = AutostartMode.Disabled;

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

    /// <summary>
    /// Whether the <c>exec</c> agent tool is exposed to the model. Off by default: shell execution is a
    /// powerful capability, and when on it follows <see cref="ExecApprovalMode"/>.
    /// </summary>
    public bool ExecEnabled { get; set; } = false;

    /// <summary>
    /// Whether shell commands require approval in the overlay before execution.
    /// Stored as a string so config.json stays hand-editable.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ExecApprovalMode ExecApprovalMode { get; set; } = ExecApprovalMode.AlwaysRequire;

    /// <summary>Which shell the <c>exec</c> tool launches. Defaults to PowerShell on Windows, zsh elsewhere.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ExecShellKind ExecShell { get; set; } =
        OperatingSystem.IsWindows() ? ExecShellKind.PowerShell : ExecShellKind.Zsh;

    /// <summary>Executable path for <see cref="ExecShellKind.Custom"/>, e.g. <c>/usr/bin/fish</c>.</summary>
    public string ExecCustomShellPath { get; set; } = string.Empty;

    /// <summary>
    /// Argument template for <see cref="ExecShellKind.Custom"/>. The <c>{command}</c> token is replaced by
    /// the command text; if the token is absent, the command is appended as a final argument.
    /// </summary>
    public string ExecCustomShellArgs { get; set; } = "-c {command}";
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
