using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.AI;

namespace Floaty.Services.Tools;

/// <summary>
/// OS integration for the chat: clipboard, opening things, media and volume, switching windows and a
/// system summary. The platform work is in <see cref="ISystemIntegrationService"/>; the rules about what
/// the model may touch live here, so they hold on every platform.
/// </summary>
/// <remarks>
/// None of these ask for approval: each is either read-only or trivially undone. The one that could do
/// real harm, <c>open</c>, refuses anything that would run code — otherwise it would be a way around the
/// shell tool's opt-in and approval. Launching apps is limited to Start Menu shortcuts, i.e. things the
/// user installed.
/// </remarks>
public sealed class SystemTools : IChatToolset
{
    private const int MaxClipboardChars = 6000;

    // Opening any of these runs code. Refused unless the user has opted into the shell tool, which is
    // the switch that says "the model may run things on this machine".
    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
        ".msi", ".msp", ".msc", ".scr", ".pif", ".cpl", ".hta", ".jar", ".lnk", ".url", ".reg", ".appref-ms",
        ".application", ".sh", ".app", ".command", ".py", ".pyw", ".ahk", ".pl", ".rb", ".inf", ".scf",
        ".settingcontent-ms", ".library-ms", ".search-ms", ".diagcab", ".gadget", ".ws",
    };

    private static readonly HashSet<string> AllowedSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http", "https", "mailto",
    };

    private readonly ISystemIntegrationService _system;
    private readonly IScreenCaptureService _screens;
    private readonly SettingsService _settings;

    public SystemTools(ISystemIntegrationService system, IScreenCaptureService screens, SettingsService settings)
    {
        _system = system;
        _screens = screens;
        _settings = settings;

        Tools =
        [
            AIFunctionFactory.Create(GetClipboard, name: "get_clipboard"),
            AIFunctionFactory.Create(SetClipboard, name: "set_clipboard"),
            AIFunctionFactory.Create(Open, name: "open"),
            AIFunctionFactory.Create(OpenApp, name: "open_app"),
            AIFunctionFactory.Create(MediaControl, name: "media_control"),
            AIFunctionFactory.Create(Volume, name: "volume"),
            AIFunctionFactory.Create(ListWindows, name: "list_windows"),
            AIFunctionFactory.Create(FocusWindow, name: "focus_window"),
            AIFunctionFactory.Create(SystemInfo, name: "system_info"),
        ];
    }

    public bool IsAvailable => _system.IsSupported;

    public IReadOnlyList<AITool> Tools { get; }

    public string Guidance =>
        "You can work with the user's computer directly: read or write the clipboard (get_clipboard, " +
        "set_clipboard — use set_clipboard whenever the user wants text copied), open a web page, file or " +
        "folder (open), launch an installed app (open_app), control music and video playback " +
        "(media_control), read or change the system volume (volume), list and switch between open " +
        "windows (list_windows, focus_window), and report battery, uptime and similar (system_info). " +
        "Use these rather than the shell when they fit. Keep confirmations to one short sentence.";

    // --- Clipboard ---

    [Description("Read the text currently on the user's clipboard. Use when the user refers to what they " +
                 "copied. Credential-shaped strings are redacted.")]
    private string GetClipboard()
    {
        var text = _system.GetClipboardText();
        if (string.IsNullOrEmpty(text))
            return "The clipboard holds no text.";

        // The clipboard is where passwords and tokens go in transit; it is about to be sent to a model
        // provider, so scrub it the same way screen captures are scrubbed before they reach disk.
        var redacted = string.Join('\n', text.Split('\n').Select(CaptureRedactor.RedactLine));
        return redacted.Length <= MaxClipboardChars
            ? redacted
            : $"{redacted[..MaxClipboardChars]}\n[truncated — {redacted.Length} characters in total]";
    }

    [Description("Put text on the user's clipboard, replacing what was there, so they can paste it anywhere.")]
    private string SetClipboard(
        [Description("The exact text to copy.")] string text)
    {
        if (string.IsNullOrEmpty(text))
            return "Nothing to copy.";

        return _system.SetClipboardText(text)
            ? $"Copied {text.Length} characters to the clipboard."
            : "Could not write to the clipboard; another app may be holding it. Try again in a moment.";
    }

    // --- Open ---

    [Description("Open a web page (http/https), an email draft (mailto:), or an existing file or folder in " +
                 "its default app. Set reveal to show a file in File Explorer instead of opening it. " +
                 "Programs and scripts cannot be opened this way; use open_app for installed apps.")]
    private string Open(
        [Description("A URL, or a full path to a file or folder. '~' means the user's home folder.")] string target,
        [Description("True to show the file selected in File Explorer rather than opening it.")] bool reveal = false)
    {
        if (string.IsNullOrWhiteSpace(target))
            return "Nothing to open.";

        var trimmed = target.Trim().Trim('"');

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && !uri.IsFile)
        {
            if (!AllowedSchemes.Contains(uri.Scheme))
                return $"Only http, https and mailto links can be opened, not '{uri.Scheme}:'.";

            return _system.ShellOpen(uri.AbsoluteUri).Message;
        }

        // A bare domain ("github.com") is what people say; treat it as a web address when it
        // clearly isn't a path.
        if (!trimmed.Contains('\\') && !trimmed.Contains('/') && !Path.IsPathRooted(trimmed)
            && trimmed.Contains('.') && !trimmed.Contains(' ')
            && Uri.TryCreate("https://" + trimmed, UriKind.Absolute, out var web)
            && !File.Exists(trimmed) && !Directory.Exists(trimmed))
        {
            return _system.ShellOpen(web.AbsoluteUri).Message;
        }

        var path = ExpandPath(uri?.IsFile == true ? uri.LocalPath : trimmed);

        // A network path makes Windows authenticate to whatever server it names, handing it the user's
        // NTLM hash — exactly what a prompt-injected "open \\attacker\share" would be after. Checked
        // before anything touches the disk, since even an existence check would connect.
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            return "Network paths can't be opened from chat. The user can open them in File Explorer.";

        var isDirectory = Directory.Exists(path);
        if (!isDirectory && !File.Exists(path))
            return $"Nothing exists at '{path}'.";

        if (reveal)
            return _system.Reveal(path).Message;

        if (!isDirectory && ExecutableExtensions.Contains(Path.GetExtension(path)) && !_settings.Current.ExecEnabled)
            return $"'{Path.GetFileName(path)}' is a program or script, and opening it would run it. That " +
                   "needs the shell tool, which the user can enable in Settings → Shell. It can still be " +
                   "shown in File Explorer with reveal.";

        return _system.ShellOpen(path).Message;
    }

    [Description("Launch an installed app by name, e.g. 'Spotify', 'Visual Studio Code', 'Calculator'.")]
    private string OpenApp(
        [Description("The app's name, or part of it.")] string name)
    {
        var matches = _system.FindApps(name ?? string.Empty);
        if (matches.Count == 0)
            return $"No installed app matches '{name}'.";

        // Only launch on an unambiguous hit; otherwise let the model ask or pick with a longer name.
        var best = matches[0];
        var exact = string.Equals(best.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase);
        if (!exact && matches.Count > 1 && !best.Name.StartsWith(name!.Trim(), StringComparison.OrdinalIgnoreCase))
            return $"Several apps match '{name}': {string.Join(", ", matches.Take(8).Select(m => m.Name))}. " +
                   "Call open_app again with the full name.";

        var result = _system.ShellOpen(best.ShortcutPath);
        return result.Ok ? $"Launched {best.Name}." : result.Message;
    }

    // --- Media & volume ---

    [Description("Control music or video playing on the computer, or find out what is playing. " +
                 "Actions: now_playing, play_pause, play, pause, next, previous, stop.")]
    private async Task<string> MediaControl(
        [Description("now_playing, play_pause, play, pause, next, previous or stop.")] string action)
    {
        var normalized = (action ?? string.Empty).Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
        if (normalized is "now_playing" or "status" or "current")
            return await DescribeNowPlaying();

        MediaAction? parsed = normalized switch
        {
            "play_pause" or "toggle" => MediaAction.PlayPause,
            "play" or "resume" => MediaAction.Play,
            "pause" => MediaAction.Pause,
            "next" or "skip" => MediaAction.Next,
            "previous" or "prev" or "back" => MediaAction.Previous,
            "stop" => MediaAction.Stop,
            _ => null,
        };
        if (parsed is null)
            return $"Unknown media action '{action}'.";

        var result = await _system.ControlMediaAsync(parsed.Value);
        if (!result.Ok || parsed is not (MediaAction.Next or MediaAction.Previous))
            return result.Message;

        // After a skip, the new track is the useful part of the answer. Players update their metadata a
        // beat after acknowledging the command.
        await Task.Delay(600);
        return $"{result.Message} {await DescribeNowPlaying()}";
    }

    private async Task<string> DescribeNowPlaying()
    {
        var now = await _system.GetNowPlayingAsync();
        if (now is null)
            return "Nothing is playing.";

        var what = now.Title is null ? "something" : $"\"{now.Title}\"";
        if (now.Artist is not null)
            what += $" by {now.Artist}";
        if (now.Album is not null)
            what += $" ({now.Album})";
        return $"{now.App}: {what} — {now.Status.ToLowerInvariant()}.";
    }

    [Description("Read or change the computer's master volume. With no arguments it reports the current level.")]
    private string Volume(
        [Description("New volume, 0-100.")] int? level_percent = null,
        [Description("True to mute, false to unmute.")] bool? muted = null,
        [Description("Relative change, e.g. 10 or -10. Ignored when level_percent is given.")] int? change_by = null)
    {
        if (level_percent is null && muted is null && change_by is null)
        {
            var state = _system.GetVolume();
            return state is { } v
                ? $"{v.DeviceName}: volume {v.Percent}%{(v.Muted ? ", muted" : string.Empty)}."
                : "No audio output device was found.";
        }

        var target = level_percent;
        if (target is null && change_by is { } delta)
        {
            if (_system.GetVolume() is not { } current)
                return "No audio output device was found.";
            target = current.Percent + delta;
        }

        return _system.SetVolume(target is { } t ? Math.Clamp(t, 0, 100) : null, muted).Message;
    }

    // --- Windows ---

    [Description("List the open application windows, most recently used first.")]
    private async Task<string> ListWindows()
    {
        var windows = await _screens.ListWindowsAsync();
        if (windows.Count == 0)
            return "No application windows are open.";

        var sb = new StringBuilder($"{windows.Count} open window(s):");
        foreach (var w in windows.Take(30))
            sb.Append("\n- ").Append(w.Title).Append(" (").Append(w.ProcessName).Append(')');
        return sb.ToString();
    }

    [Description("Bring an open window to the front, matched by part of its title or its app name, " +
                 "e.g. 'Outlook' or 'README.md'.")]
    private async Task<string> FocusWindow(
        [Description("Part of the window title or the app's process name.")] string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "Say which window to switch to.";

        var needle = query.Trim();
        var windows = await _screens.ListWindowsAsync();

        // Z-order is recency, so among equal matches the first is the one the user used last.
        var match = windows.FirstOrDefault(w => string.Equals(w.ProcessName, needle, StringComparison.OrdinalIgnoreCase))
                    ?? windows.FirstOrDefault(w => w.Title.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    ?? windows.FirstOrDefault(w => w.ProcessName.Contains(needle, StringComparison.OrdinalIgnoreCase));

        if (match is null)
            return $"No open window matches '{query}'. Call list_windows to see what is open.";

        var result = _system.FocusWindow(match.Hwnd);
        return result.Ok ? $"Switched to \"{match.Title}\"." : result.Message;
    }

    // --- System ---

    [Description("Report the computer's state: OS, uptime, battery and power, app theme, displays, free disk space.")]
    private string SystemInfo() => _system.GetSystemInfo();

    private static string ExpandPath(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path == "~")
            return home;
        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
            path = Path.Combine(home, path[2..]);

        path = Environment.ExpandEnvironmentVariables(path);

        // "Downloads", "Desktop" and friends: the model often passes the folder's bare name. Relative
        // paths resolve against home, never against Floaty's own working directory.
        if (!Path.IsPathRooted(path))
            path = Path.Combine(home, path);

        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }
}
