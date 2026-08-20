using Microsoft.Extensions.AI;

namespace Floaty.Services;

/// <summary>
/// Builds an on-device embedding generator for a model from <see cref="LocalModelCatalog"/>.
/// Split out behind an interface because the runner needs ONNX Runtime, which Floaty only
/// references on Windows; other platforms get <see cref="NullLocalEmbeddingFactory"/> and the
/// Local provider simply never reports itself as available.
/// </summary>
public interface ILocalEmbeddingFactory
{
    /// <summary>True when this platform can run local embedding models at all.</summary>
    bool IsSupported { get; }

    /// <summary>
    /// Whether <paramref name="modelId"/> is a known model that is downloaded and ready. Cheap
    /// enough to call from a "is this configured?" check, unlike <see cref="Create"/>, which loads
    /// the graph into memory.
    /// </summary>
    bool IsAvailable(string modelId);

    /// <summary>
    /// Creates a generator for <paramref name="modelId"/>, or null when the platform can't run it,
    /// the id is unknown, or the model isn't downloaded. Callers own the returned instance and
    /// dispose it when the configuration changes.
    /// </summary>
    IEmbeddingGenerator<string, Embedding<float>>? Create(string modelId);
}
