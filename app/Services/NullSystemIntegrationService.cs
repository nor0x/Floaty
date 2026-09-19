namespace Floaty.Services;

/// <summary>
/// Fallback for platforms with no OS integration yet. <c>SystemTools</c> hides its tools when
/// <see cref="IsSupported"/> is false, so these bodies exist for safety rather than for the model.
/// </summary>
public sealed class NullSystemIntegrationService : ISystemIntegrationService
{
    private static readonly SystemActionResult Unsupported =
        SystemActionResult.Failure("This isn't available on this platform.");

    public bool IsSupported => false;

    public string? GetClipboardText() => null;

    public bool SetClipboardText(string text) => false;

    public SystemActionResult ShellOpen(string target) => Unsupported;

    public SystemActionResult Reveal(string path) => Unsupported;

    public IReadOnlyList<InstalledApp> FindApps(string query) => [];

    public Task<SystemActionResult> ControlMediaAsync(MediaAction action) => Task.FromResult(Unsupported);

    public Task<NowPlaying?> GetNowPlayingAsync() => Task.FromResult<NowPlaying?>(null);

    public VolumeState? GetVolume() => null;

    public SystemActionResult SetVolume(int? percent, bool? muted) => Unsupported;

    public SystemActionResult FocusWindow(nint hwnd) => Unsupported;

    public string GetSystemInfo() => $"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}";
}
