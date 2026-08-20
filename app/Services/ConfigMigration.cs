namespace Floaty.Services;

/// <summary>
/// Brings an on-disk <c>config.json</c> forward to the shape the current build expects. Run from
/// <see cref="SettingsService"/> right after deserialization, before anything else reads the config.
/// Every step must be idempotent: it runs on every load, not once.
/// </summary>
public static class ConfigMigration
{
    /// <summary>
    /// Applies all pending migrations in place. Returns true when something changed, so the caller
    /// can decide whether the file is worth rewriting.
    /// </summary>
    public static bool Apply(FloatyConfig config)
    {
        var changed = MigrateLegacyProvider(config);
        changed |= DropDanglingRoles(config);
        return changed;
    }

    /// <summary>
    /// Folds the pre-multi-provider fields (a single OpenAI key plus three model ids) into a
    /// <see cref="ProviderProfile"/> and points all three roles at it, then clears the legacy fields
    /// so they stop being written. A blank <c>SnapshotModel</c> meant "don't caption screenshots",
    /// which is now expressed as an unassigned vision role.
    /// </summary>
    private static bool MigrateLegacyProvider(FloatyConfig config)
    {
        var hasLegacy =
            !string.IsNullOrWhiteSpace(config.OpenAiApiKey) ||
            !string.IsNullOrWhiteSpace(config.Model) ||
            !string.IsNullOrWhiteSpace(config.EmbeddingModel) ||
            !string.IsNullOrWhiteSpace(config.SnapshotModel) ||
            !string.IsNullOrWhiteSpace(config.Provider);

        if (!hasLegacy)
            return false;

        // Providers already populated means a newer build wrote this file and the legacy keys are
        // leftovers a user (or a downgrade) left behind. Drop them rather than clobbering the new shape.
        if (config.Providers.Count == 0)
        {
            var preset = ProviderPresets.Find(ProviderPresets.OpenAiId)!;
            var profile = ProviderPresets.CreateProfile(preset, config.Providers);

            profile.ApiKey = config.OpenAiApiKey ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(config.Model))
                profile.ChatModel = config.Model;
            if (!string.IsNullOrWhiteSpace(config.EmbeddingModel))
                profile.EmbeddingModel = config.EmbeddingModel;
            profile.VisionModel = config.SnapshotModel ?? string.Empty;

            config.Providers.Add(profile);
            config.ChatRole = new ModelAssignment { ProviderId = profile.Id, Model = profile.ChatModel };
            config.EmbeddingRole = new ModelAssignment { ProviderId = profile.Id, Model = profile.EmbeddingModel };
            config.VisionRole = string.IsNullOrWhiteSpace(profile.VisionModel)
                ? new ModelAssignment()
                : new ModelAssignment { ProviderId = profile.Id, Model = profile.VisionModel };
        }

        config.Provider = null;
        config.OpenAiApiKey = null;
        config.Model = null;
        config.EmbeddingModel = null;
        config.SnapshotModel = null;
        return true;
    }

    /// <summary>
    /// Unassigns roles pointing at a provider that no longer exists — a hand-edited config, or a
    /// profile removed by a build that missed one of the bindings. An unassigned role degrades to
    /// "feature off" everywhere, whereas a dangling one would fail on every call.
    /// </summary>
    private static bool DropDanglingRoles(FloatyConfig config)
    {
        var known = config.Providers.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = false;

        foreach (var role in new[] { config.ChatRole, config.EmbeddingRole, config.VisionRole })
        {
            if (role.IsAssigned && !known.Contains(role.ProviderId))
            {
                role.ProviderId = string.Empty;
                role.Model = string.Empty;
                changed = true;
            }
        }

        return changed;
    }
}
