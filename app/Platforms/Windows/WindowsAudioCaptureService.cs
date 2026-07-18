using Floaty.Services;
using NAudio.Wave;

namespace Floaty.Platforms.Windows;

/// <summary>
/// Microphone capture via NAudio's WaveInEvent on the default input device. WinMM resamples to the
/// requested 16 kHz mono 16-bit format via the driver; buffers arrive on NAudio's callback thread
/// and are converted to floats for the recognizer.
/// </summary>
public sealed class WindowsAudioCaptureService : IAudioCaptureService, IDisposable
{
    private WaveInEvent? _waveIn;

    public bool IsSupported => true;

    public bool IsCapturing { get; private set; }

    public event EventHandler<float[]>? SamplesAvailable;
    public event EventHandler<string>? CaptureFailed;

    public void Start()
    {
        if (IsCapturing)
            return;

        try
        {
            // Silero VAD consumes 512-sample (32 ms) windows; matching the buffer keeps latency low.
            _waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(16000, 16, 1),
                BufferMilliseconds = 32,
            };
            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.RecordingStopped += OnRecordingStopped;
            _waveIn.StartRecording();
            IsCapturing = true;
        }
        catch (Exception ex)
        {
            DisposeWaveIn();
            CaptureFailed?.Invoke(this, $"Microphone unavailable: {ex.Message}");
        }
    }

    public void Stop()
    {
        if (!IsCapturing)
            return;
        IsCapturing = false;
        try
        {
            _waveIn?.StopRecording();
        }
        finally
        {
            DisposeWaveIn();
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var samples = new float[e.BytesRecorded / 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
        SamplesAvailable?.Invoke(this, samples);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        // A device error (unplugged mic) stops recording spontaneously; a user Stop() does not
        // carry an exception. Only surface the former.
        if (e.Exception is not null && IsCapturing)
        {
            IsCapturing = false;
            CaptureFailed?.Invoke(this, $"Microphone stopped: {e.Exception.Message}");
        }
    }

    private void DisposeWaveIn()
    {
        if (_waveIn is null)
            return;
        _waveIn.DataAvailable -= OnDataAvailable;
        _waveIn.RecordingStopped -= OnRecordingStopped;
        _waveIn.Dispose();
        _waveIn = null;
    }

    public void Dispose()
    {
        Stop();
    }
}
