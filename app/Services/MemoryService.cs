using System.Text.Json;
using LiteGraph;
using LiteGraph.GraphRepositories.Sqlite;
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

    // Keep embedding input under the model's token limit (~8191 tokens for text-embedding-3-small).
    private const int MaxEmbedChars = 8000;

    private readonly SettingsService _settings;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private LiteGraphClient? _client;

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

    public async Task<bool> RememberCaptureAsync(CaptureResult capture, CancellationToken cancellationToken = default)
    {
        var config = _settings.Current;
        if (string.IsNullOrWhiteSpace(config.OpenAiApiKey) || string.IsNullOrWhiteSpace(capture.Content))
            return false;

        // Ask the vision (snapshot) model to describe the screenshot, then store its words alongside
        // the plain accessibility text. Best-effort: a null description just means text-only memory.
        var description = await DescribeScreenshotAsync(capture.ImagePath, config, cancellationToken);

        // Order: title -> screenshot description -> on-screen text, so the description survives the
        // MaxEmbedChars truncation even when the accessibility text is long.
        var builder = new System.Text.StringBuilder();
        builder.Append(capture.WindowTitle);
        if (!string.IsNullOrWhiteSpace(description))
            builder.Append("\n\n[Screenshot description]\n").Append(description);
        builder.Append("\n\n[On-screen text]\n").Append(capture.Content);

        var text = builder.ToString();
        if (text.Length > MaxEmbedChars)
            text = text[..MaxEmbedChars];

        // Append the description to the on-disk capture text file so it holds both representations.
        if (!string.IsNullOrWhiteSpace(description))
            AppendDescriptionToFile(capture.TextPath, description);

        await StoreMemoryNodeAsync(
            name: string.IsNullOrWhiteSpace(capture.WindowTitle) ? "Capture" : capture.WindowTitle,
            labels: new List<string> { "Capture" },
            data: new
            {
                capture.ImagePath,
                capture.TextPath,
                capture.WindowTitle,
                SnapshotDescription = description,
                CapturedUtc = DateTime.UtcNow,
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
        if (text.Length > MaxEmbedChars)
            text = text[..MaxEmbedChars];

        await StoreMemoryNodeAsync(
            name: ShortTitle(text),
            labels: new List<string> { "Note" },
            data: new { Source = "note", CapturedUtc = DateTime.UtcNow },
            content: text,
            config: config,
            cancellationToken: cancellationToken);
        return true;
    }

    // Embeds <paramref name="content"/> and stores it as a single graph node with the given metadata.
    private async Task StoreMemoryNodeAsync(
        string name,
        List<string> labels,
        object data,
        string content,
        FloatyConfig config,
        CancellationToken cancellationToken)
    {
        var generator = GetOrCreateEmbeddings(config);
        var embeddings = await generator.GenerateAsync([content], cancellationToken: cancellationToken);
        var vector = embeddings[0].Vector.ToArray().ToList();

        var client = await GetClientAsync(cancellationToken);

        var node = new Node
        {
            TenantGUID = TenantGuid,
            GraphGUID = GraphGuid,
            Name = name,
            Labels = labels,
            Data = data,
            Vectors = new List<VectorMetadata>
            {
                new()
                {
                    TenantGUID = TenantGuid,
                    GraphGUID = GraphGuid,
                    Model = config.EmbeddingModel,
                    Dimensionality = vector.Count,
                    Content = content,
                    Vectors = vector,
                },
            },
        };

        await client.Node.Create(node, cancellationToken);
    }

    private static string ShortTitle(string text)
    {
        var firstLine = text.ReplaceLineEndings(" ").Trim();
        return firstLine.Length <= 60 ? firstLine : firstLine[..60] + "…";
    }

    // Describes the screenshot using the configured vision (snapshot) model. Returns null when
    // snapshotting is disabled, the image is missing, or the call fails — capture/embed still proceed.
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
                    "Describe this screenshot: the application shown, the visible UI, and any notable " +
                    "on-screen content. Be factual and concise."),
                new DataContent(imageBytes, "image/png"),
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

    private static void AppendDescriptionToFile(string textPath, string description)
    {
        try
        {
            if (File.Exists(textPath))
                File.AppendAllText(textPath, $"\n\n## Screenshot description\n{description}\n");
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
            TopK = topK,
            Embeddings = queryVector,
        };

        var results = new List<CaptureSearchResult>();
        await foreach (var hit in client.Vector.Search(request, cancellationToken))
        {
            if (hit.Node is null)
                continue;

            // Re-read the hit with its data + subordinates so we recover the embedded text and metadata.
            var node = await client.Node.ReadByGuid(
                TenantGuid, GraphGuid, hit.Node.GUID,
                includeData: true, includeSubordinates: true, cancellationToken);

            var content = node?.Vectors?.FirstOrDefault()?.Content ?? string.Empty;
            var (imagePath, textPath, capturedUtc) = ParseCaptureData(node?.Data);

            results.Add(new CaptureSearchResult(
                Title: node?.Name ?? hit.Node.Name ?? "Capture",
                CapturedUtc: capturedUtc,
                Score: hit.Score,
                ImagePath: imagePath,
                TextPath: textPath,
                Content: content));
        }

        return results;
    }

    // Best-effort extraction of the metadata we stored in Node.Data. Survives whatever concrete type
    // LiteGraph rehydrates Data into by round-tripping through JSON.
    private static (string? ImagePath, string? TextPath, DateTime? CapturedUtc) ParseCaptureData(object? data)
    {
        if (data is null)
            return (null, null, null);

        try
        {
            var element = JsonSerializer.SerializeToElement(data);

            string? Str(string name) =>
                element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

            DateTime? capturedUtc = element.TryGetProperty("CapturedUtc", out var c) && c.TryGetDateTime(out var dt)
                ? dt
                : null;

            return (Str("ImagePath"), Str("TextPath"), capturedUtc);
        }
        catch
        {
            return (null, null, null);
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
