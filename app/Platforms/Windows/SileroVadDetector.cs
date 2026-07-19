using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Floaty.Platforms.Windows;

/// <summary>
/// Silero voice-activity detection over onnxruntime, splitting the mic stream into speech
/// segments for per-segment transcription. Handles both the v4 graph (h/c LSTM states [2,1,64])
/// and v5 (single state [2,1,128]) by binding tensors from the model's own metadata. Segmentation
/// semantics mirror the previous sherpa-onnx VAD: a segment opens on a speech window (with a short
/// pre-roll so onsets aren't clipped) and closes after ~0.5 s of silence; segments shorter than
/// ~0.25 s of speech are discarded except on <see cref="Flush"/>, which emits whatever the user
/// was mid-saying when they tapped stop.
/// </summary>
public sealed class SileroVadDetector : IDisposable
{
    private const int WindowSize = 512; // 32 ms @ 16 kHz — the window Silero was trained on
    private const int SampleRate = 16000;

    private readonly InferenceSession _session;
    private readonly float _threshold;
    private readonly int _minSilenceWindows;
    private readonly int _minSpeechWindows;
    private const int KeepTrailingSilenceWindows = 2;
    private const int PreRollWindows = 2;

    private readonly string _inputName;
    private readonly string _srName;
    private readonly string[] _stateInputNames;
    private readonly string[] _stateOutputNames;
    private readonly DenseTensor<float>[] _states;

    private readonly Queue<float[]> _segments = new();
    private readonly List<float[]> _preRoll = new();
    private readonly List<float[]> _active = new();
    private int _speechWindowsInSegment;
    private int _silenceRun;
    private bool _inSegment;

    public SileroVadDetector(string modelPath, float threshold = 0.5f,
        float minSilenceSeconds = 0.5f, float minSpeechSeconds = 0.25f)
    {
        _session = new InferenceSession(modelPath);
        _threshold = threshold;
        _minSilenceWindows = (int)Math.Ceiling(minSilenceSeconds * SampleRate / WindowSize);
        _minSpeechWindows = (int)Math.Ceiling(minSpeechSeconds * SampleRate / WindowSize);

        // Bind tensors from the graph itself: the audio input is the rank-2 float, the sample
        // rate the lone int64, and the recurrent states the rank-3 floats. Sorting state inputs
        // and outputs by name pairs them up (v4: c↔cn, h↔hn; v5: state↔stateN) — pairing by
        // result iteration order would swap h and c.
        var inputs = _session.InputMetadata;
        _inputName = inputs.First(kv => kv.Value.ElementDataType == TensorElementType.Float
            && kv.Value.Dimensions.Length == 2).Key;
        _srName = inputs.First(kv => kv.Value.ElementDataType == TensorElementType.Int64).Key;
        var stateInputs = inputs
            .Where(kv => kv.Value.ElementDataType == TensorElementType.Float && kv.Value.Dimensions.Length == 3)
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToArray();
        _stateInputNames = stateInputs.Select(kv => kv.Key).ToArray();
        _states = stateInputs
            .Select(kv => new DenseTensor<float>(kv.Value.Dimensions.Select(d => Math.Max(d, 1)).ToArray()))
            .ToArray();
        _stateOutputNames = _session.OutputMetadata
            .Where(kv => kv.Value.ElementDataType == TensorElementType.Float && kv.Value.Dimensions.Length == 3)
            .Select(kv => kv.Key)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Whether the most recent window contained speech (drives the auto-send pause latch).</summary>
    public bool IsSpeechActive { get; private set; }

    /// <summary>Feeds exactly one 512-sample window of 16 kHz mono audio.</summary>
    public void AcceptWaveform(float[] window)
    {
        var isSpeech = RunModel(window) >= _threshold;
        IsSpeechActive = isSpeech;

        if (!_inSegment)
        {
            if (isSpeech)
            {
                _inSegment = true;
                _active.Clear();
                _active.AddRange(_preRoll);
                _active.Add((float[])window.Clone());
                _speechWindowsInSegment = 1;
                _silenceRun = 0;
            }
            else
            {
                _preRoll.Add((float[])window.Clone());
                if (_preRoll.Count > PreRollWindows)
                    _preRoll.RemoveAt(0);
            }
            return;
        }

        _active.Add((float[])window.Clone());
        if (isSpeech)
        {
            _speechWindowsInSegment++;
            _silenceRun = 0;
        }
        else if (++_silenceRun >= _minSilenceWindows)
        {
            CloseSegment(trimTrailing: true);
        }
    }

    public bool TryDequeueSegment(out float[] segment) => _segments.TryDequeue(out segment!);

    /// <summary>Emits the open segment (even a short one) — the user tapped stop mid-sentence.</summary>
    public void Flush()
    {
        if (_inSegment && _active.Count > 0)
            CloseSegment(trimTrailing: false, ignoreMinSpeech: true);
        Reset();
    }

    public void Reset()
    {
        _inSegment = false;
        IsSpeechActive = false;
        _silenceRun = 0;
        _speechWindowsInSegment = 0;
        _active.Clear();
        _preRoll.Clear();
        foreach (var s in _states)
            s.Fill(0);
    }

    private void CloseSegment(bool trimTrailing, bool ignoreMinSpeech = false)
    {
        var windows = _active.Count;
        if (trimTrailing)
            windows -= Math.Max(0, _silenceRun - KeepTrailingSilenceWindows);
        if (ignoreMinSpeech || _speechWindowsInSegment >= _minSpeechWindows)
        {
            var samples = new float[windows * WindowSize];
            for (var i = 0; i < windows; i++)
                _active[i].CopyTo(samples, i * WindowSize);
            _segments.Enqueue(samples);
        }
        _inSegment = false;
        _speechWindowsInSegment = 0;
        _silenceRun = 0;
        _active.Clear();
        _preRoll.Clear();
    }

    private float RunModel(float[] window)
    {
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_inputName,
                new DenseTensor<float>(window, new[] { 1, WindowSize })),
            NamedOnnxValue.CreateFromTensor(_srName,
                new DenseTensor<long>(new long[] { SampleRate }, new[] { 1 })),
        };
        for (var i = 0; i < _stateInputNames.Length; i++)
            inputs.Add(NamedOnnxValue.CreateFromTensor(_stateInputNames[i], _states[i]));

        using var results = _session.Run(inputs);
        var byName = results.ToDictionary(r => r.Name);
        for (var i = 0; i < _stateOutputNames.Length; i++)
            _states[i] = byName[_stateOutputNames[i]].AsTensor<float>().ToDenseTensor();
        return byName.Values
            .First(r => !_stateOutputNames.Contains(r.Name))
            .AsTensor<float>()
            .First();
    }

    public void Dispose() => _session.Dispose();
}
