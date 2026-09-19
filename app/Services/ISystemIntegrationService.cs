namespace Floaty.Services;

/// <summary>
/// The outcome of an OS action. A returned value rather than an exception, like
/// <see cref="NotificationResult"/>: the only callers are chat tools, which relay
/// <see cref="Message"/> to the model as a sentence.
/// </summary>
public readonly record struct SystemActionResult(bool Ok, string Message)
{
    public static SystemActionResult Success(string message) => new(true, message);

    public static SystemActionResult Failure(string message) => new(false, message);
}

/// <summary>A media transport command for whatever app is currently playing.</summary>
public enum MediaAction
{
    PlayPause,
    Play,
    Pause,
    Next,
    Previous,
    Stop,
}

/// <summary>What the OS reports as playing right now.</summary>
public sealed record NowPlaying(string App, string? Title, string? Artist, string? Album, string Status);

/// <summary>The default output device's master volume.</summary>
public readonly record struct VolumeState(int Percent, bool Muted, string DeviceName);

/// <summary>An installed app the user can launch by name (a Start Menu shortcut on Windows).</summary>
public sealed record InstalledApp(string Name, string ShortcutPath);

/// <summary>
/// The OS primitives behind the chat's clipboard, open, media, volume, window and system-info tools.
/// Policy — which targets may be opened, what gets redacted — lives in <c>SystemTools</c>; this
/// interface only does what it is told.
/// </summary>
/// <remarks>
/// No member throws: failures come back as <see cref="SystemActionResult.Failure"/> or null, so a
/// flaky audio device or a locked clipboard can never break a chat turn.
/// </remarks>
public interface ISystemIntegrationService
{
    /// <summary>False on platforms without an implementation; the tools are then not offered at all.</summary>
    bool IsSupported { get; }

    /// <summary>The clipboard's plain text, or null when it holds none.</summary>
    string? GetClipboardText();

    /// <summary>Replaces the clipboard's contents with <paramref name="text"/>.</summary>
    bool SetClipboardText(string text);

    /// <summary>Hands a URL (or an existing file/folder) to the OS shell to open with its default handler.</summary>
    SystemActionResult ShellOpen(string target);

    /// <summary>Opens the file manager with <paramref name="path"/> selected.</summary>
    SystemActionResult Reveal(string path);

    /// <summary>Installed apps whose name contains <paramref name="query"/>, best matches first.</summary>
    IReadOnlyList<InstalledApp> FindApps(string query);

    /// <summary>Sends a transport command to the current media session.</summary>
    Task<SystemActionResult> ControlMediaAsync(MediaAction action);

    /// <summary>The current media session, or null when nothing is playing or paused.</summary>
    Task<NowPlaying?> GetNowPlayingAsync();

    /// <summary>The default output device's volume, or null when there is no such device.</summary>
    VolumeState? GetVolume();

    /// <summary>Sets the master volume and/or mute state of the default output device.</summary>
    SystemActionResult SetVolume(int? percent, bool? muted);

    /// <summary>Restores and brings a top-level window to the front.</summary>
    SystemActionResult FocusWindow(nint hwnd);

    /// <summary>A short plain-text summary: OS, uptime, power, theme, displays.</summary>
    string GetSystemInfo();
}
