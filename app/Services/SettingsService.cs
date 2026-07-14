using System.Text.Json;

namespace Floaty.Services;

/// <summary>
/// Loads and persists <see cref="FloatyConfig"/> to <c>~/.floaty/config.json</c>.
/// Registered as a singleton so the whole app shares one cached <see cref="Current"/> instance.
/// </summary>
public sealed class SettingsService
{
    private static readonly string[] BuiltInRingImages =
    [
        "ring1.png",
        "ring2.png",
        "ring3.png",
        "ring4.png",
        "ring5.png",
        "ring6.png",
        "ring7.png",
    ];

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

    /// <summary>Smallest allowed ring diameter (device-independent units).</summary>
    public const double RingMinSize = 96;

    /// <summary>Largest allowed ring diameter (device-independent units).</summary>
    public const double RingMaxSize = 288;

    /// <summary>Default ring diameter used when unset or out of range.</summary>
    public const double RingDefaultSize = 148;

    private readonly string _configPath;
    private readonly string _systemPromptPath;
    private FloatyConfig? _current;

    /// <summary>Raised after <see cref="Save"/> writes new config, so dependents (e.g. ChatService) can refresh.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Raised while the user drags the ring-size slider so the live overlay can preview the size
    /// without persisting. The argument is the requested (unclamped) diameter in device-independent units.
    /// </summary>
    public event EventHandler<double>? RingSizePreviewRequested;

    /// <summary>Requests a transient ring-size preview on the live overlay (see <see cref="RingSizePreviewRequested"/>).</summary>
    public void PreviewRingSize(double size) => RingSizePreviewRequested?.Invoke(this, size);

    /// <summary>Clamps a ring diameter into the supported range, falling back to the default when unset/invalid.</summary>
    public static double ClampRingSize(double size) =>
        size <= 0 ? RingDefaultSize : Math.Clamp(size, RingMinSize, RingMaxSize);

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

    /// <summary>Returns built-in ring image resource names packaged with the app.</summary>
    public IReadOnlyList<string> GetBuiltInRingImages() => BuiltInRingImages;

    /// <summary>True when the configured ring image points at a built-in packaged resource.</summary>
    public bool IsBuiltInRingImage(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName) &&
        BuiltInRingImages.Contains(fileName, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns whether a ring image selection is valid (built-in resource or existing custom file).
    /// Empty selection is valid and means use default ring.
    /// </summary>
    public bool IsValidRingSelection(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return true;

        if (IsBuiltInRingImage(fileName))
            return true;

        return GetRingImageFullPath(fileName) is not null;
    }

    /// <summary>
    /// Returns a base64 data URL for a configured ring image selection, or null when it cannot be resolved.
    /// </summary>
    public async Task<string?> GetRingImageDataUrlAsync(string? fileName)
    {
        if (IsBuiltInRingImage(fileName))
            return await GetBuiltInRingImageDataUrlAsync(fileName);

        var fullPath = GetRingImageFullPath(fileName);
        if (fullPath is null)
            return null;

        try
        {
            var bytes = await File.ReadAllBytesAsync(fullPath);
            return ToDataUrl(bytes, GetMimeType(fileName));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns a base64 data URL for a built-in ring image packaged in app resources, or null when unavailable.
    /// </summary>
    public async Task<string?> GetBuiltInRingImageDataUrlAsync(string fileName)
    {
        if (!IsBuiltInRingImage(fileName))
            return null;

        try
        {
            var stream = await TryOpenBuiltInRingStreamAsync(fileName);
            if (stream is null)
                return null;

            await using (stream)
            {
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                return ToDataUrl(ms.ToArray(), GetMimeType(fileName));
            }
        }
        catch
        {
            return null;
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

    private static string ToDataUrl(byte[] bytes, string mimeType) =>
        $"data:{mimeType};base64,{Convert.ToBase64String(bytes)}";

    private static async Task<Stream?> TryOpenBuiltInRingStreamAsync(string fileName)
    {
        // MauiAsset with LogicalName="%(Filename)%(Extension)" resolves with bare filename.
        try
        {
            return await FileSystem.OpenAppPackageFileAsync(fileName);
        }
        catch (FileNotFoundException)
        {
            // Some targets/package layouts may keep the source-relative path.
            try
            {
                return await FileSystem.OpenAppPackageFileAsync($"Resources/Images/{fileName}");
            }
            catch (FileNotFoundException)
            {
                return null;
            }
        }
    }

    private static string GetMimeType(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return extension.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            _ => "application/octet-stream",
        };
    }
}
