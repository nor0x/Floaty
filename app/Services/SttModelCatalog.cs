namespace Floaty.Services;

/// <summary>One downloadable file of a speech-to-text model.</summary>
public sealed record SttModelFile(string Url, string FileName);

/// <summary>
/// A curated speech-to-text model users can download from the Voice input settings section.
/// The transcribe.cpp runtime auto-detects the architecture from GGUF metadata, so no engine
/// kind is needed. <see cref="GpuRecommended"/> flags models too heavy for real-time CPU use.
/// </summary>
public sealed record SttModelInfo(
    string Id,
    string DisplayName,
    string SizeNote,
    string LanguageNote,
    IReadOnlyList<SttModelFile> Files,
    bool GpuRecommended = false,
    bool IsAvailable = true);

/// <summary>
/// The built-in catalog of local speech-to-text models: single-file GGUF exports from the
/// handy-computer Hugging Face org, run by transcribe.cpp. Downloads land under
/// <see cref="FloatyPaths.SttModels"/>/&lt;id&gt;. Ids are stable across engine migrations so a
/// saved <see cref="FloatyConfig.SttSelectedModelId"/> stays valid.
/// </summary>
public static class SttModelCatalog
{
    private static string Hf(string repo, string file) =>
        $"https://huggingface.co/{repo}/resolve/main/{file}";

    /// <summary>
    /// Silero VAD splits the mic stream into speech segments for every model, so it is
    /// downloaded alongside whichever model the user picks (and hidden from the catalog UI).
    /// </summary>
    public static readonly SttModelInfo SileroVad = new(
        Id: "silero-vad",
        DisplayName: "Silero VAD",
        SizeNote: "2 MB",
        LanguageNote: "",
        Files: [new SttModelFile(Hf("csukuangfj/vad", "silero_vad.onnx"), "silero_vad.onnx")]);

    public static IReadOnlyList<SttModelInfo> Models { get; } =
    [
        new(
            Id: "whisper-tiny-en",
            DisplayName: "Whisper Tiny",
            SizeNote: "~46 MB",
            LanguageNote: "English · fastest",
            Files:
            [
                new SttModelFile(Hf("handy-computer/whisper-tiny.en-gguf", "whisper-tiny.en-Q8_0.gguf"), "whisper-tiny.en-Q8_0.gguf"),
            ]),
        new(
            Id: "whisper-base-en",
            DisplayName: "Whisper Base",
            SizeNote: "~85 MB",
            LanguageNote: "English · balanced",
            Files:
            [
                new SttModelFile(Hf("handy-computer/whisper-base.en-gguf", "whisper-base.en-Q8_0.gguf"), "whisper-base.en-Q8_0.gguf"),
            ]),
        new(
            Id: "moonshine-base-en",
            DisplayName: "Moonshine Base",
            SizeNote: "~78 MB",
            LanguageNote: "English · fast",
            Files:
            [
                new SttModelFile(Hf("handy-computer/moonshine-base-gguf", "moonshine-base-Q8_0.gguf"), "moonshine-base-Q8_0.gguf"),
            ]),
        new(
            Id: "parakeet-tdt-0.6b-v2",
            DisplayName: "Parakeet TDT 0.6B v2",
            SizeNote: "~730 MB",
            LanguageNote: "English · best accuracy",
            Files:
            [
                new SttModelFile(Hf("handy-computer/parakeet-tdt-0.6b-v2-gguf", "parakeet-tdt-0.6b-v2-Q8_0.gguf"), "parakeet-tdt-0.6b-v2-Q8_0.gguf"),
            ]),
        new(
            Id: "voxtral",
            DisplayName: "Voxtral Mini 3B",
            SizeNote: "~3 GB",
            LanguageNote: "Multilingual",
            GpuRecommended: true,
            Files:
            [
                new SttModelFile(Hf("handy-computer/voxtral-mini-3b-2507-GGUF", "Voxtral-Mini-3B-2507-Q4_K_M.gguf"), "Voxtral-Mini-3B-2507-Q4_K_M.gguf"),
            ]),
    ];

    public static SttModelInfo? Find(string? id) =>
        string.IsNullOrEmpty(id) ? null : Models.FirstOrDefault(m => m.Id == id);
}
