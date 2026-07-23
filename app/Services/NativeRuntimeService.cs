using System.Formats.Tar;
using System.IO.Compression;

namespace Floaty.Services;

/// <summary>
/// Downloads and installs the transcribe.cpp native runtime (transcribe.dll + ggml backend
/// modules) that runs the local speech-to-text models. Pinned to an exact release because the
/// library is pre-1.0 and its ABI may change between 0.x versions; a version bump lands in a
/// fresh folder under <see cref="FloatyPaths.NativeRuntimes"/> rather than mutating in place.
/// </summary>
public sealed class NativeRuntimeService
{
    /// <summary>Pinned transcribe.cpp release. The P/Invoke layer asserts this at load time.</summary>
    public const string Version = "0.1.3";

    /// <summary>Compressed size of the pinned archive, used for aggregate download progress.</summary>
    public const long ArchiveBytes = 25_957_910;

    private const string Url =
        "https://github.com/handy-computer/transcribe.cpp/releases/download/v" + Version +
        "/transcribe-native-" + Version + "-windows-x86_64-cpu-vulkan.tar.gz";

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary><c>~/.floaty/native/transcribe/&lt;version&gt;</c> — flat folder with the DLLs.</summary>
    public string InstallDir => Path.Combine(FloatyPaths.NativeRuntimes, "transcribe", Version);

    public string LibraryPath => Path.Combine(InstallDir, "transcribe.dll");

    public bool IsInstalled => OperatingSystem.IsWindows() && File.Exists(LibraryPath);

    /// <summary>
    /// Downloads and extracts the runtime if missing. Progress is the fraction of the archive
    /// downloaded; extraction flips <see cref="IsInstalled"/> only once transcribe.dll is in place.
    /// </summary>
    public async Task EnsureInstalledAsync(IProgress<double>? progress, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows() || IsInstalled)
        {
            progress?.Report(1);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(InstallDir)!);
        var archivePath = InstallDir + ".download.partial";
        var extractDir = InstallDir + ".extracting";

        try
        {
            using (var response = await Http.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? ArchiveBytes;
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var target = File.Create(archivePath);
                var buffer = new byte[128 * 1024];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    progress?.Report(Math.Min(1, done / (double)total));
                }
            }

            // The archive has a single top-level directory whose name carries no version; strip
            // the first path component of every entry so the DLLs land flat in the install dir.
            if (Directory.Exists(extractDir))
                Directory.Delete(extractDir, recursive: true);
            Directory.CreateDirectory(extractDir);
            await using (var fs = File.OpenRead(archivePath))
            await using (var gz = new GZipStream(fs, CompressionMode.Decompress))
            using (var tar = new TarReader(gz))
            {
                while (tar.GetNextEntry() is { } entry)
                {
                    if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                        continue;
                    var relative = entry.Name.Replace('\\', '/');
                    var slash = relative.IndexOf('/');
                    if (slash >= 0)
                        relative = relative[(slash + 1)..];
                    if (relative.Length == 0)
                        continue;
                    var dest = Path.Combine(extractDir, relative.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    entry.ExtractToFile(dest, overwrite: true);
                }
            }

            if (!File.Exists(Path.Combine(extractDir, "transcribe.dll")))
                throw new InvalidDataException("Downloaded speech engine archive is missing transcribe.dll.");
            Directory.Move(extractDir, InstallDir);
            progress?.Report(1);

            CleanUpOldVersions();
        }
        finally
        {
            try { File.Delete(archivePath); } catch { /* best effort */ }
            try { if (Directory.Exists(extractDir)) Directory.Delete(extractDir, recursive: true); } catch { /* best effort */ }
        }
    }

    private void CleanUpOldVersions()
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(Path.GetDirectoryName(InstallDir)!))
            {
                if (!string.Equals(Path.GetFileName(dir), Version, StringComparison.OrdinalIgnoreCase))
                    Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // An old version left behind (e.g. its DLL still loaded) is harmless.
        }
    }
}
