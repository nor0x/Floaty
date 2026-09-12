using Floaty.Services;

namespace Floaty.Platforms.MacCatalyst;

/// <summary>
/// Reference-only macOS implementation of <see cref="INotificationService"/>. <b>NOT COMPILED</b> —
/// <c>Platforms/MacCatalyst</c> is <c>&lt;Compile Remove&gt;</c>'d in <c>Floaty.csproj</c>; this exists
/// so the eventual AppKit head has a starting point rather than a blank file.
/// </summary>
/// <remarks>
/// <c>UNUserNotificationCenter</c> is the modern path and covers both halves of the interface. A
/// <c>UNTimeIntervalNotificationTrigger</c> (or <c>UNCalendarNotificationTrigger</c> for a wall-clock
/// time) makes the OS fire the notification whether or not Floaty is running — exactly the property
/// Windows' <c>ScheduledToastNotification</c> provides, and the reason neither platform needs a timer
/// of Floaty's own. <c>GetPendingNotificationRequests</c> and
/// <c>RemovePendingNotificationRequests</c> cover list and cancel.
///
/// Two things must be re-verified on a real Mac before any of this is trusted:
///
/// <list type="bullet">
/// <item><description>
/// <c>UNUserNotificationCenter.Current</c> throws for an app with no signed bundle identifier, so a
/// bare <c>dotnet run</c> head gets nothing until the <c>.app</c> bundle exists. This is the macOS
/// analogue of the Start Menu shortcut <see cref="Windows.WindowsNotificationService"/> needs, and it
/// belongs in the same lazy "become notification-capable on first use" slot.
/// </description></item>
/// <item><description>
/// Authorization is asynchronous and user-facing (<c>RequestAuthorization</c> with
/// Alert|Sound|Badge), while <see cref="INotificationService"/> is synchronous. The request therefore
/// has to be kicked off once at startup and its result cached, rather than awaited inside
/// <see cref="Show"/>.
/// </description></item>
/// </list>
///
/// <c>osascript -e 'display notification "…"'</c> is the zero-identity fallback: it works immediately
/// with no bundle and no authorization prompt, but it <b>cannot schedule</b>, which is most of what
/// this service is for. Worth keeping as the <see cref="Show"/> path if the bundle work is deferred.
///
/// Identifiers are plain strings on macOS with no 16-character cap, but keeping the Windows id format
/// means the chat tools read identically on both platforms.
/// </remarks>
public sealed class MacNotificationService : INotificationService
{
    // Mirrors the Windows service: registration/authorization is resolved once, lazily, and its
    // failure becomes a sentence rather than an exception.
    public bool IsSupported => true;

    /// <summary>
    /// UNMutableNotificationContent { Title, Body } with a null trigger → AddNotificationRequest,
    /// which delivers immediately.
    /// </summary>
    public NotificationResult Show(string title, string body) =>
        throw new NotImplementedException("macOS head not started — see the remarks on this class.");

    /// <summary>
    /// UNCalendarNotificationTrigger.CreateTrigger(NSDateComponents from deliveryTime, repeats: false)
    /// — a wall-clock trigger rather than an interval one, so a reminder stays correct across sleep
    /// and daylight-saving changes.
    /// </summary>
    public NotificationResult Schedule(string title, string body, DateTimeOffset deliveryTime) =>
        throw new NotImplementedException("macOS head not started — see the remarks on this class.");

    /// <summary>
    /// UNUserNotificationCenter.Current.GetPendingNotificationRequests(callback) is asynchronous, so a
    /// real implementation needs a cached snapshot refreshed on every mutation to satisfy this
    /// synchronous signature.
    /// </summary>
    public IReadOnlyList<ScheduledNotification> ListScheduled() =>
        throw new NotImplementedException("macOS head not started — see the remarks on this class.");

    /// <summary>RemovePendingNotificationRequests([id]).</summary>
    public NotificationResult Cancel(string id) =>
        throw new NotImplementedException("macOS head not started — see the remarks on this class.");
}
