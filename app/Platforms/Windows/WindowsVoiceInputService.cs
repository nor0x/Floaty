using System.Runtime.InteropServices;
using System.Threading.Channels;
using Floaty.Services;

namespace Floaty.Platforms.Windows;

/// <summary>
/// Local speech-to-text pipeline: mic samples flow through Silero VAD, which splits them into
/// speech segments; each finished segment is transcribed by the transcribe.cpp runtime (GGUF
/// model selected in settings, Vulkan-accelerated where a driver exists, CPU otherwise). Pause
/// detection for auto-send also lives here (not in a UI timer) so one long unbroken sentence is
/// never sent mid-speech: silence only counts once at least one segment has been transcribed and
/// the VAD reports no active speech.
/// </summary>
public sealed class WindowsVoiceInputService : IVoiceInputService, IDisposable
{
    private const int SampleRate = 16000;
    private const int VadWindowSize = 512; // Silero VAD consumes 512-sample (32 ms) windows

    private readonly IAudioCaptureService _audio;
    private readonly SettingsService _settings;
    private readonly ModelDownloadService _downloads;
    private readonly NativeRuntimeService _runtime;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IntPtr _model;
    private IntPtr _session;
    private SileroVadDetector? _vad;
    private string? _loadedModelId;

    private Channel<float[]>? _channel;
    private Task? _worker;

    public WindowsVoiceInputService(
        IAudioCaptureService audio, SettingsService settings, ModelDownloadService downloads,
        NativeRuntimeService runtime)
    {
        _audio = audio;
        _settings = settings;
        _downloads = downloads;
        _runtime = runtime;
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

            // Model load can take seconds (Voxtral: tens of seconds); keep it off the UI thread.
            // The VAD is recreated per session so no stale audio state leaks between recordings.
            await Task.Run(() =>
            {
                TranscribeNative.Initialize(_runtime.InstallDir);
                if (_session == IntPtr.Zero || _loadedModelId != model.Id)
                {
                    FreeNative();
                    LoadModel(model);
                    _loadedModelId = model.Id;
                }
                _vad?.Dispose();
                _vad = new SileroVadDetector(Path.Combine(
                    _downloads.GetModelDir(SttModelCatalog.SileroVad.Id),
                    SttModelCatalog.SileroVad.Files[0].FileName));
            });

            _channel = Channel.CreateUnbounded<float[]>();
            var session = _session;
            _worker = Task.Run(() => RunWorkerAsync(_channel.Reader, session, _vad!,
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
        // A different model was selected (or cleared): drop the loaded native handles so the
        // weights (up to ~3 GB for Voxtral) are freed; the next StartAsync loads the new one.
        if (_loadedModelId is null || _settings.Current.SttSelectedModelId == _loadedModelId)
            return;

        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync();
            try
            {
                await StopCoreAsync();
                FreeNative();
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
    /// Loads the GGUF with backend auto-selection (Vulkan when a driver registered, else CPU).
    /// A broken GPU driver surfaces as ERR_BACKEND at load; the header-documented recovery is a
    /// clean reload pinned to CPU, tried once before giving up.
    /// </summary>
    private void LoadModel(SttModelInfo model)
    {
        var path = Path.Combine(_downloads.GetModelDir(model.Id), model.Files[0].FileName);

        var status = TranscribeNative.transcribe_model_load_file(path, IntPtr.Zero, out _model);
        if (status == TranscribeNative.ErrBackend)
        {
            var cpuParams = default(TranscribeNative.ModelLoadParams);
            TranscribeNative.transcribe_model_load_params_init(ref cpuParams);
            cpuParams.BackendRequest = TranscribeNative.Backend.Cpu;
            status = TranscribeNative.transcribe_model_load_file(path, ref cpuParams, out _model);
        }
        TranscribeNative.Check(status, $"loading {model.DisplayName}");
        TranscribeNative.Check(
            TranscribeNative.transcribe_session_init(_model, IntPtr.Zero, out _session), "session setup");
    }

    /// <summary>
    /// Drains mic buffers, feeds the VAD in 512-sample windows, transcribes finished segments,
    /// and raises <see cref="PauseElapsed"/> once silence outlasts the configured pause. Runs
    /// until the channel completes (StopAsync), then flushes the trailing partial segment.
    /// </summary>
    private async Task RunWorkerAsync(
        ChannelReader<float[]> reader, IntPtr session, SileroVadDetector vad, double pauseSeconds)
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

                if (vad.IsSpeechActive)
                {
                    silentSamples = 0;
                    pauseLatched = false;
                }
                else
                {
                    silentSamples += VadWindowSize;
                }

                if (DrainSegments(session, vad))
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
        DrainSegments(session, vad);
    }

    private bool DrainSegments(IntPtr session, SileroVadDetector vad)
    {
        var emitted = false;
        while (vad.TryDequeueSegment(out var segment))
        {
            var status = TranscribeNative.transcribe_run(session, segment, segment.Length, IntPtr.Zero);
            if (status is TranscribeNative.Ok or TranscribeNative.ErrOutputTruncated)
            {
                var text = Marshal.PtrToStringUTF8(TranscribeNative.transcribe_full_text(session))?.Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    emitted = true;
                    SegmentTranscribed?.Invoke(this, text);
                }
            }
            else if (status == TranscribeNative.ErrInputTooLong)
            {
                Error?.Invoke(this, "That was too long for this model in one stretch — try a short pause.");
            }
            else
            {
                throw new InvalidOperationException(TranscribeNative.StatusString(status));
            }
        }
        return emitted;
    }

    private void FreeNative()
    {
        // A session borrows its model: free it first.
        if (_session != IntPtr.Zero)
        {
            TranscribeNative.transcribe_session_free(_session);
            _session = IntPtr.Zero;
        }
        if (_model != IntPtr.Zero)
        {
            TranscribeNative.transcribe_model_free(_model);
            _model = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        _audio.SamplesAvailable -= OnSamplesAvailable;
        _audio.CaptureFailed -= OnCaptureFailed;
        _settings.Changed -= OnSettingsChanged;
        StopCoreAsync().GetAwaiter().GetResult();
        FreeNative();
        _vad?.Dispose();
        _gate.Dispose();
    }
}
