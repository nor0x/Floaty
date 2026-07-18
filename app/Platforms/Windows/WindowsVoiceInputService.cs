using System.Threading.Channels;
using Floaty.Services;
using SherpaOnnx;

namespace Floaty.Platforms.Windows;

/// <summary>
/// Local speech-to-text pipeline: mic samples flow through Silero VAD, which splits them into
/// speech segments; each finished segment is transcribed by the sherpa-onnx offline recognizer
/// for the model selected in settings. Pause detection for auto-send also lives here (not in a UI
/// timer) so one long unbroken sentence is never sent mid-speech: silence only counts once at
/// least one segment has been transcribed and the VAD reports no active speech.
/// </summary>
public sealed class WindowsVoiceInputService : IVoiceInputService, IDisposable
{
    private const int SampleRate = 16000;
    private const int VadWindowSize = 512; // Silero VAD consumes 512-sample (32 ms) windows

    private readonly IAudioCaptureService _audio;
    private readonly SettingsService _settings;
    private readonly ModelDownloadService _downloads;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private OfflineRecognizer? _recognizer;
    private VoiceActivityDetector? _vad;
    private string? _loadedModelId;

    private Channel<float[]>? _channel;
    private Task? _worker;

    public WindowsVoiceInputService(
        IAudioCaptureService audio, SettingsService settings, ModelDownloadService downloads)
    {
        _audio = audio;
        _settings = settings;
        _downloads = downloads;
        _audio.SamplesAvailable += OnSamplesAvailable;
        _audio.CaptureFailed += OnCaptureFailed;
        _settings.Changed += OnSettingsChanged;
    }

    public bool IsConfigured =>
        _audio.IsSupported
        && SttModelCatalog.Find(_settings.Current.SttSelectedModelId) is { } model
        && _downloads.IsDownloaded(model);

    public bool IsListening { get; private set; }

    public event EventHandler<string>? SegmentTranscribed;
    public event EventHandler? PauseElapsed;
    public event EventHandler<string>? Error;

    public async Task StartAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (IsListening)
                return;

            var model = SttModelCatalog.Find(_settings.Current.SttSelectedModelId)
                ?? throw new InvalidOperationException("No speech-to-text model selected.");
            if (!_downloads.IsDownloaded(model))
                throw new InvalidOperationException($"{model.DisplayName} is not downloaded.");

            // Model load can take seconds (Parakeet); keep it off the UI thread. The VAD is
            // recreated per session so no stale audio state leaks between recordings.
            await Task.Run(() =>
            {
                if (_recognizer is null || _loadedModelId != model.Id)
                {
                    _recognizer?.Dispose();
                    _recognizer = null;
                    _recognizer = CreateRecognizer(model);
                    _loadedModelId = model.Id;
                }
                _vad?.Dispose();
                _vad = CreateVad();
            });

            _channel = Channel.CreateUnbounded<float[]>();
            _worker = Task.Run(() => RunWorkerAsync(_channel.Reader, _recognizer!, _vad!,
                pauseSeconds: Math.Clamp(_settings.Current.AutoSendPauseSeconds, 1, 10)));

            IsListening = true;
            _audio.Start();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await StopCoreAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        if (!IsListening)
            return;
        IsListening = false;

        _audio.Stop();
        _channel?.Writer.TryComplete();
        if (_worker is not null)
        {
            try
            {
                await _worker;
            }
            catch (Exception ex)
            {
                Error?.Invoke(this, $"Voice recognition failed: {ex.Message}");
            }
        }
        _channel = null;
        _worker = null;
    }

    private void OnSamplesAvailable(object? sender, float[] samples)
    {
        if (IsListening)
            _channel?.Writer.TryWrite(samples);
    }

    private void OnCaptureFailed(object? sender, string message)
    {
        _ = Task.Run(async () =>
        {
            await StopAsync();
            Error?.Invoke(this, message);
        });
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        // A different model was selected (or cleared): drop the loaded recognizer so its memory
        // (several hundred MB for Parakeet) is freed; the next StartAsync loads the new one.
        if (_loadedModelId is null || _settings.Current.SttSelectedModelId == _loadedModelId)
            return;

        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync();
            try
            {
                await StopCoreAsync();
                _recognizer?.Dispose();
                _recognizer = null;
                _vad?.Dispose();
                _vad = null;
                _loadedModelId = null;
            }
            finally
            {
                _gate.Release();
            }
        });
    }

    /// <summary>
    /// Drains mic buffers, feeds the VAD in 512-sample windows, transcribes finished segments,
    /// and raises <see cref="PauseElapsed"/> once silence outlasts the configured pause. Runs
    /// until the channel completes (StopAsync), then flushes the trailing partial segment.
    /// </summary>
    private async Task RunWorkerAsync(
        ChannelReader<float[]> reader, OfflineRecognizer recognizer, VoiceActivityDetector vad,
        double pauseSeconds)
    {
        var carry = new List<float>(VadWindowSize * 4);
        var window = new float[VadWindowSize];
        long silentSamples = 0;
        var hasEmitted = false;
        var pauseLatched = false;

        await foreach (var samples in reader.ReadAllAsync())
        {
            carry.AddRange(samples);
            var offset = 0;
            while (carry.Count - offset >= VadWindowSize)
            {
                carry.CopyTo(offset, window, 0, VadWindowSize);
                offset += VadWindowSize;

                vad.AcceptWaveform(window);

                if (vad.IsSpeechDetected())
                {
                    silentSamples = 0;
                    pauseLatched = false;
                }
                else
                {
                    silentSamples += VadWindowSize;
                }

                if (DrainSegments(recognizer, vad))
                    hasEmitted = true;

                if (hasEmitted && !pauseLatched && silentSamples >= (long)(pauseSeconds * SampleRate))
                {
                    pauseLatched = true;
                    PauseElapsed?.Invoke(this, EventArgs.Empty);
                }
            }
            carry.RemoveRange(0, offset);
        }

        // Emit whatever the user was mid-saying when they tapped stop.
        vad.Flush();
        DrainSegments(recognizer, vad);
    }

    private bool DrainSegments(OfflineRecognizer recognizer, VoiceActivityDetector vad)
    {
        var emitted = false;
        while (!vad.IsEmpty())
        {
            var segment = vad.Front();
            vad.Pop();

            using var stream = recognizer.CreateStream();
            stream.AcceptWaveform(SampleRate, segment.Samples);
            recognizer.Decode(stream);
            var text = stream.Result.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                emitted = true;
                SegmentTranscribed?.Invoke(this, text);
            }
        }
        return emitted;
    }

    private OfflineRecognizer CreateRecognizer(SttModelInfo model)
    {
        var dir = _downloads.GetModelDir(model.Id);
        string P(string fileName) => Path.Combine(dir, fileName);
        var files = model.Files.Select(f => f.FileName).ToArray();

        var config = new OfflineRecognizerConfig();
        config.ModelConfig.Tokens = P(files.Single(f => f.EndsWith("tokens.txt")));
        config.ModelConfig.NumThreads = Math.Min(4, Environment.ProcessorCount);
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.Debug = 0;
        config.DecodingMethod = "greedy_search";

        switch (model.Engine)
        {
            case SttEngineKind.Whisper:
                config.ModelConfig.Whisper.Encoder = P(files.Single(f => f.Contains("encoder")));
                config.ModelConfig.Whisper.Decoder = P(files.Single(f => f.Contains("decoder")));
                config.ModelConfig.Whisper.Language = "en";
                config.ModelConfig.Whisper.Task = "transcribe";
                break;
            case SttEngineKind.Transducer:
                config.ModelConfig.Transducer.Encoder = P(files.Single(f => f.StartsWith("encoder")));
                config.ModelConfig.Transducer.Decoder = P(files.Single(f => f.StartsWith("decoder")));
                config.ModelConfig.Transducer.Joiner = P(files.Single(f => f.StartsWith("joiner")));
                // NeMo transducer exports (Parakeet) need the explicit model type; the generic
                // "transducer" path assumes zipformer-style metadata.
                config.ModelConfig.ModelType = "nemo_transducer";
                break;
            case SttEngineKind.Moonshine:
                config.ModelConfig.Moonshine.Preprocessor = P("preprocess.onnx");
                config.ModelConfig.Moonshine.Encoder = P(files.Single(f => f.StartsWith("encode.")));
                config.ModelConfig.Moonshine.UncachedDecoder = P(files.Single(f => f.StartsWith("uncached_decode")));
                config.ModelConfig.Moonshine.CachedDecoder = P(files.Single(f => f.StartsWith("cached_decode")));
                break;
            default:
                throw new InvalidOperationException($"Unsupported engine: {model.Engine}");
        }

        return new OfflineRecognizer(config);
    }

    private VoiceActivityDetector CreateVad()
    {
        var config = new VadModelConfig();
        config.SileroVad.Model = Path.Combine(
            _downloads.GetModelDir(SttModelCatalog.SileroVad.Id),
            SttModelCatalog.SileroVad.Files[0].FileName);
        config.SileroVad.Threshold = 0.5f;
        config.SileroVad.MinSilenceDuration = 0.5f;
        config.SileroVad.MinSpeechDuration = 0.25f;
        config.SileroVad.WindowSize = VadWindowSize;
        config.SampleRate = SampleRate;
        return new VoiceActivityDetector(config, bufferSizeInSeconds: 60);
    }

    public void Dispose()
    {
        _audio.SamplesAvailable -= OnSamplesAvailable;
        _audio.CaptureFailed -= OnCaptureFailed;
        _settings.Changed -= OnSettingsChanged;
        StopCoreAsync().GetAwaiter().GetResult();
        _recognizer?.Dispose();
        _vad?.Dispose();
        _gate.Dispose();
    }
}
