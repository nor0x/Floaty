using Avalonia.Animation.Easings;
using Avalonia.Threading;

namespace Floaty.Ui;

/// <summary>
/// A tiny tween driver, replacing MAUI's <c>RotateToAsync</c>/<c>ScaleToAsync</c>/<c>FadeToAsync</c>
/// extensions and the <c>Animation(...).Commit(...)</c> pattern.
/// </summary>
/// <remarks>
/// Avalonia's own animation system is styling-driven and awkward to aim at a single mutable value that
/// C# also writes to directly (the ring's angle is driven by drags, the wheel, the idle spin and four
/// separate flourishes). The MAUI code had already hand-rolled a tick loop for exactly that reason -
/// see the shutter - so this generalises that one loop rather than inventing a second mechanism.
/// Everything runs on the UI thread; each call is independently cancellable.
/// </remarks>
public static class Anim
{
    /// <summary>Roughly 60fps. Matches the interval the MAUI shutter loop used.</summary>
    public const int FrameMs = 16;

    /// <summary>
    /// Tweens <paramref name="from"/> to <paramref name="to"/>, calling <paramref name="apply"/> once
    /// per frame and exactly once more with the final value. Returns early - without applying the end
    /// value - if cancelled, leaving the caller's finally block to decide the resting state.
    /// </summary>
    public static Task RunAsync(
        double from,
        double to,
        int durationMs,
        Easing easing,
        Action<double> apply,
        CancellationToken cancellationToken = default) =>
        RunAsync(durationMs, easing, t => apply(from + ((to - from) * t)), cancellationToken);

    /// <summary>
    /// Tweens normalised progress 0→1, easing applied. Use this when a single beat has to move several
    /// properties in lockstep (the shutter scales the ring and the flash disc together).
    /// </summary>
    public static async Task RunAsync(
        int durationMs,
        Easing easing,
        Action<double> applyEasedProgress,
        CancellationToken cancellationToken = default)
    {
        if (durationMs <= 0)
        {
            applyEasedProgress(1);
            return;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        while (stopwatch.ElapsedMilliseconds < durationMs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            applyEasedProgress(easing.Ease(Math.Clamp(stopwatch.Elapsed.TotalMilliseconds / durationMs, 0, 1)));
            await Task.Delay(FrameMs, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        applyEasedProgress(1);
    }

    /// <summary>A one-shot timer. Avalonia's DispatcherTimer repeats, so this stops itself on tick.</summary>
    public static DispatcherTimer OneShot(int intervalMs, Action onTick)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(intervalMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            onTick();
        };
        return timer;
    }

    // MAUI easing equivalents, named so the port reads one-for-one against the original call sites.
    public static readonly Easing CubicOut = new CubicEaseOut();
    public static readonly Easing CubicIn = new CubicEaseIn();
    public static readonly Easing CubicInOut = new CubicEaseInOut();
    public static readonly Easing SinOut = new SineEaseOut();
    public static readonly Easing Linear = new LinearEasing();
}
