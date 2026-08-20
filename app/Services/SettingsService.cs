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

    /// <summary>
    /// Sound effects packaged with the app (see <c>Resources\Sounds</c> and its CREDITS.md). One flat
    /// pool: any of them can be assigned to either the capture or the assistant-reply slot.
    /// </summary>
    private static readonly string[] BuiltInSounds =
    [
        "shutter.wav",
        "shutter-double.wav",
        "camera-phone.wav",
        "notify.wav",
        "chime.wav",
    ];

    /// <summary>
    /// What <see cref="ISoundService"/> can decode: WAV natively, MP3 through the platform codec.
    /// </summary>
    private static readonly HashSet<string> SoundExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".wav",
        ".mp3",
    };

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Built-in used when <see cref="FloatyConfig.CaptureSoundFileName"/> is unset.</summary>
    public const string DefaultCaptureSound = "shutter.wav";

    /// <summary>Built-in used when <see cref="FloatyConfig.AssistantDoneSoundFileName"/> is unset.</summary>
    public const string DefaultAssistantDoneSound = "notify.wav";

    /// <summary>Smallest allowed ring diameter (device-independent units).</summary>
    public const double RingMinSize = 50;

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

    /// <summary>
    /// Raised while the user picks an accent color so the live overlay can preview it
    /// without persisting. The argument is the requested hex color.
    /// </summary>
    public event EventHandler<string>? AccentColorPreviewRequested;

    /// <summary>Requests a transient accent-color preview on the live overlay (see <see cref="AccentColorPreviewRequested"/>).</summary>
    public void PreviewAccentColor(string hex) => AccentColorPreviewRequested?.Invoke(this, hex);

    /// <summary>
    /// Raised when the Sounds settings page wants to audition a sound. Routing the preview back
    /// through <see cref="ISoundService"/> (rather than playing it in the settings WebView) means the
    /// user hears it on the same device, at the same volume, as the real thing.
    /// </summary>
    public event EventHandler<(string FileName, double Volume)>? SoundPreviewRequested;

    /// <summary>Requests a one-off sound preview at an un-persisted volume (see <see cref="SoundPreviewRequested"/>).</summary>
    public void PreviewSound(string fileName, double volume) =>
        SoundPreviewRequested?.Invoke(this, (fileName, ClampSoundVolume(volume)));

    /// <summary>Normalizes an accent hex color, falling back to the default when unset/invalid.</summary>
    public static string NormalizeAccentColor(string? hex) => AccentPalette.Normalize(hex);

    /// <summary>Clamps a ring diameter into the supported range, falling back to the default when unset/invalid.</summary>
    public static double ClampRingSize(double size) =>
        size <= 0 ? RingDefaultSize : Math.Clamp(size, RingMinSize, RingMaxSize);

    /// <summary>Clamps a playback volume to 0–1; NaN (a malformed config.json) falls back to silent-safe 0.7.</summary>
    public static double ClampSoundVolume(double volume) =>
        double.IsNaN(volume) ? 0.7 : Math.Clamp(volume, 0, 1);

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
                {
                    // Bring an older file forward before anything else reads it. Not written back
                    // here — the next Save persists the new shape, and until then the in-memory
                    // config is already correct.
                    ConfigMigration.Apply(config);
                    return config;
                }
            }
        }
        catch
        {
            // Corrupt or unreadable config falls back to defaults rather than crashing the app.
        }

        // A fresh config has no providers at all, which is exactly the "not configured yet" state
        // the AiClientFactory and the Settings UI expect — nothing to migrate.
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
            var stream = await TryOpenPackagedAssetAsync(fileName, "Resources/Images");
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

    // --- Sound effects (~/.floaty/sounds + packaged built-ins). Mirrors the ring image block above. ---

    /// <summary>Returns sound filenames the user dropped into <c>~/.floaty/sounds</c>.</summary>
    public IReadOnlyList<string> GetAvailableSounds()
    {
        try
        {
            return Directory
                .EnumerateFiles(FloatyPaths.Sounds)
                .Where(path => SoundExtensions.Contains(Path.GetExtension(path)))
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

    /// <summary>Returns the sound names packaged with the app.</summary>
    public IReadOnlyList<string> GetBuiltInSounds() => BuiltInSounds;

    /// <summary>True when a sound selection names a built-in packaged asset.</summary>
    public bool IsBuiltInSound(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName) &&
        BuiltInSounds.Contains(fileName, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a sound selection still resolves (built-in or an existing custom file). An empty
    /// selection is valid and means "use the slot's default built-in".
    /// </summary>
    public bool IsValidSoundSelection(string? fileName) =>
        string.IsNullOrWhiteSpace(fileName) || IsBuiltInSound(fileName) || GetSoundFullPath(fileName) is not null;

    /// <summary>
    /// Resolves a sound filename to a full path in <c>~/.floaty/sounds</c>, or null when it is a
    /// built-in, escapes the folder, has an unsupported extension, or no longer exists.
    /// </summary>
    public string? GetSoundFullPath(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        var safeName = Path.GetFileName(fileName);
        if (!string.Equals(fileName, safeName, StringComparison.Ordinal))
            return null;

        if (!SoundExtensions.Contains(Path.GetExtension(safeName)))
            return null;

        var fullPath = Path.Combine(FloatyPaths.Sounds, safeName);
        return File.Exists(fullPath) ? fullPath : null;
    }

    /// <summary>
    /// Opens the bytes behind a sound selection — the packaged asset for a built-in, the file on disk
    /// for a custom one — or null when it cannot be resolved. Callers own the returned stream.
    /// </summary>
    public async Task<Stream?> OpenSoundStreamAsync(string? fileName)
    {
        if (IsBuiltInSound(fileName))
            return await TryOpenPackagedAssetAsync(fileName!, "Resources/Sounds");

        var fullPath = GetSoundFullPath(fileName);
        if (fullPath is null)
            return null;

        try
        {
            return File.OpenRead(fullPath);
        }
        catch
        {
            return null;
        }
    }

    private static string ToDataUrl(byte[] bytes, string mimeType) =>
        $"data:{mimeType};base64,{Convert.ToBase64String(bytes)}";

    /// <summary>
    /// Opens a packaged <c>MauiAsset</c> by bare filename. <paramref name="sourceFolder"/> is the
    /// project-relative folder it was declared in, used only for the fallback lookup.
    /// </summary>
    private static async Task<Stream?> TryOpenPackagedAssetAsync(string fileName, string sourceFolder)
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
                return await FileSystem.OpenAppPackageFileAsync($"{sourceFolder}/{fileName}");
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
