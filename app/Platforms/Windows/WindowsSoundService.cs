using System.Collections.Concurrent;
using Floaty.Services;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Floaty.Platforms.Windows;

/// <summary>
/// Plays Floaty's feedback sounds through NAudio, which is already on the Windows target for mic
/// capture (see <see cref="WindowsAudioCaptureService"/>).
/// </summary>
/// <remarks>
/// One long-lived <see cref="WaveOutEvent"/> is fed by a <see cref="MixingSampleProvider"/> rather
/// than opening a device per play: opening WinMM costs tens of milliseconds, and a mixer lets two
/// sounds overlap (a capture landing while a reply finishes) instead of cutting each other off.
/// Clips are decoded once into memory — they are a few tens of KB each — and cached until the
/// config changes, per the "config-reactive services cache rather than re-read" convention.
/// Every failure is swallowed: a capture or a chat turn must not fail because audio is unavailable.
/// </remarks>
public sealed class WindowsSoundService : ISoundService, IDisposable
{
    // The mixer's fixed format. Everything loaded is resampled to it; 44.1 kHz stereo is what the
    // built-ins are authored at and what every output device accepts.
    private const int MixerSampleRate = 44100;
    private const int MixerChannels = 2;

    private readonly SettingsService _settings;

    // Decoded clips keyed by selection name. Concurrent because Play() decodes on the thread pool.
    private readonly ConcurrentDictionary<string, float[]> _cache = new(StringComparer.OrdinalIgnoreCase);

    private readonly Lock _deviceLock = new();
    private WaveOutEvent? _output;
    private MixingSampleProvider? _mixer;
    private bool _disposed;

    public WindowsSoundService(SettingsService settings)
    {
        _settings = settings;
        _settings.Changed += OnSettingsChanged;
        _settings.SoundPreviewRequested += OnSoundPreviewRequested;
    }

    public void Play(FloatySound sound)
    {
        var config = _settings.Current;
        var (enabled, selection, fallback) = sound switch
        {
            FloatySound.Capture => (
                config.CaptureSoundEnabled,
                config.CaptureSoundFileName,
                SettingsService.DefaultCaptureSound),
            FloatySound.AssistantDone => (
                config.AssistantDoneSoundEnabled,
                config.AssistantDoneSoundFileName,
                SettingsService.DefaultAssistantDoneSound),
            _ => (false, string.Empty, string.Empty),
        };

        if (!enabled)
            return;

        PlayFile(
            string.IsNullOrWhiteSpace(selection) ? fallback : selection,
            SettingsService.ClampSoundVolume(config.SoundVolume),
            fallback);
    }

    // Settings → Sounds auditions through the real playback path so the user hears exactly what the
    // app will play. The volume is the slider's live value, which may not be saved yet.
    private void OnSoundPreviewRequested(object? sender, (string FileName, double Volume) e) =>
        PlayFile(e.FileName, e.Volume, fallback: null);

    /// <summary>
    /// Decodes (or reuses) a clip and queues it on the mixer. <paramref name="fallback"/> is tried
    /// when the selection can't be loaded — a user file deleted behind Floaty's back still makes a
    /// noise rather than silently doing nothing.
    /// </summary>
    private void PlayFile(string fileName, double volume, string? fallback)
    {
        if (string.IsNullOrWhiteSpace(fileName) || volume <= 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                var samples = await LoadAsync(fileName);
                if (samples is null && !string.IsNullOrWhiteSpace(fallback) &&
                    !string.Equals(fileName, fallback, StringComparison.OrdinalIgnoreCase))
                {
                    samples = await LoadAsync(fallback);
                }

                if (samples is { Length: > 0 })
                    Queue(samples, (float)volume);
            }
            catch
            {
                // Audio is decoration; nothing upstream should ever hear about a failure here.
            }
        });
    }

    /// <summary>Decodes a selection to mixer-format samples, caching the result. Null when unusable.</summary>
    private async Task<float[]?> LoadAsync(string fileName)
    {
        if (_cache.TryGetValue(fileName, out var cached))
            return cached;

        try
        {
            await using var stream = await _settings.OpenSoundStreamAsync(fileName);
            if (stream is null)
                return null;

            // The package stream isn't seekable on every layout, and the readers below need to seek.
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            buffer.Position = 0;

            using var reader = CreateReader(buffer, fileName);
            var samples = ToMixerFormat(reader);
            _cache[fileName] = samples;
            return samples;
        }
        catch
        {
            return null;
        }
    }

    private static WaveStream CreateReader(Stream stream, string fileName) =>
        // StreamMediaFoundationReader covers MP3 (and anything else Media Foundation knows) for
        // user-supplied files; the built-ins are all plain PCM WAV.
        string.Equals(Path.GetExtension(fileName), ".wav", StringComparison.OrdinalIgnoreCase)
            ? new WaveFileReader(stream)
            : new StreamMediaFoundationReader(stream);

    /// <summary>Converts a decoded stream to interleaved 44.1 kHz stereo floats.</summary>
    private static float[] ToMixerFormat(WaveStream reader)
    {
        ISampleProvider source = reader.ToSampleProvider();

        if (source.WaveFormat.SampleRate != MixerSampleRate)
            source = new WdlResamplingSampleProvider(source, MixerSampleRate);

        source = source.WaveFormat.Channels switch
        {
            1 => new MonoToStereoSampleProvider(source),
            2 => source,
            // Anything exotic gets folded to mono first so the mixer's channel count always matches.
            _ => new MonoToStereoSampleProvider(new StereoToMonoSampleProvider(source)),
        };

        var samples = new List<float>();
        var block = new float[MixerSampleRate * MixerChannels]; // one second at a time
        int read;
        while ((read = source.Read(block, 0, block.Length)) > 0)
            samples.AddRange(block.AsSpan(0, read));

        return samples.ToArray();
    }

    private void Queue(float[] samples, float volume)
    {
        lock (_deviceLock)
        {
            if (_disposed)
                return;

            if (_mixer is null || _output is null)
            {
                _mixer = new MixingSampleProvider(
                    WaveFormat.CreateIeeeFloatWaveFormat(MixerSampleRate, MixerChannels))
                {
                    // Without this the mixer signals end-of-stream once the last clip drains and the
                    // device stops, so the next Play() would be swallowed.
                    ReadFully = true,
                };

                _output = new WaveOutEvent { DesiredLatency = 100 };
                _output.Init(_mixer);
                _output.Play();
            }

            _mixer.AddMixerInput(new VolumeSampleProvider(new CachedSampleProvider(samples))
            {
                Volume = volume,
            });
        }
    }

    // A saved selection may now point at a file that changed on disk, so drop the decoded copies.
    private void OnSettingsChanged(object? sender, EventArgs e) => _cache.Clear();

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _settings.SoundPreviewRequested -= OnSoundPreviewRequested;

        lock (_deviceLock)
        {
            _disposed = true;
            _output?.Dispose();
            _output = null;
            _mixer = null;
        }

        _cache.Clear();
    }

    /// <summary>Plays a cached clip once from memory, then reports end-of-stream so the mixer drops it.</summary>
    private sealed class CachedSampleProvider(float[] samples) : ISampleProvider
    {
        private int _position;

        public WaveFormat WaveFormat { get; } =
            WaveFormat.CreateIeeeFloatWaveFormat(MixerSampleRate, MixerChannels);

        public int Read(float[] buffer, int offset, int count)
        {
            var available = Math.Min(samples.Length - _position, count);
            if (available <= 0)
                return 0;

            Array.Copy(samples, _position, buffer, offset, available);
            _position += available;
            return available;
        }
    }
}
