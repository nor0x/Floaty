using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace Floaty.Services;

/// <summary>
/// Manages connections to the user's configured MCP servers (<see cref="FloatyConfig.McpServers"/>).
/// A server is connected on first use and its <see cref="McpClient"/> + tools are cached for the
/// session; the cache is cleared (and clients disposed) whenever settings change.
/// </summary>
public sealed class McpService : IMcpService
{
    private readonly SettingsService _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Connected clients keyed by server name (case-insensitive).
    private readonly Dictionary<string, McpClient> _clients = new(StringComparer.OrdinalIgnoreCase);

    public McpService(SettingsService settings)
    {
        _settings = settings;
        _settings.Changed += (_, _) => _ = ResetAsync();
    }

    public IReadOnlyList<McpServerConfig> EnabledServers =>
        _settings.Current.McpServers
            .Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Name))
            .ToList();

    public async Task<IReadOnlyList<AIFunction>> GetToolsAsync(string serverName, CancellationToken cancellationToken = default)
    {
        var config = EnabledServers.FirstOrDefault(s =>
            string.Equals(s.Name, serverName, StringComparison.OrdinalIgnoreCase));
        if (config is null)
            return Array.Empty<AIFunction>();

        var client = await GetOrConnectAsync(config, cancellationToken);
        var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
        return tools.Cast<AIFunction>().ToList();
    }

    private async Task<McpClient> GetOrConnectAsync(McpServerConfig config, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_clients.TryGetValue(config.Name, out var existing))
                return existing;

            var transport = CreateTransport(config);
            var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
            _clients[config.Name] = client;
            return client;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static IClientTransport CreateTransport(McpServerConfig config)
    {
        if (string.Equals(config.Transport, "http", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(config.Url) || !Uri.TryCreate(config.Url, UriKind.Absolute, out var endpoint))
                throw new InvalidOperationException($"MCP server '{config.Name}' has an invalid URL.");

            return new HttpClientTransport(new HttpClientTransportOptions
            {
                Name = config.Name,
                Endpoint = endpoint,
                AdditionalHeaders = config.Headers.Count > 0 ? new Dictionary<string, string>(config.Headers) : null,
            });
        }

        if (string.IsNullOrWhiteSpace(config.Command))
            throw new InvalidOperationException($"MCP server '{config.Name}' has no command configured.");

        return new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = config.Name,
            Command = config.Command,
            Arguments = config.Args.Count > 0 ? config.Args.ToList() : null,
            EnvironmentVariables = config.Env.Count > 0
                ? config.Env.ToDictionary(kv => kv.Key, kv => (string?)kv.Value)
                : null,
        });
    }

    private async Task ResetAsync()
    {
        await _gate.WaitAsync();
        try
        {
            foreach (var client in _clients.Values)
            {
                try
                {
                    await client.DisposeAsync();
                }
                catch
                {
                    // Best-effort teardown; a failing dispose shouldn't block re-configuration.
                }
            }

            _clients.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }
}
