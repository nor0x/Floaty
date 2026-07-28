using Floaty.Services;
using Xberg;

namespace Floaty.Platforms.Windows;

/// <summary>
/// Document text extraction backed by Xberg (a Rust core reached through per-RID native binaries).
/// </summary>
/// <remarks>
/// Every failure mode is swallowed and reported as "no text", never as an error: a dropped file that
/// can't be parsed still becomes a usable attachment via <see cref="FileIngestService"/>'s UTF-8 and
/// filename-only fallbacks, and a text-extraction hiccup must never take down a chat message. The
/// package is young and P/Invokes into a native library, so the catch list is deliberately broad.
/// </remarks>
public sealed class WindowsTextExtractionService : ITextExtractionService
{
    // The native library is an unknown quantity on malformed input; don't let one bad file hang a chip.
    private static readonly TimeSpan ExtractTimeout = TimeSpan.FromSeconds(15);

    // A ten-file drop must not start ten native extractions at once.
    private readonly SemaphoreSlim _gate = new(2, 2);

    // Latched once the native payload proves unusable (missing, wrong architecture, failed static
    // init). That failure repeats for every file, so stop paying the timeout on each one.
    private bool _unavailable;

    // Soft counter for non-typed failures; a library that fails on everything shouldn't be retried
    // forever either, but a handful of unparseable files shouldn't disable it.
    private int _consecutiveFailures;
    private const int MaxConsecutiveFailures = 5;

    public async Task<ExtractedText?> ExtractAsync(string path, CancellationToken cancellationToken = default)
    {
        if (_unavailable)
            return null;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ExtractTimeout);

            var result = await XbergConverter.ExtractAsync(
                ExtractInput.FromUri(path),
                ExtractionConfig.Default())
                .WaitAsync(timeout.Token);

            var document = result?.Results?.FirstOrDefault();
            if (document is null || string.IsNullOrWhiteSpace(document.Content))
            {
                _consecutiveFailures++;
                return null;
            }

            _consecutiveFailures = 0;
            return new ExtractedText(document.Content, document.MimeType);
        }
        catch (Exception ex) when (ex is DllNotFoundException
                                      or BadImageFormatException
                                      or TypeInitializationException
                                      or EntryPointNotFoundException
                                      or FileNotFoundException)
        {
            // The native payload didn't ship for this RID (or is the wrong architecture). It will fail
            // identically for every file, so stop trying.
            _unavailable = true;
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            // XbergException and anything else the native boundary throws: this file has no text.
            if (++_consecutiveFailures >= MaxConsecutiveFailures)
                _unavailable = true;
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }
}
