using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Floaty.Services;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Floaty.Platforms.Windows;

/// <summary>
/// Speaks assistant replies through NAudio. Each reply (an "utterance") runs its own small pipeline:
/// <see cref="SpeechTextChunker"/> cuts the streamed markdown into sentences, each sentence starts
/// synthesizing immediately (a few in flight, so the next one is ready before the current one ends),
/// and a single player loop feeds the clips, in order, into one gapless output device.
/// </summary>
/// <remarks>
/// Deliberately separate from <see cref="WindowsSoundService"/>'s device: stopping speech must never cut
/// off a capture shutter, and a reply's audio outlives the short clips that mixer is built for.
/// </remarks>
public sealed class WindowsVoiceOutputService : IVoiceOutputService, IDisposable
{
    private const int OutputSampleRate = 44100;
    private const int OutputChannels = 2;

    // Sentences synthesizing at once. Enough to stay ahead of playback without firing a whole long
    // reply's worth of requests the moment it finishes streaming.
    private const int MaxInFlight = 3;

    private readonly ISpeechSynthesisService _synth;
    private readonly SettingsService _settings;
    private readonly Lock _gate = new();

    private Utterance? _current;
    private bool _speaking;
    private bool _disposed;

    public WindowsVoiceOutputService(ISpeechSynthesisService synth, SettingsService settings)
    {
        _synth = synth;
        _settings = settings;
    }

    public bool IsConfigured => _synth.IsConfigured;

    public bool IsEnabled => _settings.Current.VoiceOutputEnabled && IsConfigured;

    public bool IsSpeaking
    {
        get
        {
            lock (_gate)
                return _speaking;
        }
    }

    public event EventHandler? SpeakingChanged;

    public event EventHandler<string>? Error;

    public IReplySpeech BeginReply() => Start();

    public void Speak(string markdown)
    {
        var utterance = Start();
        utterance.Append(markdown);
        utterance.Complete();
    }

    public void PlayClip(byte[] wav, double? volume = null)
    {
        var utterance = Start(volume);
        utterance.EnqueueClip(Task.FromResult<byte[]?>(wav));
        utterance.Complete();
    }

    public void Stop()
    {
        Utterance? stopped;
        lock (_gate)
        {
            stopped = _current;
            _current = null;
        }

        stopped?.Cancel();
        SetSpeaking(false);
    }

    public void Dispose()
    {
        lock (_gate)
            _disposed = true;

        Stop();
    }

    private Utterance Start(double? volumeOverride = null)
    {
        var volume = (float)Math.Clamp(volumeOverride ?? _settings.Current.SpeechVolume, 0, 1);
        var utterance = new Utterance(this, volume);

        Utterance? previous;
        lock (_gate)
        {
            previous = _current;
            if (_disposed)
            {
                // Still hand back something usable; it just never plays.
                utterance.Cancel();
                return utterance;
            }

            _current = utterance;
        }

        previous?.Cancel();
        SetSpeaking(true);
        utterance.Run();
        return utterance;
    }

    private void OnFinished(Utterance utterance)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_current, utterance))
                return;

            _current = null;
        }

        SetSpeaking(false);
    }

    private void SetSpeaking(bool value)
    {
        lock (_gate)
        {
            if (_speaking == value)
                return;

            _speaking = value;
        }

        SpeakingChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ReportError(string message) => Error?.Invoke(this, message);

    /// <summary>One reply's worth of speech: chunker → synthesis → ordered playback.</summary>
    private sealed class Utterance : IReplySpeech
    {
        private readonly WindowsVoiceOutputService _owner;
        private readonly float _volume;
        private readonly CancellationTokenSource _cts = new();
        private readonly SpeechTextChunker _chunker = new();
        private readonly Lock _chunkerGate = new();
        private readonly SemaphoreSlim _inFlight = new(MaxInFlight);
        private readonly Channel<Task<byte[]?>> _clips =
            Channel.CreateUnbounded<Task<byte[]?>>(new UnboundedChannelOptions { SingleReader = true });
        private int _errorReported;

        public Utterance(WindowsVoiceOutputService owner, float volume)
        {
            _owner = owner;
            _volume = volume;
        }

        public void Append(string delta)
        {
            if (_cts.IsCancellationRequested)
                return;

            IReadOnlyList<string> chunks;
            lock (_chunkerGate)
                chunks = _chunker.Append(delta);

            foreach (var chunk in chunks)
                EnqueueClip(SynthesizeAsync(chunk));
        }

        public void Complete()
        {
            if (!_cts.IsCancellationRequested)
            {
                IReadOnlyList<string> chunks;
                lock (_chunkerGate)
                    chunks = _chunker.Flush();

                foreach (var chunk in chunks)
                    EnqueueClip(SynthesizeAsync(chunk));
            }

            _clips.Writer.TryComplete();
        }

        public void EnqueueClip(Task<byte[]?> clip) => _clips.Writer.TryWrite(clip);

        public void Cancel()
        {
            _cts.Cancel();
            _clips.Writer.TryComplete();
        }

        public void Run() => _ = Task.Run(PlayAllAsync);

        private async Task<byte[]?> SynthesizeAsync(string text)
        {
            var token = _cts.Token;
            try
            {
                await _inFlight.WaitAsync(token);
                try
                {
                    return await _owner._synth.SynthesizeAsync(text, token);
                }
                finally
                {
                    _inFlight.Release();
                }
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VoiceOutput] Synthesis failed: {ex.Message}");

                // One toast per utterance: a bad key would otherwise complain once per sentence.
                if (Interlocked.Exchange(ref _errorReported, 1) == 0)
                    _owner.ReportError(ex.Message);

                return null;
            }
        }

        private async Task PlayAllAsync()
        {
            var token = _cts.Token;
            var queue = new ClipQueueSampleProvider();
            WaveOutEvent? output = null;
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            try
            {
                await foreach (var clipTask in _clips.Reader.ReadAllAsync(token))
                {
                    var wav = await clipTask;
                    if (wav is null || token.IsCancellationRequested)
                        continue;

                    var source = Decode(wav);
                    if (source is null)
                        continue;

                    queue.Add(source);

                    // The device opens on the first real clip, so an utterance that turns out to have
                    // nothing speakable (a reply that was all code) never touches the audio stack.
                    if (output is null)
                    {
                        output = new WaveOutEvent { DesiredLatency = 150 };
                        output.PlaybackStopped += (_, _) => stopped.TrySetResult();
                        output.Init(new VolumeSampleProvider(queue) { Volume = _volume });
                        output.Play();
                    }
                }

                queue.MarkComplete();

                if (output is not null)
                {
                    await using var registration = token.Register(() => stopped.TrySetResult());
                    await stopped.Task;
                }
            }
            catch (OperationCanceledException)
            {
                // Stopped by the user or superseded by the next reply.
            }
            catch (Exception ex)
            {
                // Audio is secondary to the text; a device failure must not surface as a crash.
                Debug.WriteLine($"[VoiceOutput] Playback failed: {ex.Message}");
            }
            finally
            {
                if (output is not null)
                {
                    try
                    {
                        output.Stop();
                        output.Dispose();
                    }
                    catch
                    {
                        // Best-effort teardown.
                    }
                }

                // The CTS is left undisposed on purpose: a late Cancel() or Append() may still touch
                // it, and it owns no timer, so there is nothing to leak.
                _owner.OnFinished(this);
            }
        }
    }

    /// <summary>
    /// Turns one synthesized clip into output-format samples. WAV is parsed by hand because streamed
    /// TTS responses often carry placeholder chunk sizes that a strict reader rejects; anything that
    /// isn't WAV (an endpoint that ignored response_format and sent MP3) goes to Media Foundation.
    /// </summary>
    private static ISampleProvider? Decode(byte[] bytes)
    {
        try
        {
            WaveStream stream = TryParseWav(bytes, out var format, out var offset, out var length)
                ? new RawSourceWaveStream(new MemoryStream(bytes, offset, length, writable: false), format)
                : new StreamMediaFoundationReader(new MemoryStream(bytes, writable: false));

            return ToOutputFormat(stream.ToSampleProvider());
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VoiceOutput] Could not decode clip: {ex.Message}");
            return null;
        }
    }

    private static bool TryParseWav(byte[] bytes, out WaveFormat format, out int dataOffset, out int dataLength)
    {
        format = null!;
        dataOffset = dataLength = 0;

        if (bytes.Length < 12
            || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            || !bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            return false;

        var position = 12;
        while (position + 8 <= bytes.Length)
        {
            var id = bytes.AsSpan(position, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 4, 4));
            var body = position + 8;

            if (id.SequenceEqual("fmt "u8) && body + 16 <= bytes.Length)
            {
                var tag = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body, 2));
                var channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body + 2, 2));
                var rate = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(body + 4, 4));
                var bits = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body + 14, 2));

                // 1 = PCM, 3 = IEEE float, 0xFFFE = extensible (treated as PCM at the stated depth).
                format = tag == 3
                    ? WaveFormat.CreateIeeeFloatWaveFormat(rate, channels)
                    : new WaveFormat(rate, bits, channels);
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (format is null)
                    return false;

                // A streamed response can't know its length up front and says 0 or 0xFFFFFFFF here:
                // trust the bytes that actually arrived instead.
                dataOffset = body;
                dataLength = (int)Math.Min(size, (uint)(bytes.Length - body));
                dataLength -= dataLength % format.BlockAlign;
                return dataLength > 0;
            }

            // Chunks are word-aligned. A bogus size would jump past the end and simply end the loop.
            var next = (long)body + size + (size & 1);
            if (next > bytes.Length)
                break;

            position = (int)next;
        }

        return false;
    }

    private static ISampleProvider ToOutputFormat(ISampleProvider source)
    {
        if (source.WaveFormat.SampleRate != OutputSampleRate)
            source = new WdlResamplingSampleProvider(source, OutputSampleRate);

        return source.WaveFormat.Channels switch
        {
            1 => new MonoToStereoSampleProvider(source),
            2 => source,
            _ => new MonoToStereoSampleProvider(new StereoToMonoSampleProvider(source)),
        };
    }

    /// <summary>
    /// Plays queued clips back to back. While the queue is momentarily empty (the next sentence is still
    /// synthesizing) it plays silence rather than ending, and it only reports end-of-stream once
    /// <see cref="MarkComplete"/> has been called and everything queued has drained.
    /// </summary>
    private sealed class ClipQueueSampleProvider : ISampleProvider
    {
        private readonly ConcurrentQueue<ISampleProvider> _pending = new();
        private ISampleProvider? _playing;
        private volatile bool _complete;

        public WaveFormat WaveFormat { get; } =
            WaveFormat.CreateIeeeFloatWaveFormat(OutputSampleRate, OutputChannels);

        public void Add(ISampleProvider clip) => _pending.Enqueue(clip);

        public void MarkComplete() => _complete = true;

        public int Read(float[] buffer, int offset, int count)
        {
            var written = 0;

            while (written < count)
            {
                if (_playing is null && !_pending.TryDequeue(out _playing))
                    break;

                var read = _playing.Read(buffer, offset + written, count - written);
                if (read == 0)
                {
                    _playing = null;
                    continue;
                }

                written += read;
            }

            if (written == count)
                return written;

            // Nothing more to play right now. Finished for good: return what we have (0 ends playback).
            if (_complete && _pending.IsEmpty && _playing is null)
                return written;

            // Waiting on synthesis: pad with silence so the device keeps running.
            Array.Clear(buffer, offset + written, count - written);
            return count;
        }
    }
}
