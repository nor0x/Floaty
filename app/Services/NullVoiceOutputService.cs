namespace Floaty.Services;

/// <summary>No-op voice output for platforms without an audio backend. Floaty stays text-only.</summary>
public sealed class NullVoiceOutputService : IVoiceOutputService
{
    public bool IsConfigured => false;

    public bool IsEnabled => false;

    public bool IsSpeaking => false;

#pragma warning disable CS0067 // events required by the interface, never raised here
    public event EventHandler? SpeakingChanged;
    public event EventHandler<string>? Error;
#pragma warning restore CS0067

    public IReplySpeech BeginReply() => NullReplySpeech.Instance;

    public void Speak(string markdown)
    {
    }

    public void PlayClip(byte[] wav, double? volume = null)
    {
    }

    public void Stop()
    {
    }

    private sealed class NullReplySpeech : IReplySpeech
    {
        public static readonly NullReplySpeech Instance = new();

        public void Append(string delta)
        {
        }

        public void Complete()
        {
        }
    }
}
