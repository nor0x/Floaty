namespace Floaty.Services;

/// <summary>
/// Reads files that ship inside the app itself (ring images, built-in sound effects) rather than out
/// of <c>~/.floaty</c>.
/// </summary>
/// <remarks>
/// This exists so <see cref="SettingsService"/> stays free of UI-framework types, per the convention
/// that everything in <c>Services/</c> is portable and only <c>Platforms/</c> knows the framework.
/// It replaces MAUI's <c>FileSystem.OpenAppPackageFileAsync</c>, which had to be probed with two
/// candidate paths because <c>MauiAsset</c> logical names varied by package layout; Avalonia's
/// <c>avares://</c> URIs are exact, so a single lookup is enough.
/// </remarks>
public interface IAppAssets
{
    /// <summary>
    /// Opens a packaged asset, or returns null when it does not exist. Callers own the stream.
    /// </summary>
    /// <param name="folder">Project-relative folder the asset was declared in, e.g. <c>Resources/Images</c>.</param>
    /// <param name="fileName">Bare file name, e.g. <c>daisy.png</c>.</param>
    Stream? Open(string folder, string fileName);
}
