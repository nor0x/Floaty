namespace Floaty.Services;

/// <summary>No-op voice input for platforms without a speech-to-text implementation.</summary>
public sealed class NullVoiceInputService : IVoiceInputService
{
    public bool IsConfigured => false;

    public bool IsListening => false;

#pragma warning disable CS0067 // events required by the interface, never raised here
    public event EventHandler<string>? SegmentTranscribed;
    public event EventHandler? PauseElapsed;
    public event EventHandler<string>? Error;
#pragma warning restore CS0067

    public Task StartAsync() => Task.CompletedTask;

    public Task StopAsync() => Task.CompletedTask;
}
