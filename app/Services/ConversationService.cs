using System.Text.Json;

namespace Floaty.Services;

/// <summary>A single persisted message within a <see cref="Conversation"/>.</summary>
public sealed class StoredMessage
{
    public bool IsUser { get; set; }
    public string Text { get; set; } = string.Empty;
    public bool IsSystemNote { get; set; }
    public List<MemoryCitation>? Citations { get; set; }
}

/// <summary>A saved chat thread, persisted as <c>~/.floaty/conversations/{Id}.json</c>.</summary>
public sealed class Conversation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public List<StoredMessage> Messages { get; set; } = new();
}

/// <summary>
/// Loads and persists chat threads as JSON files under <c>~/.floaty/conversations</c>. Registered as a
/// singleton; mirrors the file-IO style of <see cref="SettingsService"/>.
/// </summary>
public sealed class ConversationService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>All saved conversations, most recently updated first. Corrupt files are skipped.</summary>
    public IReadOnlyList<Conversation> LoadAll()
    {
        var result = new List<Conversation>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(FloatyPaths.Conversations, "*.json"))
            {
                var conversation = TryRead(file);
                if (conversation is not null)
                    result.Add(conversation);
            }
        }
        catch
        {
            // Missing/unreadable directory yields an empty list rather than crashing the chat UI.
        }

        return result.OrderByDescending(c => c.UpdatedUtc).ToList();
    }

    public Conversation? Load(string id) => TryRead(PathFor(id));

    public void Save(Conversation conversation)
    {
        File.WriteAllText(PathFor(conversation.Id), JsonSerializer.Serialize(conversation, JsonOptions));
    }

    public void Delete(string id)
    {
        try
        {
            var path = PathFor(id);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort: a failed delete shouldn't break the switcher.
        }
    }

    private static string PathFor(string id) => Path.Combine(FloatyPaths.Conversations, $"{id}.json");

    private static Conversation? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            return JsonSerializer.Deserialize<Conversation>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }
}
