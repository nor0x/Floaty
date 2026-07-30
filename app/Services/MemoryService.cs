using System.Text.Json;
using ExpressionTree;
using LiteGraph;
using LiteGraph.GraphRepositories.Sqlite;
using LiteGraph.Indexing.Vector;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Floaty.Services;

/// <summary>
/// Microsoft.Extensions.AI (OpenAI) embeddings persisted to an embedded LiteGraph SQLite database.
/// Each capture becomes one node (label <c>Capture</c>) carrying its embedding vector and metadata.
/// </summary>
public sealed class MemoryService : IMemoryService
{
    // Fixed identifiers for Floaty's single tenant + capture graph, created on first use.
    private static readonly Guid TenantGuid = new("f10a7100-0000-0000-0000-000000000001");
    private static readonly Guid GraphGuid = new("f10a7100-0000-0000-0000-000000000002");

    // Safety net only: TextChunker keeps pieces far below the model's ~8191-token input limit, but a
    // chunk is clamped before it ever reaches the API so a pathological line can't fail a capture.
    private const int MaxEmbedChars = 8000;

    // Chunks per embedding request. The API takes an array, so a 500-chunk capture costs ~8 calls
    // rather than 500; at ~250 tokens a chunk this stays well inside the per-request token budget.
    private const int EmbedBatchSize = 64;

    // Insurance only. Measured against LiteGraph 7.0.0 — brute force and HNSW alike return at most
    // one hit per node, already collapsing a capture's chunks — but the result type doesn't promise
    // it, so ask for a margin and group by capture anyway rather than silently returning fewer
    // memories than asked for if that ever changes.
    private const int ChunkOverfetchFactor = 2;

    // Below this many stored vectors LiteGraph's brute-force cosine scan beats walking an index,
    // per its own documentation, so the index only takes over once it's actually worth it.
    private const int VectorIndexThreshold = 2000;

    // Nodes deleted per round-trip when clearing screen history; bounds peak memory on a history
    // that's been accumulating for months.
    private const int DeleteBatchSize = 200;

    private readonly SettingsService _settings;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private LiteGraphClient? _client;

    // Set once the graph's vector index has been reconciled this session. The check needs a real
    // vector's dimensionality, so it can only run after the first store.
    private int _vectorIndexChecked;

    private IEmbeddingGenerator<string, Embedding<float>>? _embeddings;
    private string? _embeddingsKey;
    private string? _embeddingsModel;

    private IChatClient? _snapshot;
    private string? _snapshotKey;
    private string? _snapshotModel;

    public MemoryService(SettingsService settings)
    {
        _settings = settings;
        _settings.Changed += (_, _) =>
        {
            _embeddings = null;
            _snapshot = null;
        };
    }

    public async Task<bool> RememberCaptureAsync(
        CaptureResult capture,
        string source = IMemoryService.ManualCaptureSource,
        CancellationToken cancellationToken = default)
    {
        var config = _settings.Current;
        if (string.IsNullOrWhiteSpace(config.OpenAiApiKey))
            return false;

        // A dropped image has no text of its own — the vision description *is* the memory — so only
        // bail when there's neither text to embed nor an image to describe.
        var isDrop = source == IMemoryService.DroppedFileSource;
        if (string.IsNullOrWhiteSpace(capture.Content)
            && (string.IsNullOrWhiteSpace(capture.ImagePath) || !File.Exists(capture.ImagePath)))
        {
            return false;
        }

        // Ask the vision (snapshot) model to describe the image, then store its words alongside the
        // plain text. Best-effort: a null description just means text-only memory. Text-only history
        // captures (and non-image drops) arrive with an empty ImagePath, which skips the vision call.
        var description = await DescribeScreenshotAsync(capture.ImagePath, config, cancellationToken);

        // Order: title -> description -> body. The whole thing is chunked rather than truncated, so
        // this is about reading order rather than about what survives.
        var builder = new System.Text.StringBuilder();
        builder.Append(capture.WindowTitle);
        if (!string.IsNullOrWhiteSpace(description))
            builder.Append(isDrop ? "\n\n[File description]\n" : "\n\n[Screenshot description]\n").Append(description);
        if (!string.IsNullOrWhiteSpace(capture.Content))
            builder.Append(isDrop ? "\n\n[File contents]\n" : "\n\n[On-screen text]\n").Append(capture.Content);

        var text = builder.ToString();

        // Append the description to the on-disk text file so it holds both representations.
        if (!string.IsNullOrWhiteSpace(description))
            AppendDescriptionToFile(capture.TextPath, description, isDrop ? "File description" : "Screenshot description");

        // Drops carry a second label so a future "forget dropped files" sweep is cheap. LiteGraph
        // matches label supersets, so search and the auto-capture maintenance queries are unaffected.
        var labels = isDrop
            ? new List<string> { "Capture", "Drop" }
            : new List<string> { "Capture" };

        await StoreMemoryNodeAsync(
            name: string.IsNullOrWhiteSpace(capture.WindowTitle) ? (isDrop ? "Dropped file" : "Capture") : capture.WindowTitle,
            labels: labels,
            data: new
            {
                capture.ImagePath,
                capture.TextPath,
                capture.WindowTitle,
                SnapshotDescription = description,
                CapturedUtc = DateTime.UtcNow,
                Source = source,
                MimeType = isDrop ? MimeTypes.FromPath(capture.WindowTitle) : "image/png",
            },
            content: text,
            config: config,
            cancellationToken: cancellationToken);
        return true;
    }

    public async Task<bool> RememberTextAsync(string text, CancellationToken cancellationToken = default)
    {
        var config = _settings.Current;
        if (string.IsNullOrWhiteSpace(config.OpenAiApiKey) || string.IsNullOrWhiteSpace(text))
            return false;

        text = text.Trim();

        await StoreMemoryNodeAsync(
            name: ShortTitle(text),
            labels: new List<string> { "Note" },
            data: new { Source = "note", CapturedUtc = DateTime.UtcNow },
            content: text,
            config: config,
            cancellationToken: cancellationToken);
        return true;
    }

    // Chunks <paramref name="content"/>, embeds every chunk, and stores them as one graph node
    // carrying a vector per chunk. One node per capture keeps citations, the Settings count and
    // "Clear screen history" working on whole captures, while search matches at chunk granularity.
    private async Task StoreMemoryNodeAsync(
        string name,
        List<string> labels,
        object data,
        string content,
        FloatyConfig config,
        CancellationToken cancellationToken)
    {
        var vectors = await EmbedChunksAsync(content, config, cancellationToken);
        if (vectors.Count == 0)
            return;

        var client = await GetClientAsync(cancellationToken);

        var node = new Node
        {
            TenantGUID = TenantGuid,
            GraphGUID = GraphGuid,
            Name = name,
            Labels = labels,
            Data = data,
            Vectors = vectors,
        };

        await client.Node.Create(node, cancellationToken);

        // Deliberately not awaited: building the index for the first time over a long history can
        // take a while, and /capture and file drops are awaited by the UI. Nothing depends on the
        // result, and the method swallows its own failures. CancellationToken.None because the
        // caller's token dies with their operation, not with this.
        var dimensionality = vectors[0].Dimensionality;
        _ = Task.Run(() => TryEnsureVectorIndexAsync(client, dimensionality, CancellationToken.None));
    }

    // Splits text into chunks and embeds them in batches. Batching is what makes "index everything"
    // affordable: a 400k-character capture is ~550 chunks, which is ~9 requests instead of ~550.
    private async Task<List<VectorMetadata>> EmbedChunksAsync(
        string content,
        FloatyConfig config,
        CancellationToken cancellationToken)
    {
        var chunks = TextChunker.Split(content);
        if (chunks.Count == 0)
            return new List<VectorMetadata>();

        var generator = GetOrCreateEmbeddings(config);
        var vectors = new List<VectorMetadata>(chunks.Count);

        for (var offset = 0; offset < chunks.Count; offset += EmbedBatchSize)
        {
            var batch = chunks
                .Skip(offset)
                .Take(EmbedBatchSize)
                .Select(c => c.Length > MaxEmbedChars ? c[..MaxEmbedChars] : c)
                .ToList();

            var embeddings = await generator.GenerateAsync(batch, cancellationToken: cancellationToken);

            for (var i = 0; i < batch.Count; i++)
            {
                var vector = embeddings[i].Vector.ToArray().ToList();
                vectors.Add(new VectorMetadata
                {
                    TenantGUID = TenantGuid,
                    GraphGUID = GraphGuid,
                    Model = config.EmbeddingModel,
                    Dimensionality = vector.Count,
                    Content = batch[i],
                    Vectors = vector,
                });
            }
        }

        return vectors;
    }

    // Screen history runs permanently, so the vector table only grows. Without an index LiteGraph
    // brute-forces cosine similarity across every stored vector on every search, which gets slower
    // forever. Runs after the first store because the index needs a concrete dimensionality, and is
    // entirely best-effort: brute force is correct, just slower, so nothing here may fail a capture.
    private async Task TryEnsureVectorIndexAsync(
        LiteGraphClient client,
        int dimensionality,
        CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _vectorIndexChecked, 1) != 0)
            return;

        try
        {
            var graph = await client.Graph.ReadByGuid(
                TenantGuid, GraphGuid, includeData: false, includeSubordinates: false, cancellationToken);
            if (graph is null)
                return;

            var indexed = graph.VectorIndexType is not null and not VectorIndexTypeEnum.None;

            // Dimensionality is fixed per graph, but the user can switch EmbeddingModel in Settings
            // (text-embedding-3-large is 3072, not 1536). Tear a mismatched index down rather than
            // search a stale one.
            if (indexed && graph.VectorDimensionality != dimensionality)
            {
                await client.Graph.DisableVectorIndexing(
                    TenantGuid, GraphGuid, deleteIndexFile: true, cancellationToken);
                indexed = false;
            }

            if (!indexed)
            {
                await client.Graph.EnableVectorIndexing(TenantGuid, GraphGuid, new VectorIndexConfiguration
                {
                    VectorIndexType = VectorIndexTypeEnum.HnswSqlite,
                    VectorIndexFile = Path.Combine(FloatyPaths.Home, "floaty.vectors.db"),
                    VectorDimensionality = dimensionality,
                    VectorIndexThreshold = VectorIndexThreshold,
                }, cancellationToken);
            }
            else if (graph.VectorIndexDirty)
            {
                await client.Graph.RebuildVectorIndex(TenantGuid, GraphGuid, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Floaty] Vector index setup skipped: {ex.Message}");
        }
    }

    private static string ShortTitle(string text)
    {
        var firstLine = text.ReplaceLineEndings(" ").Trim();
        return firstLine.Length <= 60 ? firstLine : firstLine[..60] + "…";
    }

    // Describes an image using the configured vision (snapshot) model — a window screenshot, or a
    // dropped image file. Returns null when snapshotting is disabled, the image is missing, or the
    // call fails; capture/embed still proceed in that case.
    private async Task<string?> DescribeScreenshotAsync(string imagePath, FloatyConfig config, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.SnapshotModel) || !File.Exists(imagePath))
            return null;

        try
        {
            var client = GetOrCreateSnapshot(config);
            var imageBytes = await File.ReadAllBytesAsync(imagePath, cancellationToken);

            var message = new ChatMessage(ChatRole.User, new List<AIContent>
            {
                new TextContent(
                    "Describe this image: if it's a screenshot, the application shown, the visible UI and " +
                    "any notable on-screen content; otherwise its subject and any legible text. " +
                    "Be factual and concise."),
                // Screenshots are always PNG, but dropped images can be JPEG/WebP/GIF/…
                new DataContent(imageBytes, MimeTypes.FromPath(imagePath)),
            });

            var response = await client.GetResponseAsync([message], cancellationToken: cancellationToken);
            var text = response.Text?.Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            // Snapshot is best-effort; degrade to text-only memory rather than failing the capture.
            return null;
        }
    }

    private static void AppendDescriptionToFile(string textPath, string description, string heading)
    {
        try
        {
            if (File.Exists(textPath))
                File.AppendAllText(textPath, $"\n\n## {heading}\n{description}\n");
        }
        catch
        {
            // The capture is already saved; failing to annotate the .txt is non-fatal.
        }
    }

    public async Task<IReadOnlyList<CaptureSearchResult>> SearchCapturesAsync(
        string query,
        int topK = 5,
        CancellationToken cancellationToken = default)
    {
        var config = _settings.Current;
        if (string.IsNullOrWhiteSpace(config.OpenAiApiKey) || string.IsNullOrWhiteSpace(query))
            return Array.Empty<CaptureSearchResult>();

        var generator = GetOrCreateEmbeddings(config);
        var embeddings = await generator.GenerateAsync([query], cancellationToken: cancellationToken);
        var queryVector = embeddings[0].Vector.ToArray().ToList();

        var client = await GetClientAsync(cancellationToken);

        var request = new VectorSearchRequest
        {
            TenantGUID = TenantGuid,
            GraphGUID = GraphGuid,
            Domain = VectorSearchDomainEnum.Node,
            SearchType = VectorSearchTypeEnum.CosineSimilarity,
            // Over-fetch: a capture now holds one vector per chunk, so several of the top hits can be
            // different passages of the same screen. Without this, one long capture could take every
            // slot and the caller would get a single distinct memory back.
            TopK = topK * ChunkOverfetchFactor,
            Embeddings = queryVector,
        };

        var results = new List<CaptureSearchResult>();
        var seen = new HashSet<Guid>();

        await foreach (var hit in client.Vector.Search(request, cancellationToken))
        {
            if (hit.Node is null || !seen.Add(hit.Node.GUID))
                continue; // already have this capture, via a better-scoring chunk

            // Re-read the hit with its data + subordinates so we recover the chunks and metadata.
            var node = await client.Node.ReadByGuid(
                TenantGuid, GraphGuid, hit.Node.GUID,
                includeData: true, includeSubordinates: true, cancellationToken);

            var (imagePath, textPath, capturedUtc, _) = ParseCaptureData(node?.Data);

            results.Add(new CaptureSearchResult(
                Title: node?.Name ?? hit.Node.Name ?? "Capture",
                CapturedUtc: capturedUtc,
                Score: hit.Score,
                ImagePath: imagePath,
                TextPath: textPath,
                Content: BestChunk(node?.Vectors, queryVector)));

            if (results.Count >= topK)
                break;
        }

        return results;
    }

    /// <summary>
    /// Picks the chunk that actually matched. LiteGraph's search result names the node but not which
    /// of its vectors scored, so the winning passage is chosen here by cosine against the same query
    /// vector. Captures stored before chunking hold a single vector and fall through unchanged.
    /// </summary>
    private static string BestChunk(List<VectorMetadata>? vectors, List<float> queryVector)
    {
        if (vectors is null || vectors.Count == 0)
            return string.Empty;

        if (vectors.Count == 1)
            return vectors[0].Content ?? string.Empty;

        var best = string.Empty;
        var bestScore = float.NegativeInfinity;

        foreach (var candidate in vectors)
        {
            var score = CosineSimilarity(candidate.Vectors, queryVector);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate.Content ?? string.Empty;
            }
        }

        return best;
    }

    private static float CosineSimilarity(List<float>? a, List<float> b)
    {
        if (a is null || a.Count != b.Count)
            return float.NegativeInfinity;

        double dot = 0, magA = 0, magB = 0;
        for (var i = 0; i < a.Count; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        if (magA <= 0 || magB <= 0)
            return float.NegativeInfinity;

        return (float)(dot / (Math.Sqrt(magA) * Math.Sqrt(magB)));
    }

    // Best-effort extraction of the metadata we stored in Node.Data. Survives whatever concrete type
    // LiteGraph rehydrates Data into by round-tripping through JSON.
    private static (string? ImagePath, string? TextPath, DateTime? CapturedUtc, string? Source) ParseCaptureData(object? data)
    {
        if (data is null)
            return (null, null, null, null);

        try
        {
            var element = JsonSerializer.SerializeToElement(data);

            string? Str(string name) =>
                element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

            DateTime? capturedUtc = element.TryGetProperty("CapturedUtc", out var c) && c.TryGetDateTime(out var dt)
                ? dt
                : null;

            return (Str("ImagePath"), Str("TextPath"), capturedUtc, Str("Source"));
        }
        catch
        {
            return (null, null, null, null);
        }
    }

    public async Task<int> ReindexCapturesAsync(
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var config = _settings.Current;
        if (string.IsNullOrWhiteSpace(config.OpenAiApiKey))
            return 0;

        var client = await GetClientAsync(cancellationToken);

        // Collect identities first, without subordinates: after one re-index a capture can carry
        // hundreds of chunk vectors, and pulling them all into memory just to replace them would
        // undo the point of paging everywhere else.
        var targets = new List<(Guid Guid, string? TextPath)>();
        await foreach (var node in client.Node.ReadMany(
            TenantGuid, GraphGuid,
            labels: new List<string> { "Capture" },
            includeData: true,
            token: cancellationToken))
        {
            var (_, textPath, _, _) = ParseCaptureData(node.Data);
            targets.Add((node.GUID, textPath));
        }

        var done = 0;
        var reindexed = 0;
        progress?.Report((0, targets.Count));

        foreach (var (guid, textPath) in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            done++;

            try
            {
                // The saved .txt holds the full capture — it was never subject to the embedding
                // limit — so it, not the stored vector, is the source of truth for re-indexing.
                if (string.IsNullOrWhiteSpace(textPath) || !File.Exists(textPath))
                    continue;

                var node = await client.Node.ReadByGuid(
                    TenantGuid, GraphGuid, guid,
                    includeData: true, includeSubordinates: true, cancellationToken);
                if (node is null)
                    continue;

                var text = await File.ReadAllTextAsync(textPath, cancellationToken);
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                // Lead with the title, matching how a fresh capture is composed.
                var composed = string.IsNullOrWhiteSpace(node.Name) ? text : $"{node.Name}\n\n{text}";

                var vectors = await EmbedChunksAsync(composed, config, cancellationToken);
                if (vectors.Count == 0)
                    continue;

                node.Vectors = vectors;
                await client.Node.Update(node, cancellationToken);
                reindexed++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One unreadable capture must not abandon the rest of the sweep.
                System.Diagnostics.Debug.WriteLine($"[Floaty] Re-index skipped {guid}: {ex.Message}");
            }
            finally
            {
                progress?.Report((done, targets.Count));
            }
        }

        // Every vector in the graph just changed, so any existing index describes the old ones.
        try
        {
            var graph = await client.Graph.ReadByGuid(
                TenantGuid, GraphGuid, includeData: false, includeSubordinates: false, cancellationToken);
            if (graph?.VectorIndexType is not null and not VectorIndexTypeEnum.None)
                await client.Graph.RebuildVectorIndex(TenantGuid, GraphGuid, cancellationToken);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Floaty] Vector index rebuild skipped: {ex.Message}");
        }

        return reindexed;
    }

    public async Task<int> CountAutoCapturesAsync(CancellationToken cancellationToken = default)
    {
        var client = await GetClientAsync(cancellationToken);

        // One query, no Data transferred: let SQLite match Source and just report the total. Settings
        // loads this on open, and screen history is meant to accumulate indefinitely, so the old
        // count-by-reading-every-node got slower every day the app ran.
        var result = await client.Node.Enumerate(new EnumerationRequest
        {
            TenantGUID = TenantGuid,
            GraphGUID = GraphGuid,
            Labels = new List<string> { "Capture" },
            Expr = AutoCaptureFilter,
            IncludeData = false,
            IncludeSubordinates = false,
            MaxResults = 1,
        }, cancellationToken);

        return (int)Math.Min(result.TotalRecords, int.MaxValue);
    }

    public async Task<int> DeleteAutoCapturesAsync(CancellationToken cancellationToken = default)
    {
        var client = await GetClientAsync(cancellationToken);
        var deleted = 0;

        // Page rather than materialize: a months-old history would otherwise pull every auto-capture
        // node — Data and all — into memory before deleting the first one. Deleting while enumerating
        // the same SQLite-backed cursor is asking for trouble, so each pass reads a batch, finishes
        // the enumeration, then deletes it; the next pass sees what's left.
        while (true)
        {
            var batch = new List<Node>(DeleteBatchSize);

            await foreach (var node in client.Node.ReadMany(
                TenantGuid, GraphGuid,
                labels: new List<string> { "Capture" },
                includeData: true,
                token: cancellationToken))
            {
                // Re-check Source in-process. Clearing history must never delete a manual capture,
                // so correctness here doesn't rest on the Data-path filter's semantics.
                var (_, _, _, source) = ParseCaptureData(node.Data);
                if (source != IMemoryService.AutoCaptureSource)
                    continue;

                batch.Add(node);
                if (batch.Count >= DeleteBatchSize)
                    break;
            }

            if (batch.Count == 0)
                break;

            await client.Node.DeleteMany(
                TenantGuid, GraphGuid, batch.Select(n => n.GUID).ToList(), cancellationToken);
            deleted += batch.Count;

            // Best-effort file cleanup; a missing or locked file must not abort the sweep.
            foreach (var node in batch)
            {
                var (imagePath, textPath, _, _) = ParseCaptureData(node.Data);
                TryDeleteFile(imagePath);
                TryDeleteFile(textPath);
            }
        }

        return deleted;
    }

    // Matches Source == "auto" against the node's Data JSON. Left terms are LiteGraph data paths
    // relative to the Data object, so this mirrors what ParseCaptureData reads out of it.
    private static Expr AutoCaptureFilter =>
        new("Source", OperatorEnum.Equals, IMemoryService.AutoCaptureSource);

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            File.Delete(path);
        }
        catch
        {
            // Leaving an orphaned capture file behind is acceptable; failing the cleanup is not.
        }
    }

    private IEmbeddingGenerator<string, Embedding<float>> GetOrCreateEmbeddings(FloatyConfig config)
    {
        if (_embeddings is not null && _embeddingsKey == config.OpenAiApiKey && _embeddingsModel == config.EmbeddingModel)
            return _embeddings;

        _embeddingsKey = config.OpenAiApiKey;
        _embeddingsModel = config.EmbeddingModel;
        _embeddings = new OpenAIClient(config.OpenAiApiKey)
            .GetEmbeddingClient(config.EmbeddingModel)
            .AsIEmbeddingGenerator();
        return _embeddings;
    }

    private IChatClient GetOrCreateSnapshot(FloatyConfig config)
    {
        if (_snapshot is not null && _snapshotKey == config.OpenAiApiKey && _snapshotModel == config.SnapshotModel)
            return _snapshot;

        _snapshotKey = config.OpenAiApiKey;
        _snapshotModel = config.SnapshotModel;
        _snapshot = new OpenAIClient(config.OpenAiApiKey)
            .GetChatClient(config.SnapshotModel)
            .AsIChatClient();
        return _snapshot;
    }

    private async Task<LiteGraphClient> GetClientAsync(CancellationToken cancellationToken)
    {
        if (_client is not null)
            return _client;

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_client is not null)
                return _client;

            var dbPath = Path.Combine(FloatyPaths.Home, "floaty.db");
            var client = new LiteGraphClient(new SqliteGraphRepository(dbPath));
            client.InitializeRepository();

            if (!await client.Tenant.ExistsByGuid(TenantGuid, cancellationToken))
                await client.Tenant.Create(new TenantMetadata { GUID = TenantGuid, Name = "Floaty" }, cancellationToken);

            if (!await client.Graph.ExistsByGuid(TenantGuid, GraphGuid, cancellationToken))
                await client.Graph.Create(new Graph { GUID = GraphGuid, TenantGUID = TenantGuid, Name = "Captures" }, cancellationToken);

            _client = client;
            return _client;
        }
        finally
        {
            _initLock.Release();
        }
    }
}
