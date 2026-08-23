using Avalonia.Platform;
using Floaty.Services;

namespace Floaty.Platforms;

/// <summary>
/// <see cref="IAppAssets"/> over Avalonia's <c>avares://</c> resource scheme. The assets themselves
/// are declared as <c>AvaloniaResource</c> in Floaty.csproj.
/// </summary>
public sealed class AvaloniaAppAssets : IAppAssets
{
    private const string BaseUri = "avares://Floaty/";

    public Stream? Open(string folder, string fileName)
    {
        // Guard against a config value walking out of the packaged folder. Callers already validate,
        // but this is the boundary that actually resolves a name to a resource.
        var safeName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeName) || !string.Equals(fileName, safeName, StringComparison.Ordinal))
            return null;

        var uri = new Uri($"{BaseUri}{folder.Trim('/')}/{safeName}");

        // Exists() first: Open() throws for a missing resource, and a missing built-in is a normal
        // outcome here (a config that names a ring image from a newer build, say).
        return AssetLoader.Exists(uri) ? AssetLoader.Open(uri) : null;
    }
}
