namespace Floaty.Services;

/// <summary>No-op audio capture for platforms without a microphone implementation.</summary>
public sealed class NullAudioCaptureService : IAudioCaptureService
{
    public bool IsSupported => false;

    public bool IsCapturing => false;

#pragma warning disable CS0067 // events required by the interface, never raised here
    public event EventHandler<float[]>? SamplesAvailable;
    public event EventHandler<string>? CaptureFailed;
#pragma warning restore CS0067

    public void Start()
    {
    }

    public void Stop()
    {
    }
}
