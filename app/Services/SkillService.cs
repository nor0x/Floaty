namespace Floaty.Services;

/// <summary>
/// A discovered agent skill: a folder containing a <c>SKILL.md</c> (YAML frontmatter + markdown body).
/// <see cref="Instructions"/> is the body injected as system guidance when the skill is invoked.
/// </summary>
public sealed class FloatySkill
{
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Instructions { get; init; } = string.Empty;
    public string SourcePath { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// True for a skill shipped inside the app rather than found on disk; <see cref="SourcePath"/> is
    /// then empty. A same-named folder in one of the scanned roots overrides it.
    /// </summary>
    public bool IsBuiltIn { get; init; }
}

/// <summary>
/// Discovers SKILL.md-based agent skills from common locations (<c>~/.floaty/skills</c>,
/// <c>~/.claude/skills</c>, <c>~/.agents/skills</c>), plus the ones Floaty ships with, and exposes them
/// as enable-able skills the chat can scope to via a <c>/name</c> slash command.
/// </summary>
public sealed class SkillService
{
    private const int MaxScanDepth = 4;

    /// <summary>
    /// Skills packaged with the app, under <c>Resources/Skills/&lt;name&gt;/SKILL.md</c>. Listed by name
    /// rather than enumerated because <c>avares://</c> has no directory listing that stays cheap and
    /// stable across build layouts.
    /// </summary>
    private static readonly string[] BuiltInSkills = ["generate-image", "ring-image"];

    private readonly SettingsService _settings;
    private readonly IAppAssets _appAssets;
    private IReadOnlyList<FloatySkill>? _cache;

    public SkillService(SettingsService settings, IAppAssets appAssets)
    {
        _settings = settings;
        _appAssets = appAssets;
        _settings.Changed += (_, _) => _cache = null; // re-evaluate enabled state / pick up edits
    }

    /// <summary>All discovered skills (cached). <see cref="FloatySkill.Enabled"/> reflects the disabled list.</summary>
    public IReadOnlyList<FloatySkill> Skills => _cache ??= Scan();

    /// <summary>Forces a fresh scan from disk on the next access.</summary>
    public void Reload() => _cache = null;

    /// <summary>Returns the enabled skill with the given name (case-insensitive), or null.</summary>
    public FloatySkill? GetEnabled(string name) =>
        Skills.FirstOrDefault(s => s.Enabled && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> Roots()
    {
        // ~/.floaty/skills always exists (EnsureDir). The others are scanned only if present.
        yield return FloatyPaths.Skills;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(home, ".claude", "skills");
        yield return Path.Combine(home, ".agents", "skills");
    }

    private IReadOnlyList<FloatySkill> Scan()
    {
        var disabled = new HashSet<string>(_settings.Current.DisabledSkills, StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, FloatySkill>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in Roots())
        {
            if (!Directory.Exists(root))
                continue;

            foreach (var file in EnumerateSkillFiles(root))
            {
                var skill = TryParse(file, disabled);
                if (skill is null || byName.ContainsKey(skill.Name))
                    continue; // first occurrence of a name wins
                byName[skill.Name] = skill;
            }
        }

        // Last, so the same first-occurrence-wins rule lets a user's own ~/.floaty/skills/ring-image
        // replace the shipped one wholesale rather than fighting with it.
        foreach (var name in BuiltInSkills)
        {
            var skill = TryParseBuiltIn(name, disabled);
            if (skill is null || byName.ContainsKey(skill.Name))
                continue;
            byName[skill.Name] = skill;
        }

        return byName.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private FloatySkill? TryParseBuiltIn(string name, HashSet<string> disabled)
    {
        try
        {
            using var stream = _appAssets.Open($"Resources/Skills/{name}", "SKILL.md");
            if (stream is null)
                return null;

            using var reader = new StreamReader(stream);
            return Build(reader.ReadToEnd(), name, disabled, sourcePath: string.Empty, builtIn: true);
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> EnumerateSkillFiles(string root)
    {
        // Depth-limited recursive search for SKILL.md to avoid walking very large trees.
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();

            string[] files;
            try { files = Directory.GetFiles(dir, "SKILL.md"); }
            catch { continue; }

            foreach (var f in files)
                yield return f;

            if (depth >= MaxScanDepth)
                continue;

            string[] subdirs;
            try { subdirs = Directory.GetDirectories(dir); }
            catch { continue; }

            foreach (var sub in subdirs)
                queue.Enqueue((sub, depth + 1));
        }
    }

    private static FloatySkill? TryParse(string path, HashSet<string> disabled)
    {
        try
        {
            var content = File.ReadAllText(path);
            var folder = new DirectoryInfo(Path.GetDirectoryName(path)!).Name;
            return Build(content, folder, disabled, sourcePath: path, builtIn: false);
        }
        catch
        {
            return null; // malformed/unreadable skills are skipped
        }
    }

    /// <summary>
    /// Turns one SKILL.md's text into a skill. Shared by the disk and packaged sources so a bundled
    /// skill is parsed by exactly the same rules as a user's own.
    /// </summary>
    private static FloatySkill? Build(
        string content, string fallbackName, HashSet<string> disabled, string sourcePath, bool builtIn)
    {
        var (name, description, body) = ParseSkillMarkdown(content);

        // Fall back to the containing folder name when frontmatter has no name.
        if (string.IsNullOrWhiteSpace(name))
            name = fallbackName;
        if (string.IsNullOrWhiteSpace(name))
            return null;

        return new FloatySkill
        {
            Name = name.Trim(),
            Description = description.Trim(),
            Instructions = string.IsNullOrWhiteSpace(body) ? content.Trim() : body.Trim(),
            SourcePath = sourcePath,
            Enabled = !disabled.Contains(name.Trim()),
            IsBuiltIn = builtIn,
        };
    }

    // Splits a SKILL.md into (name, description, body). Frontmatter is the block between the first two
    // lines that are exactly "---"; body is everything after it. Frontmatter keys are simple "key: value".
    // Flat by design: only name and description are read, and everything the Agent Skills spec allows
    // besides them is ignored. Note that means a nested block's children are read as top-level keys — the
    // spec's "metadata:" map shows up here as keys named "author" and "version" — so anything added to
    // this parser later must not pick a name that a nested block could shadow.
    private static (string Name, string Description, string Body) ParseSkillMarkdown(string content)
    {
        var normalized = content.Replace("\r\n", "\n").Replace('\r', '\n');
        var name = string.Empty;
        var description = string.Empty;
        var body = normalized;

        if (normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            var end = normalized.IndexOf("\n---", 4, StringComparison.Ordinal);
            if (end > 0)
            {
                var frontmatter = normalized[4..end];
                body = normalized[(end + 4)..].TrimStart('\n');

                foreach (var raw in frontmatter.Split('\n'))
                {
                    var line = raw.Trim();
                    var colon = line.IndexOf(':');
                    if (colon <= 0)
                        continue;
                    var key = line[..colon].Trim();
                    var value = line[(colon + 1)..].Trim().Trim('"', '\'');
                    if (key.Equals("name", StringComparison.OrdinalIgnoreCase))
                        name = value;
                    else if (key.Equals("description", StringComparison.OrdinalIgnoreCase))
                        description = value;
                }
            }
        }

        return (name, description, body);
    }
}
