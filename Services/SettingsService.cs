using System.Text.Json;

namespace Floaty.Services;

/// <summary>
/// Loads and persists <see cref="FloatyConfig"/> to <c>~/.floaty/config.json</c>.
/// Registered as a singleton so the whole app shares one cached <see cref="Current"/> instance.
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _configPath;
    private FloatyConfig? _current;

    /// <summary>Raised after <see cref="Save"/> writes new config, so dependents (e.g. ChatService) can refresh.</summary>
    public event EventHandler? Changed;

    public SettingsService()
    {
        _configPath = Path.Combine(FloatyPaths.Home, "config.json");
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
}
