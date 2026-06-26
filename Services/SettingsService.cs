using System.Text.Json;

namespace Floaty.Services;

/// <summary>
/// Loads and persists <see cref="FloatyConfig"/> to <c>~/.floaty/config.json</c>.
/// Registered as a singleton so the whole app shares one cached <see cref="Current"/> instance.
/// </summary>
public sealed class SettingsService
{
    private static readonly HashSet<string> RingImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg",
        ".webp",
        ".gif",
        ".bmp",
    };

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _configPath;
    private readonly string _systemPromptPath;
    private FloatyConfig? _current;

    /// <summary>Raised after <see cref="Save"/> writes new config, so dependents (e.g. ChatService) can refresh.</summary>
    public event EventHandler? Changed;

    public SettingsService()
    {
        _configPath = Path.Combine(FloatyPaths.Home, "config.json");
        _systemPromptPath = FloatyPaths.SystemPrompt;
    }

    /// <summary>The current configuration, loaded lazily from disk (defaults if the file is missing/invalid).</summary>
    public FloatyConfig Current => _current ??= Load();

    private FloatyConfig Load()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                var config = JsonSerializer.Deserialize<FloatyConfig>(json);
                if (config is not null)
                    return config;
            }
        }
        catch
        {
            // Corrupt or unreadable config falls back to defaults rather than crashing the app.
        }

        return new FloatyConfig();
    }

    /// <summary>Persists the given config to disk, updates the cache, and raises <see cref="Changed"/>.</summary>
    public void Save(FloatyConfig config)
    {
        _current = config;
        File.WriteAllText(_configPath, JsonSerializer.Serialize(config, JsonOptions));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Loads the user system prompt from disk, falling back to <paramref name="defaultPrompt"/> when missing/empty.</summary>
    public string GetSystemPrompt(string defaultPrompt)
    {
        try
        {
            if (File.Exists(_systemPromptPath))
            {
                var prompt = File.ReadAllText(_systemPromptPath);
                if (!string.IsNullOrWhiteSpace(prompt))
                    return prompt;
            }
        }
        catch
        {
            // Falls back to the shipped prompt when the file cannot be read.
        }

        return defaultPrompt;
    }

    /// <summary>Saves the user system prompt to <c>~/.floaty/floaty.md</c>.</summary>
    public void SaveSystemPrompt(string prompt)
    {
        File.WriteAllText(_systemPromptPath, prompt ?? string.Empty);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Returns ring image filenames from <c>~/.floaty/ring</c>.</summary>
    public IReadOnlyList<string> GetAvailableRingImages()
    {
        try
        {
            return Directory
                .EnumerateFiles(FloatyPaths.RingImages)
                .Where(path => RingImageExtensions.Contains(Path.GetExtension(path)))
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Cast<string>()
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Resolves a configured ring image filename to a full path in <c>~/.floaty/ring</c>, or null when invalid/missing.
    /// </summary>
    public string? GetRingImageFullPath(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        var safeName = Path.GetFileName(fileName);
        if (!string.Equals(fileName, safeName, StringComparison.Ordinal))
            return null;

        if (!RingImageExtensions.Contains(Path.GetExtension(safeName)))
            return null;

        var fullPath = Path.Combine(FloatyPaths.RingImages, safeName);
        return File.Exists(fullPath) ? fullPath : null;
    }
}
