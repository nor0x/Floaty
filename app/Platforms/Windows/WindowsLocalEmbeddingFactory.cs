using Microsoft.Extensions.AI;

namespace Floaty.Platforms.Windows;

using Floaty.Services;

/// <summary>
/// Builds <see cref="OnnxEmbeddingGenerator"/> instances for downloaded models. Windows-only
/// because ONNX Runtime is referenced only there; other platforms get
/// <see cref="NullLocalEmbeddingFactory"/>.
/// </summary>
public sealed class WindowsLocalEmbeddingFactory : ILocalEmbeddingFactory
{
    private readonly ModelDownloadService _downloads;

    public WindowsLocalEmbeddingFactory(ModelDownloadService downloads)
    {
        _downloads = downloads;
    }

    public bool IsSupported => true;

    public bool IsAvailable(string modelId) =>
        LocalModelCatalog.Find(modelId) is { } model && _downloads.IsDownloaded(model);

    public IEmbeddingGenerator<string, Embedding<float>>? Create(string modelId)
    {
        var model = LocalModelCatalog.Find(modelId);
        if (model is null || !_downloads.IsDownloaded(model))
            return null;

        try
        {
            return new OnnxEmbeddingGenerator(model, _downloads.GetEmbeddingModelDir(model.Id));
        }
        catch
        {
            // A corrupt download or a native load failure degrades to "not configured" rather than
            // taking down the capture that asked for an embedding.
            return null;
        }
    }
}
