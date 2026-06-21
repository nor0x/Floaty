namespace Floaty.Services;

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
}
