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

    /// <summary><c>~/.floaty/ring</c> — user-provided ring images available in Appearance settings.</summary>
    public static string RingImages => EnsureDir(Path.Combine(Home, "ring"));

    /// <summary><c>~/.floaty/conversations</c> — one JSON file per saved chat thread.</summary>
    public static string Conversations => EnsureDir(Path.Combine(Home, "conversations"));

    /// <summary><c>~/.floaty/skills</c> — user-placed agent skills (each a folder with a SKILL.md).</summary>
    public static string Skills => EnsureDir(Path.Combine(Home, "skills"));

    /// <summary><c>~/.floaty/models</c> — downloaded local speech-to-text models, one folder per model id.</summary>
    public static string SttModels => EnsureDir(Path.Combine(Home, "models"));

    /// <summary><c>~/.floaty/native</c> — downloaded native runtimes (transcribe.cpp), one folder per version.</summary>
    public static string NativeRuntimes => EnsureDir(Path.Combine(Home, "native"));

    private static string EnsureDir(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
