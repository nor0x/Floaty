namespace Floaty.Services;

/// <summary>Why a capture did or didn't happen, for the one-off tool to relay and the rule list to show.</summary>
public sealed record WindowCaptureOutcome(bool Stored, string Message, CaptureResult? Capture = null);

/// <summary>A rule just stored a capture.</summary>
public sealed record CaptureRuleFired(string RuleId, string WindowTitle, DateTimeOffset At);

/// <summary>
/// Runs <see cref="FloatyConfig.CaptureRules"/>: "capture Notepad every time it opens", "capture Visual
/// Studio every 5 minutes". Also backs the chat's one-off <c>capture_window</c>, so both share one
/// matching rule and one set of exclusions.
/// </summary>
/// <remarks>
/// Deliberately a poll, not a hook. Screen history's WinEvent hooks only report the foreground window,
/// and a rule has to see windows that open in the background and keep capturing them while the user
/// works elsewhere — which <see cref="IScreenCaptureService.CaptureWindowAsync"/> (PrintWindow plus UI
/// Automation) can do. One <see cref="IScreenCaptureService.ListWindowsAsync"/> every
/// <see cref="PollInterval"/> is a single EnumWindows pass, and the timer only runs while a rule is live.
///
/// Rules run whatever <see cref="FloatyConfig.ScreenHistoryMode"/> says: they are explicit requests.
/// They are silent (no sound, no toast), since an interval rule would otherwise chirp all day.
/// </remarks>
public sealed class CaptureRuleService : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    // Floor between any two rule captures, however many rules fire at once: each one is an embedding
    // (and, with a screenshot, a vision call), so a burst of matches must not become a burst of spend.
    private static readonly TimeSpan MinCaptureSpacing = TimeSpan.FromSeconds(15);

    // Text-only captures with less than this aren't worth a memory. Screenshot captures are kept
    // regardless — a code canvas or a design tool can be almost all image.
    private const int MinTextChars = 40;

    private readonly SettingsService _settings;
    private readonly IScreenCaptureService _capture;
    private readonly IMemoryService _memory;
    private readonly CaptureDedupe _dedupe = new();
    private readonly object _gate = new();

    // Per rule: the windows it has seen, keyed by handle. Rebuilt from scratch when a rule is added
    // (its first tick only takes note of what is already open, so "when it opens" means from now on).
    private readonly Dictionary<string, Dictionary<nint, WindowState>> _seen = new();
    private readonly Dictionary<string, DateTimeOffset> _lastCaptured = new();

    private CancellationTokenSource? _loop;
    private DateTimeOffset _lastCaptureAt = DateTimeOffset.MinValue;
    private int _captureInFlight;
    private bool _started;

    public CaptureRuleService(SettingsService settings, IScreenCaptureService capture, IMemoryService memory)
    {
        _settings = settings;
        _capture = capture;
        _memory = memory;
    }

    /// <summary>Raised on a background thread after a rule stores a capture.</summary>
    public event EventHandler<CaptureRuleFired>? Captured;

    /// <summary>When the rule last stored a capture in this session, or null.</summary>
    public DateTimeOffset? LastCaptured(string ruleId)
    {
        lock (_gate)
            return _lastCaptured.TryGetValue(ruleId, out var at) ? at : null;
    }

    /// <summary>Starts following the config. Called once the overlay is up; later calls are no-ops.</summary>
    public void Start()
    {
        if (_started)
            return;
        _started = true;

        _settings.Changed += OnSettingsChanged;
        Reconcile();
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        StopLoop();
    }

    /// <summary>
    /// True when <paramref name="window"/> belongs to what <paramref name="match"/> names: an exact
    /// process name, or a substring of the process name or the title. Case-insensitive.
    /// </summary>
    public static bool Matches(string match, WindowInfo window)
    {
        if (string.IsNullOrWhiteSpace(match))
            return false;

        var needle = match.Trim();
        if (needle.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            needle = needle[..^4];

        return window.ProcessName.Contains(needle, StringComparison.OrdinalIgnoreCase)
               || window.Title.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Open windows <paramref name="match"/> would capture, most recently used first.</summary>
    public async Task<IReadOnlyList<WindowInfo>> FindWindowsAsync(string match, CancellationToken cancellationToken = default)
    {
        var windows = await _capture.ListWindowsAsync(cancellationToken);

        // Exact process-name hits first: "code" should prefer VS Code over a window titled "barcode".
        return windows
            .Where(w => Matches(match, w))
            .OrderByDescending(w => string.Equals(w.ProcessName, match.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Captures one window into memory now. Used by rules (with <see cref="IMemoryService.RuleCaptureSource"/>)
    /// and by the chat's one-off capture. Never throws.
    /// </summary>
    public async Task<WindowCaptureOutcome> CaptureAsync(
        WindowInfo window,
        bool includeScreenshot,
        string source,
        bool skipUnchanged,
        CancellationToken cancellationToken = default)
    {
        // Whole-window exclusions first, so a password manager's contents are never even read.
        if (CaptureRedactor.IsExcludedApp(window.ProcessName)
            || CaptureRedactor.IsExcludedApp(window.Title)
            || CaptureRedactor.IsPrivateWindow(window.Title))
        {
            return new(false, $"\"{window.Title}\" is a password manager or private window, which Floaty never captures.");
        }

        if (!_memory.CanRemember)
            return new(false, "Memory isn't set up — assign an embedding model in Settings → Model provider.");

        CaptureResult? result = null;
        var stored = false;
        try
        {
            result = await _capture.CaptureWindowAsync(window.Hwnd, includeScreenshot, cancellationToken);
            if (result is null)
                return new(false, $"\"{window.Title}\" can't be captured right now (closed or minimized?).");

            // The raw capture path writes the window's text as-is; scrub it like screen history does,
            // both in memory and in the file on disk.
            var content = Redact(result.Content);
            if (!string.Equals(content, result.Content, StringComparison.Ordinal))
                TryRedactFile(result.TextPath);
            result = result with { Content = content, AppName = window.ProcessName };

            if (!includeScreenshot && content.Trim().Length < MinTextChars)
                return new(false, $"\"{window.Title}\" shows almost no text; try again with a screenshot.");

            var fingerprint = CaptureDedupe.Fingerprint(content);
            if (skipUnchanged && _dedupe.IsDuplicate(fingerprint))
                return new(false, $"\"{window.Title}\" hasn't changed since the last capture.");

            stored = await _memory.RememberCaptureAsync(result, source, cancellationToken);
            if (!stored)
                return new(false, "The capture could not be stored in memory.");

            _dedupe.Record(window.Hwnd.ToString(), fingerprint, DateTime.UtcNow);
            return new(true, $"Captured \"{window.Title}\".", result);
        }
        catch (Exception ex)
        {
            return new(false, $"Capturing \"{window.Title}\" failed: {ex.Message}");
        }
        finally
        {
            // Don't keep files that never made it into memory.
            if (!stored && result is not null)
            {
                TryDeleteFile(result.ImagePath);
                TryDeleteFile(result.TextPath);
            }
        }
    }

    // --- Loop ---

    private void OnSettingsChanged(object? sender, EventArgs e) => Reconcile();

    /// <summary>Runs the poll only while some rule can still fire; forgets state for removed rules.</summary>
    private void Reconcile()
    {
        var live = LiveRules(DateTimeOffset.Now);

        lock (_gate)
        {
            var ids = live.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var stale in _seen.Keys.Where(id => !ids.Contains(id)).ToList())
                _seen.Remove(stale);
        }

        if (live.Count > 0)
            StartLoop();
        else
            StopLoop();
    }

    private void StartLoop()
    {
        lock (_gate)
        {
            if (_loop is not null)
                return;
            _loop = new CancellationTokenSource();
            var token = _loop.Token;
            _ = Task.Run(() => RunAsync(token));
        }
    }

    private void StopLoop()
    {
        lock (_gate)
        {
            // Cancel only: the loop may be the caller (a rule expired mid-tick) and is still
            // awaiting on this token. An undisposed, cancelled source holds no timer or handle.
            _loop?.Cancel();
            _loop = null;
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            do
            {
                try
                {
                    await TickAsync(token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Rules are best-effort: one bad tick must never stop the loop.
                    System.Diagnostics.Debug.WriteLine($"[Floaty] Capture rules tick failed: {ex.Message}");
                }
            }
            while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task TickAsync(CancellationToken token)
    {
        var now = DateTimeOffset.Now;
        var rules = LiveRules(now);
        if (rules.Count == 0)
        {
            // The last rule expired while running; there is no config save to notice that for us.
            StopLoop();
            return;
        }

        var windows = await _capture.ListWindowsAsync(token);
        var due = new List<(CaptureRule Rule, WindowInfo Window)>();

        lock (_gate)
        {
            foreach (var rule in rules)
            {
                var firstTick = !_seen.TryGetValue(rule.Id, out var seen);
                if (seen is null)
                {
                    seen = new Dictionary<nint, WindowState>();
                    _seen[rule.Id] = seen;
                }

                var matching = windows.Where(w => Matches(rule.Match, w)).ToList();
                var open = matching.Select(w => w.Hwnd).ToHashSet();
                foreach (var gone in seen.Keys.Where(h => !open.Contains(h)).ToList())
                    seen.Remove(gone);

                foreach (var window in matching)
                {
                    if (!seen.TryGetValue(window.Hwnd, out var state))
                    {
                        // An on-open rule ignores what was already open when it started; an interval
                        // rule covers those too. Either way, wait a tick so the window has content.
                        seen[window.Hwnd] = new WindowState
                        {
                            FirstSeen = now,
                            Done = firstTick && rule.Trigger == CaptureRuleTrigger.OnOpen,
                        };
                        continue;
                    }

                    if (state.Done)
                        continue;

                    var isDue = rule.Trigger == CaptureRuleTrigger.OnOpen
                        ? true
                        : state.LastCapture is not { } last
                          || now - last >= TimeSpan.FromMinutes(Math.Clamp(rule.IntervalMinutes, 1, 1440));

                    if (isDue)
                        due.Add((rule, window));
                }
            }
        }

        // One capture per tick at most, oldest-waiting first; the rest are still due next tick. With
        // the spacing floor this caps rules at about four captures a minute in the worst case.
        if (due.Count == 0 || now - _lastCaptureAt < MinCaptureSpacing)
            return;
        if (Interlocked.CompareExchange(ref _captureInFlight, 1, 0) != 0)
            return;

        try
        {
            var (rule, window) = due
                .OrderBy(d => StateFor(d.Rule.Id, d.Window.Hwnd)?.LastCapture ?? DateTimeOffset.MinValue)
                .First();

            _lastCaptureAt = now;
            var outcome = await CaptureAsync(
                window, rule.IncludeScreenshot, IMemoryService.RuleCaptureSource, skipUnchanged: true, token);

            lock (_gate)
            {
                // Advance even when nothing was stored (unchanged, excluded, too little text), or the
                // window would be retried every tick.
                if (StateFor(rule.Id, window.Hwnd) is { } state)
                {
                    state.LastCapture = now;
                    if (rule.Trigger == CaptureRuleTrigger.OnOpen)
                        state.Done = true;
                }

                if (outcome.Stored)
                    _lastCaptured[rule.Id] = now;
            }

            if (outcome.Stored)
                Captured?.Invoke(this, new CaptureRuleFired(rule.Id, window.Title, now));
        }
        finally
        {
            Volatile.Write(ref _captureInFlight, 0);
        }
    }

    private WindowState? StateFor(string ruleId, nint hwnd) =>
        _seen.TryGetValue(ruleId, out var seen) && seen.TryGetValue(hwnd, out var state) ? state : null;

    private List<CaptureRule> LiveRules(DateTimeOffset now) =>
        _settings.Current.CaptureRules
            .Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Match) && (r.ExpiresAt is null || r.ExpiresAt > now))
            .ToList();

    private static string Redact(string text) =>
        string.Join('\n', text.Split('\n').Select(CaptureRedactor.RedactLine));

    private static void TryRedactFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            File.WriteAllText(path, Redact(File.ReadAllText(path)));
        }
        catch
        {
            // The in-memory copy is what gets embedded; a file we couldn't rewrite is deleted below
            // if the capture isn't stored, and otherwise matches what manual captures already keep.
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
            // Orphaned capture files are harmless.
        }
    }

    private sealed class WindowState
    {
        public DateTimeOffset FirstSeen { get; init; }
        public DateTimeOffset? LastCapture { get; set; }
        public bool Done { get; set; }
    }
}
