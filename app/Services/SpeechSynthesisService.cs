using OpenAI.Audio;

namespace Floaty.Services;

/// <summary>The voice settings a synthesis request is made with, lifted out of config so Settings can audition unsaved ones.</summary>
public sealed record SpeechVoiceSettings(string Voice, double Speed, string Instructions)
{
    /// <summary>The saved settings from <paramref name="config"/>.</summary>
    public static SpeechVoiceSettings From(FloatyConfig config) =>
        new(config.SpeechVoice, config.SpeechSpeed, config.SpeechInstructions);
}

/// <summary>
/// Turns text into WAV bytes using whichever provider holds <see cref="ModelRole.Speech"/>.
/// </summary>
public interface ISpeechSynthesisService
{
    /// <summary>Whether the speech role points at a provider that can actually serve it right now.</summary>
    bool IsConfigured { get; }

    /// <summary>Synthesizes <paramref name="text"/> with the saved voice settings. Returns a WAV file's bytes.</summary>
    Task<byte[]> SynthesizeAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>Synthesizes with an explicit client and settings — the Settings page's "Test voice".</summary>
    Task<byte[]> SynthesizeAsync(
        AudioClient client,
        string model,
        SpeechVoiceSettings settings,
        string text,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The OpenAI speech endpoint (<c>/audio/speech</c>), which OpenAI, Azure and the compatible gateways
/// that do TTS all speak. Like image generation there is no <c>Microsoft.Extensions.AI</c> abstraction
/// worth using here, so this talks to the SDK client directly.
/// </summary>
public sealed class SpeechSynthesisService : ISpeechSynthesisService
{
    // The endpoint's own limits; anything outside is a 400.
    private const double MinSpeed = 0.25;
    private const double MaxSpeed = 4.0;

    private readonly AiClientFactory _clients;
    private readonly SettingsService _settings;

    public SpeechSynthesisService(AiClientFactory clients, SettingsService settings)
    {
        _clients = clients;
        _settings = settings;
    }

    public bool IsConfigured => _clients.IsConfigured(ModelRole.Speech);

    public Task<byte[]> SynthesizeAsync(string text, CancellationToken cancellationToken = default)
    {
        var (client, model) = _clients.GetSpeechClient()
            ?? throw new InvalidOperationException(
                "No speech model is configured. Assign one in Settings → Model provider → Roles → Speech.");

        return SynthesizeAsync(client, model, SpeechVoiceSettings.From(_settings.Current), text, cancellationToken);
    }

    public async Task<byte[]> SynthesizeAsync(
        AudioClient client,
        string model,
        SpeechVoiceSettings settings,
        string text,
        CancellationToken cancellationToken = default)
    {
        // WAV rather than the endpoint's MP3 default: NAudio reads it without a codec, and it carries
        // its own header, unlike raw PCM, so the sample rate never has to be assumed.
        var options = new SpeechGenerationOptions
        {
            ResponseFormat = GeneratedSpeechFormat.Wav,
        };

        // 1.0 is the default; leaving it unset keeps the request minimal for picky compatible endpoints.
        var speed = Math.Clamp(settings.Speed, MinSpeed, MaxSpeed);
        if (Math.Abs(speed - 1.0) > 0.001)
            options.SpeedRatio = (float)speed;

        // Only the gpt-4o TTS family understands instructions; tts-1 and most gateways reject the field.
        if (!string.IsNullOrWhiteSpace(settings.Instructions)
            && model.StartsWith("gpt-4o", StringComparison.OrdinalIgnoreCase))
        {
#pragma warning disable OPENAI001 // Same experimental surface AiClientFactory already opts into.
            options.Instructions = settings.Instructions.Trim();
#pragma warning restore OPENAI001
        }

        var voice = string.IsNullOrWhiteSpace(settings.Voice) ? "alloy" : settings.Voice.Trim();

        var result = await client.GenerateSpeechAsync(text, new GeneratedSpeechVoice(voice), options, cancellationToken);
        return result.Value.ToArray();
    }
}
