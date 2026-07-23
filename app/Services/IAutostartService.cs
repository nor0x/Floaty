namespace Floaty.Services;

/// <summary>
/// Keeps the OS autostart registration in sync with <see cref="FloatyConfig.AutostartMode"/>.
/// Implementations react to <see cref="SettingsService.Changed"/> on their own, so any code path
/// that saves config keeps the registration current — the UI never calls an apply method directly.
/// </summary>
public interface IAutostartService
{
    /// <summary>False on platforms without an autostart implementation.</summary>
    bool IsSupported { get; }

    /// <summary>
    /// Re-aligns the OS registration with the saved config and current executable path. Called once
    /// at app start so the registration survives install-path changes (e.g. after an update).
    /// </summary>
    void SyncOnStartup();
}
