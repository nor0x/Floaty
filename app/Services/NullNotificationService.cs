namespace Floaty.Services;

/// <summary>
/// Fallback for platforms with no notification backend. macOS would use UserNotifications — see the
/// reference-only <c>MacNotificationService</c> under <c>Platforms/MacCatalyst</c>.
/// </summary>
/// <remarks>
/// <see cref="ChatService"/> skips registering the notification tools when <see cref="IsSupported"/>
/// is false, so these bodies exist for safety rather than for the model: anything that did reach them
/// gets a sentence it can relay instead of a null reference.
/// </remarks>
public sealed class NullNotificationService : INotificationService
{
    private const string Unsupported = "Desktop notifications aren't available on this platform.";

    public bool IsSupported => false;

    public NotificationResult Show(string title, string body) => new(false, null, Unsupported);

    public NotificationResult Schedule(string title, string body, DateTimeOffset deliveryTime) =>
        new(false, null, Unsupported);

    public IReadOnlyList<ScheduledNotification> ListScheduled() => [];

    public NotificationResult Cancel(string id) => new(false, null, Unsupported);
}
