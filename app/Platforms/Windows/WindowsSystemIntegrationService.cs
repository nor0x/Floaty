using System.Diagnostics;
using System.Runtime.InteropServices;
using Floaty.Services;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using Windows.Media.Control;

namespace Floaty.Platforms.Windows;

/// <summary>
/// Windows backing for the chat's OS tools: raw Win32 for the clipboard, window focus and power state,
/// NAudio's Core Audio wrapper for the master volume, and WinRT's
/// <see cref="GlobalSystemMediaTransportControlsSessionManager"/> — the same session list the volume
/// flyout shows — for media. Everything is reachable from the existing TFM and packages.
/// </summary>
public sealed class WindowsSystemIntegrationService : ISystemIntegrationService
{
    public bool IsSupported => true;

    // --- Clipboard ---

    public string? GetClipboardText() => Win32Clipboard.ReadText();

    public bool SetClipboardText(string text) => Win32Clipboard.WriteText(text);

    // --- Open ---

    public SystemActionResult ShellOpen(string target)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return SystemActionResult.Success($"Opened {target}.");
        }
        catch (Exception ex)
        {
            return SystemActionResult.Failure($"Could not open {target}: {ex.Message}");
        }
    }

    public SystemActionResult Reveal(string path)
    {
        try
        {
            // explorer.exe parses its own command line: the comma belongs to /select, and the path
            // must be quoted as one argument, so ArgumentList (which would quote the switch) won't do.
            using var _ = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = false,
            });
            return SystemActionResult.Success($"Showing {path} in File Explorer.");
        }
        catch (Exception ex)
        {
            return SystemActionResult.Failure($"Could not show {path}: {ex.Message}");
        }
    }

    public IReadOnlyList<InstalledApp> FindApps(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var needle = query.Trim();
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
        };

        var apps = new List<(InstalledApp App, int Rank)>();
        foreach (var root in roots.Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r)))
        {
            IEnumerable<string> shortcuts;
            try
            {
                shortcuts = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories);
            }
            catch
            {
                continue;
            }

            foreach (var shortcut in shortcuts)
            {
                var name = Path.GetFileNameWithoutExtension(shortcut);

                // Start Menu folders are full of "Uninstall X" and "X Help" shortcuts; never launch those.
                if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase))
                    continue;

                var rank = string.Equals(name, needle, StringComparison.OrdinalIgnoreCase) ? 0
                    : name.StartsWith(needle, StringComparison.OrdinalIgnoreCase) ? 1
                    : name.Contains(needle, StringComparison.OrdinalIgnoreCase) ? 2
                    : -1;
                if (rank >= 0)
                    apps.Add((new InstalledApp(name, shortcut), rank));
            }
        }

        return apps
            .OrderBy(a => a.Rank)
            .ThenBy(a => a.App.Name.Length)
            .Select(a => a.App)
            .DistinctBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // --- Media ---

    public async Task<SystemActionResult> ControlMediaAsync(MediaAction action)
    {
        try
        {
            var session = await GetMediaSessionAsync();
            if (session is not null)
            {
                var accepted = action switch
                {
                    MediaAction.PlayPause => await session.TryTogglePlayPauseAsync(),
                    MediaAction.Play => await session.TryPlayAsync(),
                    MediaAction.Pause => await session.TryPauseAsync(),
                    MediaAction.Next => await session.TrySkipNextAsync(),
                    MediaAction.Previous => await session.TrySkipPreviousAsync(),
                    MediaAction.Stop => await session.TryStopAsync(),
                    _ => false,
                };

                var app = AppName(session.SourceAppUserModelId);
                return accepted
                    ? SystemActionResult.Success($"Sent {Describe(action)} to {app}.")
                    : SystemActionResult.Failure($"{app} did not accept {Describe(action)}.");
            }
        }
        catch
        {
            // Fall through to the media keys, which reach players that never registered a session.
        }

        if (MediaKey(action) is not { } key)
            return SystemActionResult.Failure("No media is playing.");

        PressKey(key);
        return SystemActionResult.Success($"Pressed the {Describe(action)} media key.");
    }

    public async Task<NowPlaying?> GetNowPlayingAsync()
    {
        try
        {
            var session = await GetMediaSessionAsync();
            if (session is null)
                return null;

            var props = await session.TryGetMediaPropertiesAsync();
            var status = session.GetPlaybackInfo()?.PlaybackStatus.ToString() ?? "Unknown";
            return new NowPlaying(
                AppName(session.SourceAppUserModelId),
                NullIfEmpty(props?.Title),
                NullIfEmpty(props?.Artist),
                NullIfEmpty(props?.AlbumTitle),
                status);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<GlobalSystemMediaTransportControlsSession?> GetMediaSessionAsync()
    {
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        return manager.GetCurrentSession();
    }

    // --- Volume ---

    public VolumeState? GetVolume()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var volume = device.AudioEndpointVolume;
            return new VolumeState(
                (int)Math.Round(volume.MasterVolumeLevelScalar * 100),
                volume.Mute,
                device.FriendlyName);
        }
        catch
        {
            return null;
        }
    }

    public SystemActionResult SetVolume(int? percent, bool? muted)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var volume = device.AudioEndpointVolume;

            if (percent is { } p)
                volume.MasterVolumeLevelScalar = Math.Clamp(p, 0, 100) / 100f;
            if (muted is { } m)
                volume.Mute = m;

            var level = (int)Math.Round(volume.MasterVolumeLevelScalar * 100);
            return SystemActionResult.Success(
                $"{device.FriendlyName}: volume {level}%{(volume.Mute ? ", muted" : string.Empty)}.");
        }
        catch (Exception ex)
        {
            return SystemActionResult.Failure($"Could not change the volume: {ex.Message}");
        }
    }

    // --- Windows ---

    public SystemActionResult FocusWindow(nint hwnd)
    {
        try
        {
            if (!IsWindow(hwnd))
                return SystemActionResult.Failure("That window no longer exists.");

            if (IsIconic(hwnd))
                ShowWindow(hwnd, SW_RESTORE);

            // Windows only lets the foreground process hand focus away. Floaty usually is it (the user
            // just typed into the chat), but a tap of Alt first satisfies the rule when it isn't.
            if (!SetForegroundWindow(hwnd))
            {
                PressKey(VK_MENU);
                SetForegroundWindow(hwnd);
            }

            return SystemActionResult.Success("Switched to the window.");
        }
        catch (Exception ex)
        {
            return SystemActionResult.Failure($"Could not switch to the window: {ex.Message}");
        }
    }

    // --- System info ---

    public string GetSystemInfo()
    {
        var lines = new List<string>
        {
            $"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})",
            $"Computer: {Environment.MachineName}, user {Environment.UserName}",
            $"Uptime: {FormatUptime(TimeSpan.FromMilliseconds(Environment.TickCount64))}",
            $"Processors: {Environment.ProcessorCount} logical",
        };

        if (GetSystemPowerStatus(out var power))
        {
            if (power.BatteryFlag == 128)
            {
                lines.Add("Power: no battery (mains powered)");
            }
            else
            {
                var percent = power.BatteryLifePercent == 255 ? "unknown" : $"{power.BatteryLifePercent}%";
                var source = power.ACLineStatus == 1 ? "plugged in" : "on battery";
                var charging = (power.BatteryFlag & 8) != 0 ? ", charging" : string.Empty;
                var remaining = power.BatteryLifeTime is > 0 and not uint.MaxValue
                    ? $", about {FormatUptime(TimeSpan.FromSeconds(power.BatteryLifeTime))} left"
                    : string.Empty;
                var saver = power.SystemStatusFlag == 1 ? ", battery saver on" : string.Empty;
                lines.Add($"Battery: {percent}, {source}{charging}{remaining}{saver}");
            }
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int light)
                lines.Add($"App theme: {(light == 0 ? "dark" : "light")}");
        }
        catch
        {
            // The theme is a nicety; a locked-down registry is no reason to drop the rest.
        }

        lines.Add($"Displays: {GetSystemMetrics(SM_CMONITORS)}, primary " +
                  $"{GetSystemMetrics(SM_CXSCREEN)}x{GetSystemMetrics(SM_CYSCREEN)}");

        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");
            lines.Add($"System drive {drive.Name}: {drive.AvailableFreeSpace / 1_000_000_000.0:0.#} GB free " +
                      $"of {drive.TotalSize / 1_000_000_000.0:0.#} GB");
        }
        catch
        {
            // Same as above.
        }

        return string.Join('\n', lines);
    }

    // --- Helpers ---

    private static string AppName(string? appUserModelId)
    {
        if (string.IsNullOrWhiteSpace(appUserModelId))
            return "the media app";

        // Win32 players report their exe ("Spotify.exe"), packaged ones an AUMID ("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic").
        var name = appUserModelId.Split('!')[^1];
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private static string Describe(MediaAction action) => action switch
    {
        MediaAction.PlayPause => "play/pause",
        MediaAction.Next => "next track",
        MediaAction.Previous => "previous track",
        _ => action.ToString().ToLowerInvariant(),
    };

    private static ushort? MediaKey(MediaAction action) => action switch
    {
        MediaAction.PlayPause or MediaAction.Play or MediaAction.Pause => VK_MEDIA_PLAY_PAUSE,
        MediaAction.Next => VK_MEDIA_NEXT_TRACK,
        MediaAction.Previous => VK_MEDIA_PREV_TRACK,
        MediaAction.Stop => VK_MEDIA_STOP,
        _ => null,
    };

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string FormatUptime(TimeSpan span) =>
        span.TotalDays >= 1 ? $"{(int)span.TotalDays}d {span.Hours}h"
        : span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m"
        : $"{span.Minutes}m";

    private static void PressKey(ushort vk)
    {
        keybd_event((byte)vk, 0, KEYEVENTF_EXTENDEDKEY, 0);
        keybd_event((byte)vk, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, 0);
    }

    // --- Native ---

    private const int SW_RESTORE = 9;
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const int SM_CMONITORS = 80;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_MENU = 0x12;
    private const ushort VK_MEDIA_NEXT_TRACK = 0xB0;
    private const ushort VK_MEDIA_PREV_TRACK = 0xB1;
    private const ushort VK_MEDIA_STOP = 0xB2;
    private const ushort VK_MEDIA_PLAY_PAUSE = 0xB3;

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, nuint dwExtraInfo);
}
