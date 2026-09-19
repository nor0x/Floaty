using System.ComponentModel;
using System.Text;
using Floaty.IconFont;
using Microsoft.Extensions.AI;

namespace Floaty.Services.Tools;

/// <summary>
/// "Is there an update?", "what's new?" and "update yourself" from chat. Reading is free; installing
/// restarts Floaty mid-conversation, so it goes through the approval card first.
/// </summary>
public sealed class UpdateTools : IChatToolset
{
    // Release notes are markdown written for humans and can run long; this is plenty for a summary.
    private const int MaxNotesChars = 6000;

    private readonly UpdateService _updates;

    public UpdateTools(UpdateService updates)
    {
        _updates = updates;

        Tools =
        [
            AIFunctionFactory.Create(CheckForUpdates, name: "check_for_updates"),
            AIFunctionFactory.Create(GetReleaseNotes, name: "get_release_notes"),
            AIFunctionFactory.Create(InstallUpdate, name: "install_update"),
        ];
    }

    public bool IsAvailable => true;

    public IReadOnlyList<AITool> Tools { get; }

    public string Guidance =>
        "When the user asks about Floaty's version, updates or what changed, use check_for_updates and " +
        "get_release_notes and summarize the notes in plain words rather than pasting them. Only call " +
        "install_update when the user asks to update; it restarts Floaty after they confirm in the UI.";

    [Description("Check whether a newer version of Floaty is available, and show its release notes.")]
    private async Task<string> CheckForUpdates()
    {
        var current = _updates.CurrentVersion;
        var result = await _updates.CheckAsync();

        if (result.Error is not null)
            return $"Running Floaty {current}. The update check could not run: {result.Error} " +
                   "get_release_notes still works for reading what's new.";

        if (!result.UpdateAvailable)
            return $"Floaty {current} is the latest version.";

        var sb = new StringBuilder($"Floaty {result.TargetVersion} is available (running {current}).");
        if (!string.IsNullOrWhiteSpace(result.NotesMarkdown))
            sb.Append("\n\nRelease notes:\n").Append(Trim(result.NotesMarkdown));
        sb.Append("\n\nCall install_update if the user wants it.");
        return sb.ToString();
    }

    [Description("Read Floaty's release notes from GitHub. With no version, returns the notes for the " +
                 "running version plus anything newer; pass a version such as '0.2.0' for that release, " +
                 "or 'all' for the recent history.")]
    private async Task<string> GetReleaseNotes(
        [Description("A version like '0.2.0', 'latest', 'all', or empty for the running version and newer.")] string? version = null)
    {
        var (releases, error) = await _updates.GetReleasesAsync();
        if (error is not null)
            return $"Could not load release notes: {error} They are also at {_updates.ReleasesUrl}.";
        if (releases.Count == 0)
            return $"No releases were found. See {_updates.ReleasesUrl}.";

        var current = _updates.CurrentVersion;
        var wanted = version?.Trim().TrimStart('v', 'V');

        IEnumerable<ReleaseNotes> picked;
        if (string.Equals(wanted, "all", StringComparison.OrdinalIgnoreCase))
        {
            picked = releases;
        }
        else if (string.Equals(wanted, "latest", StringComparison.OrdinalIgnoreCase))
        {
            picked = releases.Take(1);
        }
        else if (!string.IsNullOrWhiteSpace(wanted))
        {
            var match = releases.FirstOrDefault(r => string.Equals(r.Version, wanted, StringComparison.OrdinalIgnoreCase));
            if (match is null)
                return $"No release '{version}'. Available: {string.Join(", ", releases.Select(r => r.Version))}.";
            picked = [match];
        }
        else
        {
            // Releases come newest first, so everything up to and including the running one is
            // "this version and what came after it". A dev build that matches nothing gets the latest.
            var index = releases.ToList().FindIndex(r => IsSameVersion(r.Version, current));
            picked = index >= 0 ? releases.Take(index + 1) : releases.Take(1);
        }

        var sb = new StringBuilder($"Running Floaty {current}.");
        foreach (var r in picked)
        {
            sb.Append("\n\n## ").Append(r.Name);
            if (r.PublishedAt is { } at)
                sb.Append(" (").Append(at.ToLocalTime().ToString("d MMMM yyyy")).Append(')');
            sb.Append('\n').Append(string.IsNullOrWhiteSpace(r.Markdown) ? "(no notes)" : r.Markdown.Trim());
            sb.Append("\n").Append(r.Url);

            if (sb.Length > MaxNotesChars)
                break;
        }

        return Trim(sb.ToString());
    }

    [Description("Download and install the newer Floaty version found by check_for_updates, then restart " +
                 "Floaty. The user confirms in the UI first. Only call when the user asked to update.")]
    private async Task<string> InstallUpdate()
    {
        if (!_updates.IsSupported)
            return "This build of Floaty can't update itself (it isn't an installed release). " +
                   $"The user can download the latest from {_updates.ReleasesUrl}.";

        // Refresh rather than trust an older check: the pending update is what gets applied.
        var result = await _updates.CheckAsync();
        if (result.Error is not null)
            return $"The update check failed: {result.Error}";
        if (!result.UpdateAvailable)
            return $"Floaty {_updates.CurrentVersion} is already the latest version.";

        var approved = await ToolApproval.RequestAsync(new ToolApprovalRequest(
            Header: $"Update Floaty to {result.TargetVersion}?",
            Detail: $"{_updates.CurrentVersion} → {result.TargetVersion}",
            SubDetail: "Floaty downloads the update, then restarts.",
            ConfirmLabel: "Update & restart",
            ApprovedNote: $"⬇️ Updating to {result.TargetVersion}…",
            DeclinedNote: "🚫 Update postponed",
            Icon: TablerLine.Download));

        if (approved is null)
            return "Cannot install the update: no approval channel is available in this context.";
        if (approved == false)
            return "The user postponed the update.";

        try
        {
            await _updates.DownloadAsync();
            if (!_updates.IsUpdatePending)
                return "The download did not complete. The user can retry from Settings → Updates.";

            // Restarting from inside a tool call is fine: the process ends here, and the reply the
            // model would have written is moot. The approval note already told the user why.
            _updates.ApplyAndRestart();
            return $"Installing {result.TargetVersion}; Floaty is restarting.";
        }
        catch (Exception ex)
        {
            return $"The update failed: {ex.Message}";
        }
    }

    private static bool IsSameVersion(string a, string b) =>
        Version.TryParse(a, out var va) && Version.TryParse(b, out var vb)
            ? va.Major == vb.Major && va.Minor == vb.Minor && Math.Max(va.Build, 0) == Math.Max(vb.Build, 0)
            : string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Trim(string text) =>
        text.Length <= MaxNotesChars ? text : text[..MaxNotesChars] + "\n[truncated]";
}
