namespace Floaty.Services;

/// <summary>
/// Local speech-to-text: captures the microphone, splits speech at pauses (VAD), and transcribes
/// each finished segment with the model selected in settings. Drives the overlay's mic button.
/// </summary>
public interface IVoiceInputService
{
    /// <summary>
    /// True when a downloaded catalog model is selected and audio capture is supported.
    /// The mic button is only visible while this holds.
    /// </summary>
    bool IsConfigured { get; }

    bool IsListening { get; }

    /// <summary>One VAD segment's final text. Raised on a worker thread.</summary>
    event EventHandler<string>? SegmentTranscribed;

    /// <summary>
    /// Silence exceeded the configured auto-send pause after at least one transcribed segment.
    /// Raised on a worker thread, at most once per stretch of silence.
    /// </summary>
    event EventHandler? PauseElapsed;

    /// <summary>Raised on a worker thread when capture or recognition fails; listening has stopped.</summary>
    event EventHandler<string>? Error;

    /// <summary>Starts listening. Loads the selected model first if needed (can take seconds).</summary>
    Task StartAsync();

    /// <summary>Stops listening, flushing a trailing partial segment through <see cref="SegmentTranscribed"/>.</summary>
    Task StopAsync();
}
