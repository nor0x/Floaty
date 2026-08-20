using Microsoft.Extensions.AI;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace Floaty.Platforms.Windows;

using Floaty.Services;

/// <summary>
/// Runs a sentence-transformer ONNX encoder in-process so captures can be embedded without a cloud
/// key. Built for the screen-history workload: short chunks, called constantly, latency and cost
/// matter more than the last point of retrieval accuracy.
///
/// The graph's inputs are bound by name from session metadata rather than positionally — exports
/// differ in whether they take <c>token_type_ids</c> — the same approach
/// <see cref="SileroVadDetector"/> uses for the VAD graph.
/// </summary>
public sealed class OnnxEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private const string InputIds = "input_ids";
    private const string AttentionMask = "attention_mask";
    private const string TokenTypeIds = "token_type_ids";

    private readonly LocalEmbeddingModelInfo _model;
    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private readonly bool _wantsTokenTypeIds;
    private readonly EmbeddingGeneratorMetadata _metadata;

    // ONNX Runtime sessions are thread-safe for Run, but Floaty embeds from the screen-history
    // worker and the chat turn at once; serializing keeps peak memory to one batch.
    private readonly Lock _gate = new();

    public OnnxEmbeddingGenerator(LocalEmbeddingModelInfo model, string modelDir)
    {
        _model = model;

        var modelPath = Path.Combine(modelDir, LocalModelCatalog.ModelFileName);
        var vocabPath = Path.Combine(modelDir, LocalModelCatalog.VocabFileName);
        if (!File.Exists(modelPath) || !File.Exists(vocabPath))
            throw new FileNotFoundException($"Local embedding model '{model.Id}' is not downloaded.", modelPath);

        var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        _session = new InferenceSession(modelPath, options);
        _wantsTokenTypeIds = _session.InputMetadata.ContainsKey(TokenTypeIds);

        using var vocab = File.OpenRead(vocabPath);
        _tokenizer = BertTokenizer.Create(vocab);

        _metadata = new EmbeddingGeneratorMetadata(
            providerName: "onnx",
            defaultModelId: model.Id,
            defaultModelDimensions: model.Dimensions);
    }

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var inputs = values as IList<string> ?? values.ToList();
        if (inputs.Count == 0)
            return new GeneratedEmbeddings<Embedding<float>>();

        // Inference is CPU-bound and callers await it from UI-adjacent paths (the settings Test
        // button, a chat turn), so keep it off whatever thread asked.
        return await Task.Run(() => Generate(inputs, cancellationToken), cancellationToken);
    }

    private GeneratedEmbeddings<Embedding<float>> Generate(IList<string> inputs, CancellationToken cancellationToken)
    {
        var encoded = new List<int>[inputs.Count];
        var longest = 0;

        for (var i = 0; i < inputs.Count; i++)
        {
            var ids = _tokenizer.EncodeToIds(inputs[i] ?? string.Empty, addSpecialTokens: true);
            if (ids.Count > _model.MaxTokens)
            {
                // Keep the trailing [SEP] so the encoder still sees a well-formed sequence.
                var truncated = ids.Take(_model.MaxTokens - 1).ToList();
                truncated.Add(ids[^1]);
                ids = truncated;
            }

            encoded[i] = ids as List<int> ?? ids.ToList();
            longest = Math.Max(longest, encoded[i].Count);
        }

        var batch = inputs.Count;
        var idBuffer = new long[batch * longest];
        var maskBuffer = new long[batch * longest];
        var typeBuffer = _wantsTokenTypeIds ? new long[batch * longest] : null;

        for (var row = 0; row < batch; row++)
        {
            var ids = encoded[row];
            for (var col = 0; col < ids.Count; col++)
            {
                idBuffer[row * longest + col] = ids[col];
                maskBuffer[row * longest + col] = 1;
            }
            // Padding stays at id 0 with mask 0, so pooling ignores it entirely.
        }

        var shape = new[] { batch, longest };
        var feeds = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(InputIds, new DenseTensor<long>(idBuffer, shape)),
            NamedOnnxValue.CreateFromTensor(AttentionMask, new DenseTensor<long>(maskBuffer, shape)),
        };
        if (typeBuffer is not null)
            feeds.Add(NamedOnnxValue.CreateFromTensor(TokenTypeIds, new DenseTensor<long>(typeBuffer, shape)));

        cancellationToken.ThrowIfCancellationRequested();

        var results = new GeneratedEmbeddings<Embedding<float>>();

        lock (_gate)
        {
            using var outputs = _session.Run(feeds);

            // The first output is the token-level hidden state: [batch, tokens, dimensions].
            var hidden = outputs.First().AsTensor<float>();
            var dimensions = hidden.Dimensions[2];

            for (var row = 0; row < batch; row++)
            {
                var vector = _model.Pooling == PoolingKind.Cls
                    ? ClsPool(hidden, row, dimensions)
                    : MeanPool(hidden, row, dimensions, maskBuffer, longest);

                Normalize(vector);
                results.Add(new Embedding<float>(vector) { ModelId = _model.Id });
            }
        }

        return results;
    }

    /// <summary>Takes the [CLS] token's vector — what the BGE models were trained to use.</summary>
    private static float[] ClsPool(Tensor<float> hidden, int row, int dimensions)
    {
        var vector = new float[dimensions];
        for (var d = 0; d < dimensions; d++)
            vector[d] = hidden[row, 0, d];
        return vector;
    }

    /// <summary>Averages token vectors, counting only real tokens (mask 1), never the padding.</summary>
    private static float[] MeanPool(Tensor<float> hidden, int row, int dimensions, long[] mask, int stride)
    {
        var vector = new float[dimensions];
        var counted = 0;

        for (var token = 0; token < stride; token++)
        {
            if (mask[row * stride + token] == 0)
                continue;

            counted++;
            for (var d = 0; d < dimensions; d++)
                vector[d] += hidden[row, token, d];
        }

        if (counted > 1)
        {
            for (var d = 0; d < dimensions; d++)
                vector[d] /= counted;
        }

        return vector;
    }

    /// <summary>
    /// Scales to unit length. LiteGraph scores with cosine similarity, which normalized vectors make
    /// a plain dot product — and it matches what the OpenAI embeddings this replaces already return.
    /// </summary>
    private static void Normalize(float[] vector)
    {
        double sum = 0;
        foreach (var value in vector)
            sum += value * value;

        var length = Math.Sqrt(sum);
        if (length <= 0)
            return;

        for (var i = 0; i < vector.Length; i++)
            vector[i] = (float)(vector[i] / length);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is not null)
            return null;

        if (serviceType == typeof(EmbeddingGeneratorMetadata))
            return _metadata;

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
        lock (_gate)
            _session.Dispose();
    }
}
