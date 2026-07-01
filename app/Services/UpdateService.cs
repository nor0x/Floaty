#if WINDOWS
using Velopack;
using Velopack.Sources;
#endif

namespace Floaty.Services;

/// <summary>Outcome of an update check.</summary>
/// <param name="UpdateAvailable">True when a newer release than the running build exists.</param>
/// <param name="TargetVersion">The available (or current, when up to date) version string.</param>
/// <param name="Error">Non-null when the check could not complete.</param>
public sealed record UpdateCheckResult(bool UpdateAvailable, string? TargetVersion, string? Error = null);

/// <summary>
/// Wraps Velopack's <c>UpdateManager</c> against the GitHub Releases of <c>nor0x/Floaty</c>.
/// Registered as a singleton. Compiles on every target but only performs real work on Windows
/// installed builds; elsewhere (and during <c>dotnet run</c>) it reports "not supported".
/// </summary>
public sealed class UpdateService
{
    private const string RepoUrl = "https://github.com/nor0x/Floaty";

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
            return AppInfo.Current.VersionString;
        }
    }

    /// <summary>True when an update has been downloaded and is waiting for a restart to apply.</summary>
    public bool IsUpdatePending =>
#if WINDOWS
        _downloaded && _pendingUpdate is not null;
#else
        false;
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
            return new UpdateCheckResult(true, info.TargetFullRelease.Version.ToString());
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
}
