using System.Runtime.InteropServices;
using System.Security;
using Floaty.Services;
using Windows.UI.Notifications;

// Windows.Data.Xml.Dom.XmlDocument, not System.Xml's: the toast APIs take the WinRT one, and having
// both in scope is the single easiest way to lose an afternoon in this file.
using XmlDocument = Windows.Data.Xml.Dom.XmlDocument;

namespace Floaty.Platforms.Windows;

/// <summary>
/// Native Windows toasts through WinRT (<see cref="ToastNotificationManager"/>), reachable straight
/// from the <c>net10.0-windows10.0.19041.0</c> TFM — no extra package reference.
/// </summary>
/// <remarks>
/// Floaty is an <b>unpackaged</b> Win32 app (Velopack, no MSIX identity), so the notification
/// platform identifies it by AppUserModelID instead, and that id is only valid while a Start Menu
/// shortcut carries it in <c>System.AppUserModel.ID</c>. Without one, <c>CreateToastNotifier</c>
/// succeeds and <c>Show</c> then silently does nothing. Velopack's installer already writes exactly
/// that property (<see cref="Aumid"/>) onto <c>Floaty.lnk</c>, so on an installed Floaty this class
/// touches nothing; on a dev or portable run it creates or repairs the shortcut itself through
/// IShellLink + IPropertyStore. See <see cref="EnsureRegistered"/>.
///
/// Scheduling is delegated wholesale to Windows (<see cref="ScheduledToastNotification"/>), which is
/// what makes "remind me at 3pm" survive the user quitting Floaty: there is no timer of ours and no
/// persisted queue. Every member swallows its failures into a <see cref="NotificationResult"/>, per
/// the <see cref="INotificationService"/> contract.
/// </remarks>
public sealed class WindowsNotificationService : INotificationService
{
    /// <summary>
    /// The AppUserModelID Velopack's installer stamps onto
    /// <c>%AppData%\…\Start Menu\Programs\Floaty.lnk</c> — <c>"velopack." + packId</c>, and the pack
    /// id is <c>Floaty</c> (see <c>.github/workflows/release-windows.yml</c>). Matching it means an
    /// installed build needs no shortcut work at all, and the toast carries the same name and icon
    /// the Start Menu already shows. Kept as one constant so pointing dev runs at a separate identity
    /// stays a one-line change. Must stay under 129 characters or scheduled toasts break.
    /// </summary>
    private const string Aumid = "velopack.Floaty";

    // Same file name Velopack uses, so a later install replaces ours rather than leaving two entries.
    private const string ShortcutFileName = "Floaty.lnk";

    // A toast due sooner than this is shown immediately instead: AddToSchedule rejects a delivery
    // time in the past outright, and a few seconds of lead is not worth a trip through the
    // notification platform. Losing that race would fail the user's request on a technicality.
    private const int MinLeadSeconds = 10;

    // Self-imposed. Windows documents no ceiling on how far ahead a toast may be scheduled, and a
    // year is far past any plausible "remind me" — this exists so a hallucinated year-3000 timestamp
    // comes back as a sentence instead of vanishing into the schedule forever.
    private const int MaxHorizonDays = 365;

    // Also self-imposed, far below the platform's documented 4096, so a model in a loop cannot fill
    // the user's notification database.
    private const int MaxScheduled = 64;

    // Windows visually truncates a toast well before either of these; they exist to keep a runaway
    // model argument from producing a pathological payload.
    private const int MaxTitleChars = 120;
    private const int MaxBodyChars = 500;

    private readonly Lock _gate = new();
    private bool _registrationAttempted;
    private string? _registrationError;

    public bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10);

    public NotificationResult Show(string title, string body)
    {
        if (!EnsureRegistered(out var error))
            return new(false, null, error);

        try
        {
            ToastNotificationManager.CreateToastNotifier(Aumid)
                .Show(new ToastNotification(BuildToastXml(title, body, persistent: false)));
            return new(true, null, null);
        }
        catch (Exception ex)
        {
            return new(false, null, $"Windows refused the notification ({ex.Message}). " +
                                    "Notifications may be switched off for Floaty in " +
                                    "Windows Settings → System → Notifications.");
        }
    }

    public NotificationResult Schedule(string title, string body, DateTimeOffset deliveryTime)
    {
        if (!EnsureRegistered(out var error))
            return new(false, null, error);

        var now = DateTimeOffset.Now;

        // Near enough to now that scheduling it would be silly, or would race the past-time check.
        if (deliveryTime <= now.AddSeconds(MinLeadSeconds))
            return Show(title, body);

        if (deliveryTime > now.AddDays(MaxHorizonDays))
            return new(false, null,
                $"That's more than {MaxHorizonDays} days away; Floaty won't schedule that far ahead.");

        try
        {
            var notifier = ToastNotificationManager.CreateToastNotifier(Aumid);
            var pending = notifier.GetScheduledToastNotifications();
            if (pending.Count >= MaxScheduled)
                return new(false, null,
                    $"There are already {pending.Count} notifications scheduled. Cancel one first.");

            var toast = new ScheduledToastNotification(
                BuildToastXml(title, body, persistent: true), deliveryTime)
            {
                Id = NewId(pending),
            };

            notifier.AddToSchedule(toast);
            return new(true, toast.Id, null);
        }
        catch (Exception ex)
        {
            return new(false, null, $"Windows refused to schedule the notification ({ex.Message}).");
        }
    }

    public IReadOnlyList<ScheduledNotification> ListScheduled()
    {
        if (!EnsureRegistered(out _))
            return [];

        try
        {
            var now = DateTimeOffset.Now;
            return ToastNotificationManager.CreateToastNotifier(Aumid)
                .GetScheduledToastNotifications()
                // A toast that just fired can still sit in the schedule for a moment, and the model
                // must never tell the user to expect something that already rang.
                .Where(n => n.DeliveryTime > now)
                .OrderBy(n => n.DeliveryTime)
                .Select(n => new ScheduledNotification(
                    n.Id ?? string.Empty,
                    ReadText(n.Content, 0),
                    ReadText(n.Content, 1),
                    n.DeliveryTime))
                .ToList();
        }
        catch
        {
            // Listing is informational; an unreadable schedule reads as "nothing scheduled" rather
            // than failing the chat turn.
            return [];
        }
    }

    public NotificationResult Cancel(string id)
    {
        if (!EnsureRegistered(out var error))
            return new(false, null, error);

        var trimmed = (id ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return new(false, null, "No notification id was given.");

        try
        {
            var notifier = ToastNotificationManager.CreateToastNotifier(Aumid);
            var match = notifier.GetScheduledToastNotifications()
                .FirstOrDefault(n => string.Equals(n.Id, trimmed, StringComparison.OrdinalIgnoreCase));

            if (match is null)
                return new(false, null, $"No pending notification with id '{trimmed}'.");

            notifier.RemoveFromSchedule(match);
            return new(true, match.Id, null);
        }
        catch (Exception ex)
        {
            return new(false, null, $"Could not cancel that notification ({ex.Message}).");
        }
    }

    /// <summary>
    /// Ties the running process to the same AppUserModelID the Start Menu shortcut carries, so
    /// taskbar grouping, pinning and toast attribution all agree. Must run before the first window
    /// exists, hence the call site in <c>Program.Main</c> rather than in <c>App</c>. Belt-and-braces
    /// only: toasts are always raised through the explicit <c>CreateToastNotifier(aumid)</c> overload,
    /// so nothing here depends on it.
    /// </summary>
    public static void SetProcessAumid()
    {
        try
        {
            SetCurrentProcessExplicitAppUserModelID(Aumid);
        }
        catch
        {
            // Purely cosmetic; never worth failing startup over.
        }
    }

    /// <summary>
    /// A fresh 12-character id. WinRT caps <see cref="ScheduledToastNotification.Id"/> at <b>16</b>
    /// characters and throws past that, so this is a hex slice of a GUID rather than anything
    /// descriptive: inside the cap with margin, unique enough never to collide, and short enough that
    /// a model can copy it back into <c>cancel_notification</c> unmangled. The live schedule is
    /// checked anyway, because a duplicate id would silently replace an existing reminder.
    /// </summary>
    private static string NewId(IReadOnlyList<ScheduledToastNotification> existing)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var id = Guid.NewGuid().ToString("N")[..12];
            if (!existing.Any(n => string.Equals(n.Id, id, StringComparison.OrdinalIgnoreCase)))
                return id;
        }

        return Guid.NewGuid().ToString("N")[..12];
    }

    /// <summary>
    /// The toast payload. ToastGeneric with two <c>&lt;text&gt;</c> nodes is the minimum that renders
    /// a title plus body correctly on both Windows 10 and 11.
    /// </summary>
    /// <param name="persistent">
    /// True for a scheduled toast, which gets <c>scenario="reminder"</c> so an alarm stays on screen
    /// until the user acts instead of sliding into the Notification Center after a few seconds. That
    /// scenario is rejected unless the toast also carries at least one action, hence the system
    /// Dismiss button — which needs no activator, unlike a real click target.
    /// </param>
    /// <remarks>
    /// Deliberately no <c>activationType</c>/<c>launch</c>: activating an unpackaged toast requires a
    /// ToastActivatorCLSID registered on the shortcut, which Floaty has no need for, so a click simply
    /// dismisses. No <c>appLogoOverride</c> either — the attribution icon is taken from the Start Menu
    /// shortcut's executable, which is already Floaty's.
    /// </remarks>
    private static XmlDocument BuildToastXml(string title, string body, bool persistent)
    {
        var scenario = persistent ? " scenario=\"reminder\"" : string.Empty;
        var actions = persistent
            ? "<actions><action activationType=\"system\" arguments=\"dismiss\" content=\"Dismiss\"/></actions>"
            : string.Empty;

        var xml =
            $"<toast{scenario}><visual><binding template=\"ToastGeneric\">" +
            $"<text>{Sanitize(title, MaxTitleChars)}</text>" +
            $"<text>{Sanitize(body, MaxBodyChars)}</text>" +
            $"</binding></visual>{actions}</toast>";

        var doc = new XmlDocument();
        doc.LoadXml(xml);
        return doc;
    }

    /// <summary>
    /// Escapes and clamps model-supplied text. Both halves are load-bearing: the text comes from an
    /// LLM, so an unescaped <c>&amp;</c> or <c>&lt;</c> would make LoadXml throw, and a stray control
    /// character is illegal in XML 1.0 <i>even escaped</i> — which is why the characters are filtered
    /// first rather than relying on SecurityElement.Escape alone.
    /// </summary>
    private static string Sanitize(string? text, int max)
    {
        var cleaned = new string((text ?? string.Empty)
                .Select(c => char.IsControl(c) ? ' ' : c)
                .ToArray())
            .Trim();

        if (cleaned.Length > max)
            cleaned = cleaned[..max];

        return SecurityElement.Escape(cleaned) ?? string.Empty;
    }

    /// <summary>
    /// Recovers a title or body back out of a scheduled toast. WinRT hands back the XmlDocument it was
    /// given and nothing else — there is no Title property — so the text nodes are read positionally,
    /// exactly as <see cref="BuildToastXml"/> wrote them.
    /// </summary>
    private static string ReadText(XmlDocument content, int index)
    {
        try
        {
            var nodes = content.GetElementsByTagName("text");
            return index < (int)nodes.Count ? nodes[index].InnerText ?? string.Empty : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Makes <see cref="Aumid"/> resolvable by the notification platform, once per process.
    /// </summary>
    /// <remarks>
    /// Deliberately lazy rather than run at startup: a Start Menu shortcut buys nothing but toasts, so
    /// creating one on every launch would touch the Start Menu of users who never ask for a reminder
    /// (and would do it on every <c>dotnet run</c>). The first notification is an explicit request and
    /// the right moment to become notification-capable.
    /// </remarks>
    private bool EnsureRegistered(out string? error)
    {
        lock (_gate)
        {
            if (!_registrationAttempted)
            {
                _registrationAttempted = true;
                _registrationError = TryRegister();
            }

            error = _registrationError;
            return error is null;
        }
    }

    private static string? TryRegister()
    {
        try
        {
            // %AppData%\Microsoft\Windows\Start Menu\Programs
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Programs), ShortcutFileName);

            if (File.Exists(path))
                return AdoptShortcut(path);

            CreateShortcut(path);

            // The shell needs a beat to notice a brand-new Start Menu entry; without this the very
            // first toast after creating the shortcut can be dropped. At most once per machine.
            Thread.Sleep(750);
            return null;
        }
        catch (Exception ex)
        {
            return $"Floaty could not register for Windows notifications ({ex.Message}).";
        }
    }

    /// <summary>
    /// Adds our AppUserModelID to an existing <c>Floaty.lnk</c> if it is absent or different. The
    /// link's target, icon and arguments are left exactly as found: on an installed build that file is
    /// Velopack's and points at <c>current\Floaty.exe</c>, which is precisely what keeps working
    /// across updates — retargeting it at <c>Environment.ProcessPath</c> would pin it to a versioned
    /// folder and break at the next one.
    /// </summary>
    private static string? AdoptShortcut(string path)
    {
        var link = (IShellLinkW)new ShellLink();
        var file = (System.Runtime.InteropServices.ComTypes.IPersistFile)link;
        file.Load(path, StgmReadWrite);

        var store = (IPropertyStore)link;
        var key = AppUserModelIdKey;

        store.GetValue(ref key, out var current);
        var existing = current.AsString();
        PropVariantClear(ref current);

        // The installed case: Velopack already wrote it, so touch nothing.
        if (string.Equals(existing, Aumid, StringComparison.Ordinal))
            return null;

        var value = PropVariant.FromString(Aumid);
        try
        {
            store.SetValue(ref key, ref value);
            store.Commit();
            file.Save(null!, true); // null = save back over the file it was loaded from
        }
        finally
        {
            PropVariantClear(ref value);
        }

        return null;
    }

    /// <summary>
    /// Creates the Start Menu shortcut pointing at the running executable. Only reached on dev or
    /// portable runs — an installed Floaty already has one from Velopack.
    /// </summary>
    private static void CreateShortcut(string path)
    {
        // Same source WindowsAutostartService uses to find the current install.
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            throw new InvalidOperationException("the running executable path is unknown");

        var link = (IShellLinkW)new ShellLink();
        link.SetPath(exe);
        link.SetWorkingDirectory(Path.GetDirectoryName(exe) ?? string.Empty);
        link.SetIconLocation(exe, 0);
        link.SetDescription("Floaty");

        var store = (IPropertyStore)link;
        var key = AppUserModelIdKey;
        var value = PropVariant.FromString(Aumid);
        try
        {
            store.SetValue(ref key, ref value);
            store.Commit();
        }
        finally
        {
            PropVariantClear(ref value);
        }

        ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Save(path, true);
    }

    // ---------------------------------------------------------------------------------------------
    // COM interop. Classic [ComImport] rather than [GeneratedComInterface]: BuiltInComInteropSupport
    // is already on in the csproj, and every other piece of interop under Platforms/Windows is written
    // this way. Nothing here touches the UI thread — tool methods run on MTA thread-pool threads, and
    // both the shell link and the WinRT notification APIs are happy there.
    // ---------------------------------------------------------------------------------------------

    private const int StgmReadWrite = 0x00000002;

    [ComImport, Guid("00021401-0000-0000-C000-000000000046"), ClassInterface(ClassInterfaceType.None)]
    private class ShellLink;

    /// <remarks>
    /// Every vtable slot must be declared, in order, even though only four are called. The unused
    /// getters take <see cref="IntPtr"/> rather than a StringBuilder so no marshalling is generated
    /// for calls that never happen.
    /// </remarks>
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath(IntPtr pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription(IntPtr pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory(IntPtr pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments(IntPtr pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out ushort pwHotkey);
        void SetHotkey(ushort wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation(IntPtr pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PropertyKey pkey);
        void GetValue(ref PropertyKey key, out PropVariant pv);
        void SetValue(ref PropertyKey key, ref PropVariant pv);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    /// <summary>PROPVARIANT, only ever holding a VT_LPWSTR here. 24 bytes on x64.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct PropVariant
    {
        private const ushort VtLpwstr = 31;

        public ushort Vt;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public IntPtr Pointer;
        public IntPtr Padding;

        public static PropVariant FromString(string value) =>
            new() { Vt = VtLpwstr, Pointer = Marshal.StringToCoTaskMemUni(value) };

        public readonly string? AsString() =>
            Vt == VtLpwstr && Pointer != IntPtr.Zero ? Marshal.PtrToStringUni(Pointer) : null;
    }

    /// <summary>
    /// <c>System.AppUserModel.ID</c> — verified against the shortcut Velopack's installer writes,
    /// which carries exactly this property and nothing else.
    /// </summary>
    private static PropertyKey AppUserModelIdKey =>
        new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string appID);
}
