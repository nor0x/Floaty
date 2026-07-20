namespace Floaty.Services;

/// <summary>
/// Fallback for platforms without an autostart implementation (Mac could later use a LaunchAgent).
/// </summary>
public sealed class NullAutostartService : IAutostartService
{
    public bool IsSupported => false;

    public void SyncOnStartup()
    {
    }
}
