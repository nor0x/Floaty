namespace Floaty.Services;

/// <summary>
/// A curated embedding model that runs on this machine, downloaded from the list in
/// Settings → Model Provider → Local. These are ONNX exports of sentence-transformer models, run
/// in-process by ONNX Runtime — the same runtime that already powers the voice VAD.
/// </summary>
/// <param name="Dimensions">
/// Vector width. Switching between models of different widths invalidates the stored capture
/// vectors, so the Settings UI warns and points at the re-index button.
/// </param>
/// <param name="MaxTokens">Context window of the encoder; longer chunks are truncated.</param>
/// <param name="Pooling">How token vectors are collapsed into one sentence vector.</param>
public sealed record LocalEmbeddingModelInfo(
    string Id,
    string DisplayName,
    string SizeNote,
    string LanguageNote,
    int Dimensions,
    int MaxTokens,
    IReadOnlyList<SttModelFile> Files,
    PoolingKind Pooling = PoolingKind.Mean);

/// <summary>How an encoder's per-token outputs become one vector.</summary>
public enum PoolingKind
{
    /// <summary>Average of the token vectors, masked by attention. The sentence-transformers default.</summary>
    Mean,

    /// <summary>The [CLS] token's vector. What the BGE family was trained to use.</summary>
    Cls,
}

/// <summary>
/// The built-in catalog of on-device embedding models. Mirrors <see cref="SttModelCatalog"/>:
/// stable ids so a saved selection survives releases, a file list so multi-file models work, and
/// availability derived from disk rather than persisted anywhere.
///
/// Every entry is an int8-quantized ONNX export from the Xenova mirrors, which keeps downloads in
/// the tens of megabytes — this runs after every window switch, so size and speed matter more than
/// the last point of retrieval accuracy. All three are WordPiece/BERT models, which is what
/// <c>Microsoft.ML.Tokenizers</c>' <c>BertTokenizer</c> reads from a plain <c>vocab.txt</c>;
/// multilingual encoders are XLM-R based (SentencePiece, no vocab.txt) and would need a second
/// tokenizer path before they can be listed here.
/// </summary>
public static class LocalModelCatalog
{
    private static string Hf(string repo, string file) =>
        $"https://huggingface.co/{repo}/resolve/main/{file}";

    /// <summary>File name every entry's ONNX graph is saved as, regardless of its source path.</summary>
    public const string ModelFileName = "model.onnx";

    /// <summary>File name of the WordPiece vocabulary the tokenizer is built from.</summary>
    public const string VocabFileName = "vocab.txt";

    private static IReadOnlyList<SttModelFile> FilesFor(string repo) =>
    [
        new SttModelFile(Hf(repo, "onnx/model_quantized.onnx"), ModelFileName),
        new SttModelFile(Hf(repo, VocabFileName), VocabFileName),
    ];

    public static IReadOnlyList<LocalEmbeddingModelInfo> Models { get; } =
    [
        new(
            Id: "bge-small-en-v1.5",
            DisplayName: "BGE Small",
            SizeNote: "~34 MB",
            LanguageNote: "English · recommended",
            Dimensions: 384,
            MaxTokens: 512,
            Files: FilesFor("Xenova/bge-small-en-v1.5"),
            Pooling: PoolingKind.Cls),
        new(
            Id: "all-minilm-l6-v2",
            DisplayName: "All-MiniLM L6",
            SizeNote: "~23 MB",
            LanguageNote: "English · fastest",
            Dimensions: 384,
            MaxTokens: 256,
            Files: FilesFor("Xenova/all-MiniLM-L6-v2")),
        new(
            Id: "bge-base-en-v1.5",
            DisplayName: "BGE Base",
            SizeNote: "~105 MB",
            LanguageNote: "English · best accuracy",
            Dimensions: 768,
            MaxTokens: 512,
            Files: FilesFor("Xenova/bge-base-en-v1.5"),
            Pooling: PoolingKind.Cls),
    ];

    public static LocalEmbeddingModelInfo? Find(string? id) =>
        string.IsNullOrEmpty(id) ? null : Models.FirstOrDefault(m => m.Id == id);
}
