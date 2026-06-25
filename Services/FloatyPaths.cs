namespace Floaty.Services;

/// <summary>
/// Central location for Floaty's local-first data directory (<c>~/.floaty</c>) and its subfolders.
/// Each accessor ensures the directory exists before returning it. See readme.md ("Local First Approach").
/// </summary>
public static class FloatyPaths
{
    /// <summary><c>~/.floaty</c> — root for config, memory, logs and captures.</summary>
    public static string Home => EnsureDir(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".floaty"));

    /// <summary><c>~/.floaty/floaty.md</c> — user-editable system prompt for chat behavior.</summary>
    public static string SystemPrompt => Path.Combine(Home, "floaty.md");

    /// <summary><c>~/.floaty/captures</c> — screenshot + accessibility-content pairs from the 📷 button.</summary>
    public static string Captures => EnsureDir(Path.Combine(Home, "captures"));

    private static string EnsureDir(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
