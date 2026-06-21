using Microsoft.Extensions.AI;
using OpenAI;

namespace Floaty.Services;

/// <summary>
/// Sends a conversation to the configured LLM and returns the assistant's reply text.
/// </summary>
public interface IChatService
{
    Task<string> GetResponseAsync(
        IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Microsoft.Extensions.AI-backed chat service. Builds an <see cref="IChatClient"/> from the
/// OpenAI settings on demand and rebuilds it whenever the configuration changes.
/// </summary>
public sealed class ChatService : IChatService
{
    private readonly SettingsService _settings;
    private IChatClient? _client;
    private string? _clientKey;
    private string? _clientModel;

    public ChatService(SettingsService settings)
    {
        _settings = settings;
        // Drop the cached client when settings change so the next call picks up the new key/model.
        _settings.Changed += (_, _) => _client = null;
    }

    public async Task<string> GetResponseAsync(
        IReadOnlyList<ChatMessage> history,
        CancellationToken cancellationToken = default)
    {
        var config = _settings.Current;

        if (string.IsNullOrWhiteSpace(config.OpenAiApiKey))
            return "Add your OpenAI API key in Settings (⚙) to start chatting.";

        try
        {
            var client = GetOrCreateClient(config);
            var response = await client.GetResponseAsync(history, cancellationToken: cancellationToken);
            return response.Text;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"⚠️ {ex.Message}";
        }
    }

    private IChatClient GetOrCreateClient(FloatyConfig config)
    {
        if (_client is not null && _clientKey == config.OpenAiApiKey && _clientModel == config.Model)
            return _client;

        _clientKey = config.OpenAiApiKey;
        _clientModel = config.Model;
        _client = new OpenAIClient(config.OpenAiApiKey)
            .GetChatClient(config.Model)
            .AsIChatClient();
        return _client;
    }
}
