namespace Floaty.Services;

/// <summary>
/// The outcome of a notification request. Modelled as a returned value rather than an exception
/// because the only caller is an AI tool, and tools turn failures into sentences the model can relay
/// instead of throwing (see the comment in <c>ChatService.GenerateImage</c>).
/// </summary>
/// <param name="Ok">True when the OS accepted the request.</param>
/// <param name="Id">The scheduled notification's id, for <see cref="INotificationService.Cancel"/>.
/// Null for an immediate notification, which has nothing to cancel.</param>
/// <param name="Error">A human-readable reason when <paramref name="Ok"/> is false.</param>
public readonly record struct NotificationResult(bool Ok, string? Id, string? Error);

/// <summary>One notification the OS is holding for a future delivery time.</summary>
public sealed record ScheduledNotification(
    string Id,
    string Title,
    string Body,
    DateTimeOffset DeliveryTime);

/// <summary>
/// Raises native OS notifications — immediately, or handed to the OS to fire at a future time so a
/// reminder survives Floaty being closed. Backs the <c>notify</c> / <c>list_notifications</c> /
/// <c>cancel_notification</c> chat tools in <see cref="ChatService"/>.
/// </summary>
/// <remarks>
/// Like <see cref="ISoundService"/>, no member ever throws: a missing notification registration, or a
/// user who switched notifications off, must never break a chat turn. Failures come back as
/// <see cref="NotificationResult.Error"/> so the tool can relay them as a sentence.
///
/// Scheduling is the operating system's job, not Floaty's. There is no in-process timer and nothing
/// persisted under <c>~/.floaty</c> — Windows fires a scheduled toast whether or not Floaty is
/// running, which is the whole reason "remind me at 3pm" works after the user quits.
/// </remarks>
public interface INotificationService
{
    /// <summary>
    /// False on platforms without a notification backend. <see cref="ChatService"/> skips registering
    /// the notification tools entirely when this is false, so the model is never offered a tool whose
    /// reminders would silently never arrive.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>Shows a notification now.</summary>
    NotificationResult Show(string title, string body);

    /// <summary>
    /// Asks the OS to deliver a notification at <paramref name="deliveryTime"/>. Implementations fall
    /// back to <see cref="Show"/> when that time is already here or only seconds away.
    /// </summary>
    NotificationResult Schedule(string title, string body, DateTimeOffset deliveryTime);

    /// <summary>Notifications the OS is still holding, soonest first. Empty on any failure.</summary>
    IReadOnlyList<ScheduledNotification> ListScheduled();

    /// <summary>Cancels a pending notification by id. Not-found is an error, not an exception.</summary>
    NotificationResult Cancel(string id);
}
