namespace Floaty.Services;

/// <summary>A single capture matched by semantic search over Floaty's memory.</summary>
public sealed record CaptureSearchResult(
    string Title,
    DateTime? CapturedUtc,
    float? Score,
    string? ImagePath,
    string? TextPath,
    string Content);

/// <summary>
/// Floaty's local memory: turns a capture into a text embedding and stores it (with metadata) in the
/// local LiteGraph vector+graph database under <c>~/.floaty/litegraph.db</c> for later semantic search.
/// </summary>
public interface IMemoryService
{
    /// <summary>
    /// Embeds the capture's content and stores it as a graph node. Returns <c>false</c> when there's
    /// nothing to do (no API key configured or empty content); throws on hard failures so the caller
    /// can surface the error.
    /// </summary>
    Task<bool> RememberCaptureAsync(CaptureResult capture, CancellationToken cancellationToken = default);

    /// <summary>
    /// Embeds <paramref name="query"/> and returns the most semantically similar stored captures.
    /// Returns an empty list when there's no API key or the query is blank.
    /// </summary>
    Task<IReadOnlyList<CaptureSearchResult>> SearchCapturesAsync(
        string query,
        int topK = 5,
        CancellationToken cancellationToken = default);
}
