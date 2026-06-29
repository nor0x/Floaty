using Microsoft.Extensions.AI;

namespace Floaty.Services;

/// <summary>
/// Connects to the user's configured MCP servers and exposes their tools as <see cref="AIFunction"/>s
/// for the chat model. Tools are fetched per server (scoped to a <c>/name</c> slash command invocation).
/// </summary>
public interface IMcpService
{
    /// <summary>Enabled, named MCP servers from the current configuration (no connection performed).</summary>
    IReadOnlyList<McpServerConfig> EnabledServers { get; }

    /// <summary>
    /// Connects to the named server (once, then cached) and returns its tools. Returns an empty list
    /// when the server is unknown/disabled; throws when the server is configured but fails to connect,
    /// so the caller can surface the error.
    /// </summary>
    Task<IReadOnlyList<AIFunction>> GetToolsAsync(string serverName, CancellationToken cancellationToken = default);
}
