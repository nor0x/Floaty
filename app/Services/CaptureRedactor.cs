using System.Text.RegularExpressions;

namespace Floaty.Services;

/// <summary>
/// The last gate before capture text reaches disk. Two kinds of protection, because they answer
/// different questions:
/// <list type="number">
///   <item><description><see cref="IsExcludedApp"/> / <see cref="IsPrivateWindow"/> — whole windows
///   that should never be read at all. A password manager's window is entirely secrets, and a
///   private-browsing window is the user explicitly saying "not this one".</description></item>
///   <item><description><see cref="RedactLine"/> — credential-shaped strings that turn up anywhere:
///   a terminal echoing an API key, a settings page showing a token, a checkout form.</description></item>
/// </list>
/// Redaction runs before <see cref="CapturePruner"/>, so a scrubbed line is then also junk-filtered
/// (a card number reduced to <c>[redacted]</c> is one wordless token, and gets dropped outright).
/// </summary>
public static partial class CaptureRedactor
{
    /// <summary>Replacement written in place of anything the patterns match.</summary>
    public const string Placeholder = "[redacted]";

    // Matched case-insensitively as a substring of the process or window name, so "1Password 8",
    // "KeePassXC" and "Bitwarden - Chrome" all hit.
    private static readonly string[] ExcludedApps =
    [
        "1password",
        "bitwarden",
        "dashlane",
        "enpass",
        "keepass",
        "lastpass",
        "nordpass",
        "proton pass",
        "strongbox",
        "credential manager",
        "windows security",
    ];

    // The browser is the largest blind spot here: banking and health happen inside Chrome and Edge,
    // which can never be on the exclusion list, and the window title is the only per-site signal.
    private static readonly string[] PrivateWindowMarkers =
    [
        "private browsing",
        "incognito",
        "inprivate",
    ];

    /// <summary>True when this application's contents should never be read.</summary>
    public static bool IsExcludedApp(string? appName)
    {
        if (string.IsNullOrWhiteSpace(appName))
            return false;

        foreach (var excluded in ExcludedApps)
        {
            if (appName.Contains(excluded, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>True when the window title marks a private-browsing session in any major browser.</summary>
    public static bool IsPrivateWindow(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return false;

        foreach (var marker in PrivateWindowMarkers)
        {
            if (title.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>Replaces every credential-shaped run in <paramref name="line"/> with <see cref="Placeholder"/>.</summary>
    public static string RedactLine(string line)
    {
        if (string.IsNullOrEmpty(line))
            return line;

        line = AwsAccessKey().Replace(line, Placeholder);
        line = ProviderSecretKey().Replace(line, Placeholder);
        line = BearerToken().Replace(line, Placeholder);
        line = LabelledSecret().Replace(line, Placeholder);
        line = CardShapedDigits().Replace(line, Placeholder);
        return line;
    }

    /// <summary>Convenience overload for optional metadata (title, URL, document path).</summary>
    public static string? RedactOrNull(string? value) =>
        string.IsNullOrEmpty(value) ? value : RedactLine(value);

    // AWS access key id.
    [GeneratedRegex(@"\bAKIA[0-9A-Z]{16}\b")]
    private static partial Regex AwsAccessKey();

    // Provider-style secret keys: sk-..., pk_live_..., rk_...
    [GeneratedRegex(@"\b(?:sk|pk|rk)[-_][A-Za-z0-9_\-]{16,}\b")]
    private static partial Regex ProviderSecretKey();

    [GeneratedRegex(@"\bbearer\s+[A-Za-z0-9._\-]{16,}", RegexOptions.IgnoreCase)]
    private static partial Regex BearerToken();

    // Labelled secrets: api_key = ..., token: ..., password=...
    [GeneratedRegex(@"\b(?:api[_-]?key|secret|token|password|passwd)\b\s*[:=]\s*\S+", RegexOptions.IgnoreCase)]
    private static partial Regex LabelledSecret();

    // Card-shaped digit runs: 13 to 19 digits with optional space/dash separators. Anchored to end
    // on a digit rather than on an optional separator, so "1111 1111 1111 1111 ok" keeps its space.
    [GeneratedRegex(@"\b\d(?:[ -]?\d){12,18}\b")]
    private static partial Regex CardShapedDigits();
}
