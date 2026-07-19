namespace Floaty.Services;

/// <summary>
/// Downloads speech-to-text models from <see cref="SttModelCatalog"/> into
/// <see cref="FloatyPaths.SttModels"/>/&lt;model id&gt;, along with their shared dependencies:
/// the Silero VAD model and the transcribe.cpp native runtime. Whether a model is "downloaded"
/// is always derived from the expected files being present on disk — nothing is persisted in config.
/// </summary>
public sealed class ModelDownloadService
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly NativeRuntimeService _runtime;

    public ModelDownloadService(NativeRuntimeService runtime)
    {
        _runtime = runtime;
    }

    /// <summary>Raised after a download completes or a model is deleted.</summary>
    public event EventHandler? ModelsChanged;

    public string GetModelDir(string modelId) => Path.Combine(FloatyPaths.SttModels, modelId);

    /// <summary>
    /// True when every file of the model, the shared Silero VAD, and the native runtime are on disk.
    /// </summary>
    public bool IsDownloaded(SttModelInfo model) =>
        model.IsAvailable && HasAllFiles(model) && HasAllFiles(SttModelCatalog.SileroVad) && _runtime.IsInstalled;

    private bool HasAllFiles(SttModelInfo model)
    {
        var dir = GetModelDir(model.Id);
        return model.Files.Count > 0 && model.Files.All(f => File.Exists(Path.Combine(dir, f.FileName)));
    }

    /// <summary>
    /// Downloads all files of <paramref name="model"/> plus its dependencies (Silero VAD, native
    /// runtime), reporting aggregate progress in [0,1]. Files stream to a ".partial" name and are
    /// renamed on completion, so a killed download never leaves a truncated file that passes
    /// <see cref="IsDownloaded"/>.
    /// </summary>
    public async Task DownloadAsync(SttModelInfo model, IProgress<double>? progress, CancellationToken ct = default)
    {
        var pending = new List<(SttModelFile File, string Dir)>();
        foreach (var dep in new[] { SttModelCatalog.SileroVad, model })
        {
            var dir = GetModelDir(dep.Id);
            pending.AddRange(dep.Files
                .Where(f => !File.Exists(Path.Combine(dir, f.FileName)))
                .Select(f => (f, dir)));
        }
        var runtimeBytes = _runtime.IsInstalled ? 0 : NativeRuntimeService.ArchiveBytes;

        if (pending.Count == 0 && runtimeBytes == 0)
        {
            progress?.Report(1);
            return;
        }

        // Aggregate percent needs the total size up front; ask for headers of every file first.
        // Any file without a Content-Length degrades progress to completed-file fraction.
        var sizes = new long?[pending.Count];
        for (var i = 0; i < pending.Count; i++)
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, pending[i].File.Url);
            using var response = await Http.SendAsync(head, ct);
            sizes[i] = response.IsSuccessStatusCode ? response.Content.Headers.ContentLength : null;
        }
        var knownTotal = sizes.All(s => s.HasValue) ? sizes.Sum(s => s!.Value) + runtimeBytes : 0;

        long completedBytes = 0;

        // The shared native runtime rides along with the first model download, like the VAD.
        if (runtimeBytes > 0)
        {
            var runtimeProgress = knownTotal > 0
                ? new Progress<double>(p => progress?.Report(Math.Min(1, p * runtimeBytes / knownTotal)))
                : null;
            await _runtime.EnsureInstalledAsync(runtimeProgress, ct);
            completedBytes += runtimeBytes;
        }

        for (var i = 0; i < pending.Count; i++)
        {
            var (file, dir) = pending[i];
            Directory.CreateDirectory(dir);
            var finalPath = Path.Combine(dir, file.FileName);
            var partialPath = finalPath + ".partial";

            try
            {
                using var response = await Http.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                await using (var source = await response.Content.ReadAsStreamAsync(ct))
                await using (var target = File.Create(partialPath))
                {
                    var buffer = new byte[128 * 1024];
                    int read;
                    long fileBytes = 0;
                    while ((read = await source.ReadAsync(buffer, ct)) > 0)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, read), ct);
                        fileBytes += read;
                        if (knownTotal > 0)
                            progress?.Report(Math.Min(1, (completedBytes + fileBytes) / (double)knownTotal));
                    }
                    completedBytes += fileBytes;
                }

                File.Move(partialPath, finalPath, overwrite: true);
                if (knownTotal == 0)
                    progress?.Report((i + 1) / (double)pending.Count);
            }
            catch
            {
                try { File.Delete(partialPath); } catch { /* best effort */ }
                throw;
            }
        }

        PruneStaleFiles(model);
        progress?.Report(1);
        ModelsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Removes files in the model's folder that the catalog no longer lists — e.g. multi-file
    /// ONNX sets left behind by the earlier sherpa-onnx engine after the GGUF replaces them.
    /// </summary>
    private void PruneStaleFiles(SttModelInfo model)
    {
        try
        {
            var dir = GetModelDir(model.Id);
            var expected = model.Files.Select(f => f.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.GetFiles(dir))
            {
                if (!expected.Contains(Path.GetFileName(path)))
                    File.Delete(path);
            }
        }
        catch
        {
            // Leftover files are harmless; they go for good when the model is deleted.
        }
    }

    /// <summary>Removes the model's folder (shared VAD + runtime are kept for other models).</summary>
    public void Delete(SttModelInfo model)
    {
        var dir = GetModelDir(model.Id);
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
        ModelsChanged?.Invoke(this, EventArgs.Empty);
    }
}
