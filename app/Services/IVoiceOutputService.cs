namespace Floaty.Services;

/// <summary>
/// One reply being spoken while it streams. Feed it the answer's deltas as they arrive and call
/// <see cref="Complete"/> once the stream ends; speech keeps playing after that until it runs out
/// or <see cref="IVoiceOutputService.Stop"/> is called.
/// </summary>
public interface IReplySpeech
{
    /// <summary>Adds streamed answer text. Never blocks; synthesis and playback run in the background.</summary>
    void Append(string delta);

    /// <summary>Marks the reply finished so the last, possibly short, sentence is spoken too.</summary>
    void Complete();
}

/// <summary>
/// Speaks assistant replies aloud through the speech role (Settings → Model provider). Like
/// <see cref="ISoundService"/>, nothing here throws to the caller: a failed synthesis surfaces once
/// through <see cref="Error"/> and the chat carries on in text.
/// </summary>
public interface IVoiceOutputService
{
    /// <summary>Whether a speech model is assigned and usable, i.e. whether any speaker UI should show.</summary>
    bool IsConfigured { get; }

    /// <summary>Whether replies should be spoken automatically: configured and switched on.</summary>
    bool IsEnabled { get; }

    /// <summary>True from the first queued sentence until playback drains or is stopped.</summary>
    bool IsSpeaking { get; }

    /// <summary>Raised (on any thread) whenever <see cref="IsSpeaking"/> flips.</summary>
    event EventHandler? SpeakingChanged;

    /// <summary>Raised (on any thread) with a user-worthy message when synthesis fails, at most once per utterance.</summary>
    event EventHandler<string>? Error;

    /// <summary>Stops whatever is playing and starts a new streamed utterance.</summary>
    IReplySpeech BeginReply();

    /// <summary>Stops whatever is playing and speaks <paramref name="markdown"/> in full (read-aloud).</summary>
    void Speak(string markdown);

    /// <summary>
    /// Stops whatever is playing and speaks WAV bytes the caller already synthesized — Settings'
    /// "Test voice", which auditions unsaved settings the service itself would not use.
    /// <paramref name="volume"/> overrides the saved speech volume (0–1) for this clip.
    /// </summary>
    void PlayClip(byte[] wav, double? volume = null);

    /// <summary>Silences playback and drops everything queued or still being synthesized.</summary>
    void Stop();
}
