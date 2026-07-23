namespace Floaty.Services;

/// <summary>
/// Fallback for platforms without automatic screen history (Android / iOS, and Mac for now).
/// Mac can later observe frontmost-app changes via <c>NSWorkspace</c> notifications.
/// </summary>
public sealed class NullScreenHistoryService : IScreenHistoryService
{
}
