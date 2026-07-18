namespace Floaty.Services;

/// <summary>Which sherpa-onnx offline recognizer family a catalog model belongs to.</summary>
public enum SttEngineKind
{
    Whisper,
    Transducer,
    Moonshine,
}

/// <summary>One downloadable file of a speech-to-text model.</summary>
public sealed record SttModelFile(string Url, string FileName);

/// <summary>
/// A curated speech-to-text model users can download from the Voice input settings section.
/// <see cref="IsAvailable"/> is false for models we list but cannot run locally yet (Voxtral).
/// </summary>
public sealed record SttModelInfo(
    string Id,
    string DisplayName,
    SttEngineKind Engine,
    string SizeNote,
    string LanguageNote,
    IReadOnlyList<SttModelFile> Files,
    bool IsAvailable = true);

/// <summary>
/// The built-in catalog of local speech-to-text models (sherpa-onnx ONNX exports hosted on
/// Hugging Face). Downloaded files live under <see cref="FloatyPaths.SttModels"/>/&lt;id&gt;.
/// </summary>
public static class SttModelCatalog
{
    private static string Hf(string repo, string file) =>
        $"https://huggingface.co/{repo}/resolve/main/{file}";

    /// <summary>
    /// Silero VAD splits the mic stream into speech segments for every engine, so it is
    /// downloaded alongside whichever model the user picks (and hidden from the catalog UI).
    /// </summary>
    public static readonly SttModelInfo SileroVad = new(
        Id: "silero-vad",
        DisplayName: "Silero VAD",
        Engine: SttEngineKind.Whisper, // unused; the VAD is not a recognizer
        SizeNote: "2 MB",
        LanguageNote: "",
        Files: [new SttModelFile(Hf("csukuangfj/vad", "silero_vad.onnx"), "silero_vad.onnx")]);

    public static IReadOnlyList<SttModelInfo> Models { get; } =
    [
        new(
            Id: "whisper-tiny-en",
            DisplayName: "Whisper Tiny",
            Engine: SttEngineKind.Whisper,
            SizeNote: "~104 MB",
            LanguageNote: "English · fastest",
            Files:
            [
                new SttModelFile(Hf("csukuangfj/sherpa-onnx-whisper-tiny.en", "tiny.en-encoder.int8.onnx"), "tiny.en-encoder.int8.onnx"),
                new SttModelFile(Hf("csukuangfj/sherpa-onnx-whisper-tiny.en", "tiny.en-decoder.int8.onnx"), "tiny.en-decoder.int8.onnx"),
                new SttModelFile(Hf("csukuangfj/sherpa-onnx-whisper-tiny.en", "tiny.en-tokens.txt"), "tiny.en-tokens.txt"),
            ]),
        new(
            Id: "whisper-base-en",
            DisplayName: "Whisper Base",
            Engine: SttEngineKind.Whisper,
            SizeNote: "~161 MB",
            LanguageNote: "English · balanced",
            Files:
            [
                new SttModelFile(Hf("csukuangfj/sherpa-onnx-whisper-base.en", "base.en-encoder.int8.onnx"), "base.en-encoder.int8.onnx"),
                new SttModelFile(Hf("csukuangfj/sherpa-onnx-whisper-base.en", "base.en-decoder.int8.onnx"), "base.en-decoder.int8.onnx"),
                new SttModelFile(Hf("csukuangfj/sherpa-onnx-whisper-base.en", "base.en-tokens.txt"), "base.en-tokens.txt"),
            ]),
        new(
            Id: "moonshine-base-en",
            DisplayName: "Moonshine Base",
            Engine: SttEngineKind.Moonshine,
            SizeNote: "~287 MB",
            LanguageNote: "English · fast",
            Files:
            [
                new SttModelFile(Hf("csukuangfj/sherpa-onnx-moonshine-base-en-int8", "preprocess.onnx"), "preprocess.onnx"),
                new SttModelFile(Hf("csukuangfj/sherpa-onnx-moonshine-base-en-int8", "encode.int8.onnx"), "encode.int8.onnx"),
                new SttModelFile(Hf("csukuangfj/sherpa-onnx-moonshine-base-en-int8", "uncached_decode.int8.onnx"), "uncached_decode.int8.onnx"),
                new SttModelFile(Hf("csukuangfj/sherpa-onnx-moonshine-base-en-int8", "cached_decode.int8.onnx"), "cached_decode.int8.onnx"),
                new SttModelFile(Hf("csukuangfj/sherpa-onnx-moonshine-base-en-int8", "tokens.txt"), "tokens.txt"),
            ]),
        new(
            Id: "parakeet-tdt-0.6b-v2",
            DisplayName: "Parakeet TDT 0.6B v2",
            Engine: SttEngineKind.Transducer,
            SizeNote: "~661 MB",
            LanguageNote: "English · best accuracy",
            Files:
            [
                new SttModelFile(Hf("csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8", "encoder.int8.onnx"), "encoder.int8.onnx"),
                new SttModelFile(Hf("csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8", "decoder.int8.onnx"), "decoder.int8.onnx"),
                new SttModelFile(Hf("csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8", "joiner.int8.onnx"), "joiner.int8.onnx"),
                new SttModelFile(Hf("csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8", "tokens.txt"), "tokens.txt"),
            ]),
        new(
            Id: "voxtral",
            DisplayName: "Voxtral",
            Engine: SttEngineKind.Whisper,
            SizeNote: "",
            LanguageNote: "Coming soon",
            Files: [],
            IsAvailable: false),
    ];

    public static SttModelInfo? Find(string? id) =>
        string.IsNullOrEmpty(id) ? null : Models.FirstOrDefault(m => m.Id == id);
}
