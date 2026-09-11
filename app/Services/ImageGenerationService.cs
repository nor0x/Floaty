using OpenAI.Images;

namespace Floaty.Services;

/// <summary>One generated image, already written to <see cref="FloatyPaths.GeneratedImages"/>.</summary>
public sealed record GeneratedImageFile(string FileName, string FullPath, string Model, string? RevisedPrompt);

/// <summary>
/// Turns a prompt into a PNG on disk using whichever provider holds <see cref="ModelRole.Image"/>.
/// </summary>
public interface IImageGenerationService
{
    /// <summary>Whether the image role points at a provider that can actually serve it right now.</summary>
    bool IsConfigured { get; }

    /// <summary>Generates an image from a text prompt.</summary>
    Task<GeneratedImageFile> GenerateAsync(
        string prompt,
        string? size = null,
        bool transparentBackground = false,
        CancellationToken cancellationToken = default);

    /// <summary>Restyles an existing image file according to a prompt.</summary>
    Task<GeneratedImageFile> EditAsync(
        string sourcePath,
        string prompt,
        string? size = null,
        bool transparentBackground = false,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The OpenAI images endpoint, which every provider Floaty can bind to the image role speaks: OpenAI
/// itself (<c>gpt-image-1</c>, <c>dall-e-3</c>), Google's compatibility layer
/// (<c>gemini-2.5-flash-image</c>, "nano banana") and any gateway in between. There is no
/// <c>Microsoft.Extensions.AI</c> abstraction for image generation, so unlike chat and embeddings this
/// talks to the SDK client directly.
/// </summary>
public sealed class ImageGenerationService : IImageGenerationService
{
    // Smallest and largest edge a caller-supplied size may ask for. The provider rejects most values
    // anyway; this only stops an absurd request from being sent at all.
    private const int MinEdge = 256;
    private const int MaxEdge = 4096;

    private readonly AiClientFactory _clients;

    public ImageGenerationService(AiClientFactory clients) => _clients = clients;

    public bool IsConfigured => _clients.IsConfigured(ModelRole.Image);

    public async Task<GeneratedImageFile> GenerateAsync(
        string prompt,
        string? size = null,
        bool transparentBackground = false,
        CancellationToken cancellationToken = default)
    {
        var (client, model) = Require();

        var options = new ImageGenerationOptions();
        ApplyCommonOptions(model, size, transparentBackground,
            format => options.ResponseFormat = format,
            parsed => options.Size = parsed,
            setTransparentPng: () =>
            {
#pragma warning disable OPENAI001 // Same experimental surface AiClientFactory already opts into.
                options.Background = GeneratedImageBackground.Transparent;
                options.OutputFileFormat = GeneratedImageFileFormat.Png;
#pragma warning restore OPENAI001
            });

        var generated = (await client.GenerateImageAsync(prompt, options, cancellationToken)).Value;
        return await SaveAsync(generated, model, cancellationToken);
    }

    public async Task<GeneratedImageFile> EditAsync(
        string sourcePath,
        string prompt,
        string? size = null,
        bool transparentBackground = false,
        CancellationToken cancellationToken = default)
    {
        var (client, model) = Require();

        var options = new ImageEditOptions();
        ApplyCommonOptions(model, size, transparentBackground,
            format => options.ResponseFormat = format,
            parsed => options.Size = parsed,
            setTransparentPng: () =>
            {
#pragma warning disable OPENAI001 // Same experimental surface AiClientFactory already opts into.
                options.Background = GeneratedImageBackground.Transparent;
                options.OutputFileFormat = GeneratedImageFileFormat.Png;
#pragma warning restore OPENAI001
            });

        await using var source = File.OpenRead(sourcePath);
        var generated = (await client.GenerateImageEditAsync(
            source, Path.GetFileName(sourcePath), prompt, options, cancellationToken)).Value;
        return await SaveAsync(generated, model, cancellationToken);
    }

    private (ImageClient Client, string Model) Require() =>
        _clients.GetImageClient()
        ?? throw new InvalidOperationException(
            "No image model is configured. Assign one in Settings → Model provider → Roles → Image.");

    /// <summary>
    /// The per-model quirks, in one place because generation and editing hit exactly the same ones while
    /// their option types are unrelated — hence the setters rather than a shared base.
    /// </summary>
    private static void ApplyCommonOptions(
        string model,
        string? size,
        bool transparentBackground,
        Action<GeneratedImageFormat> setResponseFormat,
        Action<GeneratedImageSize> setSize,
        Action setTransparentPng)
    {
        // gpt-image models reject response_format outright ("Unknown parameter: 'response_format'") and
        // always answer with base64. dall-e-3 defaults to a URL and has to be asked for bytes, and every
        // other OpenAI-shaped endpoint — Gemini's compatibility layer included — accepts b64_json. So the
        // split really is just "is this a gpt-image model". Everything else stays unset: the SDK's option
        // properties are nullable, so an unset one is never serialized into the request at all.
        var isGptImage = model.StartsWith("gpt-image", StringComparison.OrdinalIgnoreCase);
        if (!isGptImage)
            setResponseFormat(GeneratedImageFormat.Bytes);

        // background is a gpt-image-only parameter; sending it to dall-e-3 or Gemini is a 400.
        if (transparentBackground && isGptImage)
            setTransparentPng();

        if (TryParseSize(size, out var parsed))
            setSize(parsed);

        // Quality and Style are deliberately never set: the defaults are what the user wants, and most
        // compatible endpoints reject the values OpenAI accepts.
    }

    /// <summary>Parses "1024x1024" (or "1024×1024") into a size, or false for anything unusable.</summary>
    private static bool TryParseSize(string? size, out GeneratedImageSize parsed)
    {
        parsed = default!;

        if (string.IsNullOrWhiteSpace(size))
            return false;

        var parts = size.Trim().Split(['x', 'X', '×'], 2);
        if (parts.Length != 2
            || !int.TryParse(parts[0], out var width)
            || !int.TryParse(parts[1], out var height))
            return false;

        parsed = new GeneratedImageSize(
            Math.Clamp(width, MinEdge, MaxEdge),
            Math.Clamp(height, MinEdge, MaxEdge));
        return true;
    }

    private static async Task<GeneratedImageFile> SaveAsync(
        GeneratedImage generated, string model, CancellationToken cancellationToken)
    {
        var bytes = generated.ImageBytes?.ToArray();
        if (bytes is null || bytes.Length == 0)
        {
            // Only reachable from a provider that ignored response_format. Floaty deliberately never
            // fetches remote image content, so this is an error rather than a download.
            throw new InvalidOperationException(generated.ImageUri is not null
                ? "The provider returned an image URL instead of image data."
                : "The provider returned no image data.");
        }

        // Always .png: gpt-image, dall-e and Gemini all return PNG by default, and a single known
        // extension is what keeps GeneratedImageUri's allowlist trivial.
        var fileName = $"image-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.png";
        var fullPath = Path.Combine(FloatyPaths.GeneratedImages, fileName);
        await File.WriteAllBytesAsync(fullPath, bytes, cancellationToken);

        return new GeneratedImageFile(fileName, fullPath, model, generated.RevisedPrompt);
    }
}
