using System.Runtime.InteropServices;
using System.Text;
using Floaty.Services;
using Avalonia.Threading;

namespace Floaty.Platforms.Windows;

/// <summary>
/// Windows implementation of <see cref="IScreenHistoryService"/>. Installs WinEvent hooks for
/// foreground-window changes (<c>EVENT_SYSTEM_FOREGROUND</c>) and foreground title changes
/// (<c>EVENT_OBJECT_NAMECHANGE</c>, e.g. browser tab switches). Events restart a short dwell
/// timer so rapid Alt-Tab / tab-cycling collapses into a single capture of the window the user
/// settles on, which is then captured per <see cref="FloatyConfig.ScreenHistoryMode"/> and stored
/// via <see cref="IMemoryService"/> — making it searchable through the chat's memory tools.
///
/// Dedupe layers keep API spend and noise down, cheapest check first:
/// <list type="number">
///   <item><description>global floor: at most one capture attempt per <see cref="MinCaptureInterval"/> (deferred, not dropped);</description></item>
///   <item><description>any window+title captured within <see cref="SameWindowCooldown"/> is skipped;</description></item>
///   <item><description>content matching any recently stored screen is skipped.</description></item>
/// </list>
/// The last two consult <see cref="CaptureDedupe"/>, which tracks a window of recent captures rather
/// than only the previous one — switching is round-robin, so a depth-one memory would re-store every
/// window the user comes back to.
///
/// Text-only mode adds a fourth layer of its own, and it is the one that does the most work: the
/// window's text is redacted, pruned to lines that read like content, and cut down to what
/// <see cref="CaptureDayLog"/> hasn't already written today. See <see cref="CaptureTextOnlyAsync"/>.
/// </summary>
public sealed class WindowsScreenHistoryService : IScreenHistoryService
{
    // How long the user must stay on a window before it's considered "settled" and captured.
    private static readonly TimeSpan Dwell = TimeSpan.FromSeconds(2);

    // Returning to any window+title captured within this span doesn't re-capture.
    private static readonly TimeSpan SameWindowCooldown = TimeSpan.FromMinutes(5);

    // Hard floor between any two capture attempts; caps worst-case embedding/vision spend at ~3/min
    // and, because it counts attempts rather than stores, also caps the UI Automation tree walks.
    private static readonly TimeSpan MinCaptureInterval = TimeSpan.FromSeconds(20);

    // Captures with less text than this aren't worth remembering. In text-only mode this is measured
    // against what survives pruning and the day's ledger, so it also ends up being the test for
    // "nothing on this screen is new" — a revisited window reduces to nothing and dies here.
    private const int MinContentChars = 40;

    private readonly SettingsService _settings;
    private readonly IScreenCaptureService _capture;
    private readonly IMemoryService _memory;
    private readonly CaptureDayLog _dayLog;
    private readonly CaptureDedupe _dedupe = new();

    private DispatcherTimer? _dwellTimer;

    // Keep the delegate alive for the hooks' lifetime so the GC can't collect the callback.
    private WinEventDelegate? _winEventProc;
    private nint _foregroundHook;
    private nint _nameChangeHook;
    private bool _initialized;

    // All of the state below lives on the dispatcher thread (WINEVENT_OUTOFCONTEXT delivers the
    // callback via the installing thread's message loop); cross-thread dedupe state lives in
    // _dedupe, which locks internally.
    private nint _pendingHwnd;
    private DateTime _lastAttemptUtc = DateTime.MinValue;
    private int _captureInFlight;

    public WindowsScreenHistoryService(
        SettingsService settings,
        IScreenCaptureService capture,
        IMemoryService memory,
        CaptureDayLog dayLog)
    {
        _settings = settings;
        _capture = capture;
        _memory = memory;
        _dayLog = dayLog;
    }

    /// <summary>
    /// Called once the overlay window is open. Everything here lives on Avalonia's UI thread, which
    /// pumps messages - a requirement for WINEVENT_OUTOFCONTEXT hooks. Only the first call takes
    /// effect; returns whether this call initialized, so the caller ties <see cref="Shutdown"/> to
    /// the overlay window's lifetime and not e.g. Settings'.
    /// </summary>
    public bool Initialize()
    {
        if (_initialized)
            return false;
        _initialized = true;

        // Avalonia's DispatcherTimer always repeats, unlike DispatcherQueueTimer's IsRepeating=false,
        // so OnDwellElapsed stops it itself on entry and restarts it when it wants another tick.
        _dwellTimer = new DispatcherTimer { Interval = Dwell };
        _dwellTimer.Tick += (_, _) => OnDwellElapsed();

        // Settings saves can happen on another thread; hook state lives on the UI thread.
        _settings.Changed += (_, _) => Dispatcher.UIThread.Post(ApplyMode);

        ApplyMode();
        return true;
    }

    /// <summary>Uninstalls the hooks; called when the overlay window closes.</summary>
    public void Shutdown()
    {
        _dwellTimer?.Stop();
        RemoveHooks();
    }

    private void ApplyMode()
    {
        var enabled = _settings.Current.ScreenHistoryMode != ScreenHistoryMode.Disabled;
        if (enabled && _foregroundHook == nint.Zero)
            InstallHooks();
        else if (!enabled && _foregroundHook != nint.Zero)
        {
            _dwellTimer?.Stop();
            _pendingHwnd = nint.Zero;
            _dedupe.Clear();
            RemoveHooks();
        }
    }

    private void InstallHooks()
    {
        _winEventProc ??= OnWinEvent;

        const uint flags = WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS;
        _foregroundHook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, nint.Zero, _winEventProc, 0, 0, flags);
        _nameChangeHook = SetWinEventHook(
            EVENT_OBJECT_NAMECHANGE, EVENT_OBJECT_NAMECHANGE, nint.Zero, _winEventProc, 0, 0, flags);

        if (_foregroundHook == nint.Zero)
            System.Diagnostics.Debug.WriteLine("[Floaty] Screen history: SetWinEventHook failed.");
    }

    private void RemoveHooks()
    {
        if (_foregroundHook != nint.Zero)
        {
            UnhookWinEvent(_foregroundHook);
            _foregroundHook = nint.Zero;
        }

        if (_nameChangeHook != nint.Zero)
        {
            UnhookWinEvent(_nameChangeHook);
            _nameChangeHook = nint.Zero;
        }
    }

    // Runs on the dispatcher thread. Must stay trivial: NAMECHANGE fires for taskbar clocks, every
    // retitling control, etc. — anything beyond a field write and a timer poke belongs elsewhere.
    private void OnWinEvent(nint hook, uint evt, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == nint.Zero || idObject != OBJID_WINDOW || idChild != CHILDID_SELF)
            return;

        // Title changes only matter on the window the user is actually looking at.
        if (evt == EVENT_OBJECT_NAMECHANGE && hwnd != GetForegroundWindow())
            return;

        _pendingHwnd = hwnd;
        _dwellTimer!.Stop();
        // Restore the dwell interval in case the last tick left it stretched to wait out the floor.
        _dwellTimer.Interval = Dwell;
        _dwellTimer.Start();
    }

    // UI thread; cheap checks only, then hand off to a background thread.
    private void OnDwellElapsed()
    {
        // One-shot semantics: Avalonia's timer repeats, so stop first and let the paths below
        // restart it when they want to be called again.
        _dwellTimer!.Stop();

        var hwnd = _pendingHwnd;
        if (hwnd == nint.Zero || GetForegroundWindow() != hwnd)
            return; // user already moved on

        var config = _settings.Current;
        var mode = config.ScreenHistoryMode;
        if (mode == ScreenHistoryMode.Disabled || !_memory.CanRemember)
            return; // nothing downstream would store it

        var now = DateTime.UtcNow;
        var sinceAttempt = now - _lastAttemptUtc;
        if (sinceAttempt < MinCaptureInterval)
        {
            // Defer rather than drop: wait out the remainder of the floor so a window the user
            // settles on right after a capture still gets recorded, without polling every 2s.
            _dwellTimer!.Interval = MinCaptureInterval - sinceAttempt + Dwell;
            _dwellTimer.Start();
            return;
        }

        var title = GetWindowText(hwnd);
        if (_dedupe.IsInCooldown(WindowKey(hwnd, title), now, SameWindowCooldown))
            return;

        if (Interlocked.CompareExchange(ref _captureInFlight, 1, 0) != 0)
            return; // a capture is already running; drop this one

        // Count the attempt, not just a successful store: otherwise a window that keeps being
        // skipped (too little text, unchanged content) never advances the floor and we pay for a
        // full UI Automation walk — and in screenshot mode a PrintWindow — every couple of seconds.
        _lastAttemptUtc = now;

        _ = Task.Run(() => mode == ScreenHistoryMode.TextOnly
            ? CaptureTextOnlyAsync(hwnd, title)
            : CaptureWithScreenshotAsync(hwnd, title, mode));
    }

    private static string WindowKey(nint hwnd, string title) => $"{hwnd} {title}";

    /// <summary>
    /// Text-only history. Everything the window offers is scrubbed, pruned down to lines that read
    /// like content, and reduced to what hasn't already been written today — so what reaches memory
    /// is one short markdown block of genuinely new material rather than a re-dump of the same
    /// chrome. The block is also appended to the day's log (see <see cref="CaptureDayLog"/>).
    /// </summary>
    private async Task CaptureTextOnlyAsync(nint hwnd, string title)
    {
        var textPath = string.Empty;
        var stored = false;
        try
        {
            // Null covers both "no longer a valid target" and "a window we must never read":
            // a password manager, or a private-browsing window.
            var snapshot = await _capture.ReadWindowAsync(hwnd);
            if (snapshot is null)
                return;

            // Scrub before pruning, so a redacted secret is then junk-filtered too: a bare card
            // number collapses to nothing rather than leaving a "[redacted]" line behind.
            var pruned = CapturePruner.Prune(snapshot.Lines.Select(CaptureRedactor.RedactLine));

            var localNow = DateTime.Now;
            var novel = _dayLog.SelectNovel(localNow, pruned);
            var content = string.Join('\n', novel);
            if (content.Length < MinContentChars)
                return; // nothing here that today's log doesn't already hold

            var fingerprint = CaptureDedupe.Fingerprint(content);
            if (_dedupe.IsDuplicate(fingerprint))
            {
                _dedupe.Record(WindowKey(hwnd, title), fingerprint, DateTime.UtcNow);
                return;
            }

            var block = new CaptureBlock(
                localNow,
                snapshot.AppName,
                snapshot.WindowTitle,
                CaptureRedactor.RedactOrNull(snapshot.Document),
                CaptureRedactor.RedactOrNull(snapshot.Url));

            textPath = NewCapturePath(localNow);
            await File.WriteAllTextAsync(textPath, CaptureDayLog.RenderCaptureFile(block, novel));

            // Embed the rendered block rather than the bare lines: the file:/url: lines are what let
            // the model open the real source instead of trusting a flattened accessibility dump.
            var result = new CaptureResult(
                ImagePath: string.Empty,
                TextPath: textPath,
                WindowTitle: snapshot.WindowTitle,
                Content: CaptureDayLog.RenderBlock(block, novel).Trim(),
                AppName: snapshot.AppName,
                Url: block.Url);

            stored = await _memory.RememberCaptureAsync(result, IMemoryService.AutoCaptureSource);
            if (!stored)
                return;

            // The ledger only advances once the capture is really in memory. A capture rejected
            // downstream must leave its lines available to whichever capture comes next, or they'd
            // be marked as written and then never appear anywhere.
            _dayLog.Commit(block, novel);
            _dedupe.Record(WindowKey(hwnd, title), fingerprint, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            // History is best-effort: a failed capture must never surface to the user or stop the hooks.
            System.Diagnostics.Debug.WriteLine($"[Floaty] Screen history capture failed: {ex.Message}");
        }
        finally
        {
            if (!stored)
                TryDeleteFile(textPath);

            Volatile.Write(ref _captureInFlight, 0);
        }
    }

    // Second-resolution stamps collide when two captures land in the same second — unlikely under
    // the global floor, but a suffix beats silently overwriting the earlier capture.
    private static string NewCapturePath(DateTime localNow)
    {
        var stamp = localNow.ToString("yyyyMMdd-HHmmss");
        var path = Path.Combine(FloatyPaths.Captures, $"capture-{stamp}.md");

        for (var n = 2; File.Exists(path); n++)
            path = Path.Combine(FloatyPaths.Captures, $"capture-{stamp}-{n}.md");

        return path;
    }

    private async Task CaptureWithScreenshotAsync(nint hwnd, string title, ScreenHistoryMode mode)
    {
        CaptureResult? result = null;
        var stored = false;
        try
        {
            result = await _capture.CaptureWindowAsync(
                hwnd, includeScreenshot: mode == ScreenHistoryMode.TextAndScreenshot);
            if (result is null)
                return;

            if (result.Content.Trim().Length < MinContentChars)
                return;

            // The title flickered but the screen didn't, or we're back on something already stored:
            // don't pay for another embedding of the same content.
            var fingerprint = CaptureDedupe.Fingerprint(result.Content);
            if (_dedupe.IsDuplicate(fingerprint))
            {
                // This screen is already in memory, so put the window on cooldown regardless: it
                // stops us re-walking its accessibility tree every time the user comes back.
                _dedupe.Record(WindowKey(hwnd, title), fingerprint, DateTime.UtcNow);
                return;
            }

            stored = await _memory.RememberCaptureAsync(result, IMemoryService.AutoCaptureSource);
            if (!stored)
                return;

            _dedupe.Record(WindowKey(hwnd, title), fingerprint, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            // History is best-effort: a failed capture must never surface to the user or stop the hooks.
            System.Diagnostics.Debug.WriteLine($"[Floaty] Screen history capture failed: {ex.Message}");
        }
        finally
        {
            // Don't keep files that never made it into memory.
            if (!stored && result is not null)
            {
                TryDeleteFile(result.ImagePath);
                TryDeleteFile(result.TextPath);
            }

            Volatile.Write(ref _captureInFlight, 0);
        }
    }

    private static void TryDeleteFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            File.Delete(path);
        }
        catch
        {
            // Orphaned capture files are harmless; failing the pipeline over them is not.
        }
    }

    private static string GetWindowText(nint hwnd)
    {
        var len = GetWindowTextLength(hwnd);
        if (len <= 0)
            return string.Empty;
        var sb = new StringBuilder(len + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    // --- Win32 interop -------------------------------------------------------------------------

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const int OBJID_WINDOW = 0;
    private const int CHILDID_SELF = 0;

    private delegate void WinEventDelegate(
        nint hWinEventHook, uint eventType, nint hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(
        uint eventMin, uint eventMax, nint hmodWinEventProc, WinEventDelegate pfnWinEventProc,
        uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(nint hWinEventHook);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);
}
