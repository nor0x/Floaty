namespace Floaty.Services;

/// <summary>
/// The occasions Floaty makes a noise. Each maps to its own enable flag and its own sound selection
/// in <see cref="FloatyConfig"/>, both editable in Settings → Sounds.
/// </summary>
public enum FloatySound
{
    /// <summary>A window was captured by the user (<c>/capture</c> or an <c>@</c> attachment).</summary>
    Capture,

    /// <summary>An assistant reply finished streaming.</summary>
    AssistantDone,
}

/// <summary>
/// Plays Floaty's own short feedback sounds. Deliberately fire-and-forget: callers sit on the UI
/// thread in the middle of a capture or a chat turn, so playback must never block them and must
/// never throw — a missing file or an unavailable audio device is not worth failing a capture over.
/// </summary>
/// <remarks>
/// The Windows implementation also serves Settings' audition button by subscribing to
/// <see cref="SettingsService.SoundPreviewRequested"/>, so previews go through the exact same
/// device and mixer as the real thing.
/// </remarks>
public interface ISoundService
{
    /// <summary>
    /// Plays the sound configured for <paramref name="sound"/>, if that occasion is enabled. Returns
    /// immediately; decoding and playback happen off the calling thread.
    /// </summary>
    void Play(FloatySound sound);
}
