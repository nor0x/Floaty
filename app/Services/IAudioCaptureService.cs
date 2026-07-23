namespace Floaty.Services;

/// <summary>
/// Microphone capture producing 16 kHz mono float samples for the voice input pipeline.
/// Implemented with NAudio on Windows; a Null implementation elsewhere keeps voice input hidden.
/// </summary>
public interface IAudioCaptureService
{
    /// <summary>False on platforms without a capture implementation — hides the mic button.</summary>
    bool IsSupported { get; }

    bool IsCapturing { get; }

    /// <summary>16 kHz mono samples in [-1, 1]. Raised on an audio callback thread, not the UI thread.</summary>
    event EventHandler<float[]>? SamplesAvailable;

    /// <summary>Raised when capture cannot start or dies mid-session (no device, privacy block).</summary>
    event EventHandler<string>? CaptureFailed;

    void Start();

    void Stop();
}
