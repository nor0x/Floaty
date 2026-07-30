using System.Numerics;
using System.Text;

namespace Floaty.Services;

/// <summary>
/// Remembers what screen history has already stored so the same screen isn't embedded twice. Two
/// questions get asked, both against a bounded least-recently-used table rather than just the
/// previous capture — real switching is round-robin (editor → browser → terminal → editor), so a
/// depth-one memory never recognises the window the user comes back to:
/// <list type="number">
///   <item><description><see cref="IsInCooldown"/> — was this exact window captured recently? Cheap,
///   so it runs before the capture does.</description></item>
///   <item><description><see cref="IsDuplicate"/> — does this content match a screen we've already
///   seen, under any title? Runs after the capture but before the embedding call.</description></item>
/// </list>
/// Content is compared by <see cref="Fingerprint"/> — a SimHash over normalized word shingles —
/// rather than by exact equality: accessibility text carries clocks, unread badges and line/column
/// readouts that change on their own, and a byte-exact hash treats every revisit as new content.
/// </summary>
public sealed class CaptureDedupe
{
    // Windows remembered before the least-recently-touched entry is evicted. Bounded so a machine
    // left running for weeks can't grow this without limit.
    private const int MaxTrackedWindows = 64;

    // Fingerprints differing in at most this many of 64 bits are the same screen. Measured over
    // editor/browser/terminal captures: digit-only churn a clock tick or a cursor move lands at 0
    // (Normalize alone handles it), one transient status word at ~5, a scrolled viewport at ~14, a
    // different file in the same window chrome at ~19, a different application at ~27. Eight sits in
    // the gap: it absorbs word-level chrome noise while leaving a scrolled viewport — genuinely new
    // content the user looked at — to be stored. Same-window revisits are throttled by
    // SameWindowCooldown, not by this.
    private const int MaxHammingDistance = 8;

    // Words per shingle. Shingling makes the fingerprint order-sensitive, so two pages that merely
    // share a vocabulary don't collide.
    private const int ShingleWords = 3;

    private const ulong FnvOffset = 14695981039346656037;
    private const ulong FnvPrime = 1099511628211;

    // Both the dispatcher thread (IsInCooldown) and the background capture task (IsDuplicate,
    // Record) reach in here. Traffic is ~3 calls/minute, so one lock costs nothing.
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _seen = new(StringComparer.Ordinal);

    // Monotonic counter for eviction order; the wall clock isn't guaranteed to move forwards.
    private long _tick;

    private sealed class Entry
    {
        public ulong Fingerprint;
        public DateTime CapturedUtc;
        public long LastTouched;
    }

    /// <summary>
    /// True when <paramref name="key"/> was captured less than <paramref name="cooldown"/> ago, i.e.
    /// re-capturing it now would be wasted work.
    /// </summary>
    public bool IsInCooldown(string key, DateTime nowUtc, TimeSpan cooldown)
    {
        lock (_gate)
        {
            if (!_seen.TryGetValue(key, out var entry) || nowUtc - entry.CapturedUtc >= cooldown)
                return false;

            // A window the user keeps returning to shouldn't be the one we evict.
            entry.LastTouched = ++_tick;
            return true;
        }
    }

    /// <summary>
    /// True when <paramref name="fingerprint"/> is within <see cref="MaxHammingDistance"/> of any
    /// screen already recorded — the same content re-titled (browser tab flicker) or barely changed.
    /// </summary>
    public bool IsDuplicate(ulong fingerprint)
    {
        lock (_gate)
        {
            foreach (var entry in _seen.Values)
            {
                if (BitOperations.PopCount(entry.Fingerprint ^ fingerprint) <= MaxHammingDistance)
                    return true;
            }

            return false;
        }
    }

    /// <summary>Records a capture that was actually stored, evicting the stalest window if full.</summary>
    public void Record(string key, ulong fingerprint, DateTime nowUtc)
    {
        lock (_gate)
        {
            if (_seen.TryGetValue(key, out var existing))
            {
                existing.Fingerprint = fingerprint;
                existing.CapturedUtc = nowUtc;
                existing.LastTouched = ++_tick;
                return;
            }

            if (_seen.Count >= MaxTrackedWindows)
            {
                // The table is a recency heuristic, not a log; dropping the stalest entry only risks
                // re-storing a window the user hasn't touched in a long time.
                var stalest = key;
                var lowest = long.MaxValue;
                foreach (var (candidate, entry) in _seen)
                {
                    if (entry.LastTouched < lowest)
                    {
                        lowest = entry.LastTouched;
                        stalest = candidate;
                    }
                }

                _seen.Remove(stalest);
            }

            _seen[key] = new Entry
            {
                Fingerprint = fingerprint,
                CapturedUtc = nowUtc,
                LastTouched = ++_tick,
            };
        }
    }

    /// <summary>Forgets everything, e.g. when the user turns screen history off.</summary>
    public void Clear()
    {
        lock (_gate)
            _seen.Clear();
    }

    /// <summary>
    /// Reduces capture text to the part that identifies the screen: lower-cased, whitespace collapsed,
    /// and every token containing a digit removed. Clocks, unread counts, line:column readouts,
    /// elapsed timers and "modified 3 minutes ago" all churn while the screen itself hasn't changed.
    /// Only fingerprinting sees this — the stored and embedded text keep their digits.
    /// </summary>
    public static string Normalize(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return string.Empty;

        var sb = new StringBuilder(content.Length);

        foreach (var token in content.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.AsSpan().ContainsAnyInRange('0', '9'))
                continue;

            if (sb.Length > 0)
                sb.Append(' ');
            sb.Append(token.ToLowerInvariant());
        }

        return sb.ToString();
    }

    /// <summary>
    /// SimHash of <paramref name="content"/>: every shingle votes ±1 on each of 64 bit positions and
    /// the sign of each column becomes that bit. Near-duplicates disagree on only a few columns, so
    /// they land a small Hamming distance apart — something an exact hash cannot express.
    /// </summary>
    public static ulong Fingerprint(string content)
    {
        var normalized = Normalize(content);
        if (normalized.Length == 0)
            return 0;

        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Too short to shingle: hash the whole thing so tiny screens still match themselves.
        if (words.Length < ShingleWords)
            return Fnv1a(FnvOffset, normalized);

        var weights = new int[64];
        for (var i = 0; i + ShingleWords <= words.Length; i++)
        {
            var hash = FnvOffset;
            for (var w = i; w < i + ShingleWords; w++)
            {
                hash = Fnv1a(hash, words[w]);
                hash = (hash ^ ' ') * FnvPrime;
            }

            for (var bit = 0; bit < 64; bit++)
                weights[bit] += (hash & (1UL << bit)) != 0 ? 1 : -1;
        }

        var result = 0UL;
        for (var bit = 0; bit < 64; bit++)
        {
            if (weights[bit] > 0)
                result |= 1UL << bit;
        }

        return result;
    }

    // FNV-1a, hand-rolled because string.GetHashCode() is randomized per process: fingerprints have
    // to stay comparable across restarts, and this avoids taking a hashing dependency for 3 lines.
    private static ulong Fnv1a(ulong hash, string text)
    {
        foreach (var c in text)
            hash = (hash ^ c) * FnvPrime;
        return hash;
    }
}
