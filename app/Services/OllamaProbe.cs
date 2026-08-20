using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Floaty.Services;

/// <summary>
/// Asks a local Ollama which models it has pulled, so the Ollama provider tab can offer a real list
/// instead of a text box the user has to remember model names for. Inference itself goes through
/// Ollama's OpenAI-compatible endpoint like any other provider — this only talks to its native
/// <c>/api/tags</c>, which the compatibility layer does not expose.
/// </summary>
public static class OllamaProbe
{
    // Short: a local daemon either answers immediately or isn't running, and this blocks a settings
    // tab from rendering its model list.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    /// <summary>
    /// Model names installed on the Ollama at <paramref name="baseUrl"/>, or an empty list when it
    /// isn't reachable. Never throws: "not running" is an ordinary state, not an error.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ListModelsAsync(
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        var root = ToApiRoot(baseUrl);
        if (root is null)
            return [];

        try
        {
            var response = await Http.GetFromJsonAsync<TagsResponse>(
                new Uri(root, "api/tags"), cancellationToken);

            return response?.Models
                ?.Select(m => m.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Turns the configured OpenAI-compatible endpoint (<c>http://localhost:11434/v1</c>) back into
    /// the server root the native API lives under, or null when it isn't a usable absolute URL.
    /// </summary>
    private static Uri? ToApiRoot(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            baseUrl = ProviderPresets.Find(ProviderPresets.OllamaId)!.BaseUrl;

        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri))
            return null;

        return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
    }

    private sealed class TagsResponse
    {
        [JsonPropertyName("models")]
        public List<TagEntry>? Models { get; set; }
    }

    private sealed class TagEntry
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
    }
}
