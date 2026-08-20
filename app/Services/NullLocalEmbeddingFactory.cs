using Microsoft.Extensions.AI;

namespace Floaty.Services;

/// <summary>
/// Fallback for platforms without an ONNX Runtime reference. Reports the feature as unsupported so
/// the Settings UI hides the Local provider rather than offering downloads that could never run.
/// </summary>
public sealed class NullLocalEmbeddingFactory : ILocalEmbeddingFactory
{
    public bool IsSupported => false;

    public bool IsAvailable(string modelId) => false;

    public IEmbeddingGenerator<string, Embedding<float>>? Create(string modelId) => null;
}
