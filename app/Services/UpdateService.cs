using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
#if WINDOWS
using Velopack;
using Velopack.Sources;
#endif

namespace Floaty.Services;

/// <summary>One published release as GitHub reports it.</summary>
public sealed record ReleaseNotes(string Version, string Name, DateTimeOffset? PublishedAt, string Markdown, string Url);

/// <summary>Outcome of an update check.</summary>
/// <param name="UpdateAvailable">True when a newer release than the running build exists.</param>
/// <param name="TargetVersion">The available (or current, when up to date) version string.</param>
/// <param name="Error">Non-null when the check could not complete.</param>
/// <param name="NotesHtml">Pre-rendered "what's new" HTML for the available release, when present.</param>
/// <param name="NotesMarkdown">Raw markdown "what's new" for the available release, when present.</param>
public sealed record UpdateCheckResult(
    bool UpdateAvailable,
    string? TargetVersion,
    string? Error = null,
    string? NotesHtml = null,
    string? NotesMarkdown = null);

/// <summary>
/// Wraps Velopack's <c>UpdateManager</c> against the GitHub Releases of <c>nor0x/Floaty</c>.
/// Registered as a singleton. Compiles on every target but only performs real work on Windows
/// installed builds; elsewhere (and during <c>dotnet run</c>) it reports "not supported".
/// </summary>
public sealed class UpdateService
{
    private const string RepoUrl = "https://github.com/nor0x/Floaty";
    private const string ReleasesApiUrl = "https://api.github.com/repos/nor0x/Floaty/releases?per_page=20";

    // Unauthenticated GitHub API: 60 requests an hour per IP, far more than a chat will ever ask for.
    private static readonly HttpClient Http = CreateHttp();

#if WINDOWS
    private readonly UpdateManager _manager;
    private UpdateInfo? _pendingUpdate;
    private bool _downloaded;

    public UpdateService()
    {
        _manager = new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false));
    }
#endif

    /// <summary>True only when running as a Velopack-installed app (not a dev/unpackaged run).</summary>
    public bool IsSupported =>
#if WINDOWS
        _manager.IsInstalled;
#else
        false;
#endif

    /// <summary>Public releases page, used as a manual fallback when updates aren't supported.</summary>
    public string ReleasesUrl => $"{RepoUrl}/releases";

    /// <summary>Human-readable current version (Velopack's when installed, else the assembly version).</summary>
    public string CurrentVersion
    {
        get
        {
#if WINDOWS
            var version = _manager.CurrentVersion;
            if (version is not null)
                return version.ToString();
#endif
            // Non-installed builds (F5, or a zip drop): fall back to what the compiler stamped.
            var informational = typeof(UpdateService).Assembly
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                // Strip the "+<commit sha>" source-revision suffix the SDK appends.
                var plus = informational.IndexOf('+');
                return plus > 0 ? informational[..plus] : informational;
            }

            return typeof(UpdateService).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        }
    }

    /// <summary>True when an update has been downloaded and is waiting for a restart to apply.</summary>
    public bool IsUpdatePending =>
#if WINDOWS
        _downloaded && _pendingUpdate is not null;
#else
        false;
#endif

    /// <summary>Version of the pending/available update (from the last check), or null.</summary>
    public string? PendingVersion =>
#if WINDOWS
        _pendingUpdate?.TargetFullRelease?.Version.ToString();
#else
        null;
#endif

    /// <summary>Pre-rendered "what's new" HTML for the pending/available update, or null.</summary>
    public string? PendingNotesHtml =>
#if WINDOWS
        _pendingUpdate?.TargetFullRelease?.NotesHTML;
#else
        null;
#endif

    /// <summary>Checks GitHub Releases for a newer version, caching the result for download.</summary>
    public async Task<UpdateCheckResult> CheckAsync()
    {
#if WINDOWS
        if (!_manager.IsInstalled)
            return new UpdateCheckResult(false, null, "Updates are only available in installed builds.");

        try
        {
            var info = await _manager.CheckForUpdatesAsync();
            if (info is null)
                return new UpdateCheckResult(false, CurrentVersion);

            _pendingUpdate = info;
            _downloaded = false;
            var asset = info.TargetFullRelease;
            return new UpdateCheckResult(
                true,
                asset.Version.ToString(),
                NotesHtml: asset.NotesHTML,
                NotesMarkdown: asset.NotesMarkdown);
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(false, null, ex.Message);
        }
#else
        await Task.CompletedTask;
        return new UpdateCheckResult(false, null, "Updates are only available on Windows installed builds.");
#endif
    }

    /// <summary>Downloads the update found by the last <see cref="CheckAsync"/>. No-op if none pending.</summary>
    public async Task DownloadAsync(IProgress<int>? progress = null)
    {
#if WINDOWS
        if (_pendingUpdate is null)
            return;

        await _manager.DownloadUpdatesAsync(_pendingUpdate, progress is null ? null : progress.Report);
        _downloaded = true;
#else
        await Task.CompletedTask;
#endif
    }

    /// <summary>Applies the downloaded update and restarts the app. No-op unless one is pending.</summary>
    public void ApplyAndRestart()
    {
#if WINDOWS
        if (_downloaded && _pendingUpdate is not null)
            _manager.ApplyUpdatesAndRestart(_pendingUpdate);
#endif
    }

    /// <summary>
    /// Background check + download for startup. Returns true when an update is downloaded and a
    /// restart is pending (the UI then offers "Restart & update"); never restarts on its own.
    /// </summary>
    public async Task<bool> AutoUpdateAsync()
    {
#if WINDOWS
        var result = await CheckAsync();
        if (!result.UpdateAvailable)
            return false;

        await DownloadAsync();
        return _downloaded;
#else
        await Task.CompletedTask;
        return false;
#endif
    }

    /// <summary>
    /// Published releases, newest first, straight from the GitHub API. Unlike <see cref="CheckAsync"/>
    /// this works in every build, because it only reads — so "what's new in this version?" can be
    /// answered from a dev run too. Empty on any failure; <paramref name="error"/> says why.
    /// </summary>
    public async Task<(IReadOnlyList<ReleaseNotes> Releases, string? Error)> GetReleasesAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await Http.GetAsync(ReleasesApiUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return ([], $"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}.");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            var releases = new List<ReleaseNotes>();
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                if (r.TryGetProperty("draft", out var draft) && draft.GetBoolean())
                    continue;

                var tag = r.GetProperty("tag_name").GetString() ?? string.Empty;
                releases.Add(new ReleaseNotes(
                    Version: tag.TrimStart('v', 'V'),
                    Name: r.TryGetProperty("name", out var name) ? name.GetString() ?? tag : tag,
                    PublishedAt: r.TryGetProperty("published_at", out var at) && at.ValueKind == JsonValueKind.String
                        ? at.GetDateTimeOffset()
                        : null,
                    Markdown: r.TryGetProperty("body", out var body) ? body.GetString() ?? string.Empty : string.Empty,
                    Url: r.TryGetProperty("html_url", out var url) ? url.GetString() ?? ReleasesUrl : ReleasesUrl));
            }

            return (releases, null);
        }
        catch (Exception ex)
        {
            return ([], ex.Message);
        }
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // GitHub rejects API requests without a User-Agent.
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Floaty", "1.0"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }
}
