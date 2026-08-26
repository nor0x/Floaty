using System.Globalization;

namespace Floaty.Services;

/// <summary>
/// Derived accent shades computed once from a single user-chosen hex color, consumed through
/// <c>Color.Parse</c>. (These used to be emitted as CSS custom properties too, for the settings
/// WebView; that half went away with the native settings rewrite.)
/// </summary>
public sealed class AccentPalette
{
    /// <summary>Default accent used when unset or invalid.</summary>
    public const string DefaultHex = "#2b7fff";

    /// <summary>The normalized base accent, "#rrggbb".</summary>
    public string Base { get; }

    /// <summary>Slightly darkened base for hover states and links on light backgrounds.</summary>
    public string Hover { get; }

    /// <summary>Strongly darkened base for active-tab text/icons.</summary>
    public string Deep { get; }

    /// <summary>Near-white tint for selected/active backgrounds.</summary>
    public string Tint { get; }

    /// <summary>Fainter tint for secondary-button backgrounds and hovers.</summary>
    public string TintFaint { get; }

    /// <summary>Light accent for borders around selected/active elements.</summary>
    public string Border { get; }

    /// <summary>Translucent accent ("rgba(...)") for focus rings and selection glows.</summary>
    public string Glow { get; }

    /// <summary>Lightened accent that stays legible on the overlay's dark chrome.</summary>
    public string IconOnDark { get; }

    /// <summary>
    /// Black or white, whichever stays legible as text/glyphs sitting *on* <see cref="Base"/>.
    /// The presets run from a dark blue to a pale teal, so a hardcoded white foreground fails the
    /// light half of them.
    /// </summary>
    public string OnAccent { get; }

    private AccentPalette(byte r, byte g, byte b)
    {
        Base = ToHex(r, g, b);
        Hover = Darken(r, g, b, 0.12);
        Deep = Darken(r, g, b, 0.30);
        Tint = MixWhite(r, g, b, 0.92);
        TintFaint = MixWhite(r, g, b, 0.96);
        Border = MixWhite(r, g, b, 0.70);
        Glow = $"rgba({r}, {g}, {b}, 0.15)";
        IconOnDark = MixWhite(r, g, b, 0.50);
        OnAccent = Luminance(r, g, b) > 0.45 ? "#101014" : "#ffffff";
        (_r, _g, _b) = (r, g, b);
    }

    private readonly byte _r, _g, _b;

    /// <summary>
    /// The base accent at a given opacity, as "#aarrggbb". Alpha rather than a mix toward white,
    /// so the same tint reads correctly in both theme variants - <see cref="Tint"/> and friends
    /// only work on light backgrounds.
    /// </summary>
    public string WithAlpha(double alpha)
    {
        var a = (byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255);
        return $"#{a:x2}{_r:x2}{_g:x2}{_b:x2}";
    }

    /// <summary>Builds the palette from a hex string, falling back to <see cref="DefaultHex"/> when invalid.</summary>
    public static AccentPalette From(string? hex)
    {
        if (!TryParse(hex, out var r, out var g, out var b))
            TryParse(DefaultHex, out r, out g, out b);
        return new AccentPalette(r, g, b);
    }

    /// <summary>
    /// Normalizes a hex color ("#RGB"/"#RRGGBB", optional "#") to lowercase "#rrggbb",
    /// falling back to <see cref="DefaultHex"/> when invalid.
    /// </summary>
    public static string Normalize(string? hex) =>
        TryParse(hex, out var r, out var g, out var b) ? ToHex(r, g, b) : DefaultHex;

    private static bool TryParse(string? hex, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        var s = hex?.Trim().TrimStart('#');
        if (string.IsNullOrEmpty(s))
            return false;

        if (s.Length == 3)
            s = $"{s[0]}{s[0]}{s[1]}{s[1]}{s[2]}{s[2]}";
        if (s.Length != 6)
            return false;

        if (!byte.TryParse(s[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r) ||
            !byte.TryParse(s[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g) ||
            !byte.TryParse(s[4..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b))
            return false;

        return true;
    }

    private static string ToHex(byte r, byte g, byte b) => $"#{r:x2}{g:x2}{b:x2}";

    /// <summary>WCAG relative luminance, used only to pick <see cref="OnAccent"/>.</summary>
    private static double Luminance(byte r, byte g, byte b) =>
        0.2126 * Channel(r) + 0.7152 * Channel(g) + 0.0722 * Channel(b);

    private static double Channel(byte value)
    {
        var c = value / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static string Darken(byte r, byte g, byte b, double amount) => ToHex(
        (byte)Math.Round(r * (1 - amount)),
        (byte)Math.Round(g * (1 - amount)),
        (byte)Math.Round(b * (1 - amount)));

    private static string MixWhite(byte r, byte g, byte b, double amount) => ToHex(
        (byte)Math.Round(r * (1 - amount) + 255 * amount),
        (byte)Math.Round(g * (1 - amount) + 255 * amount),
        (byte)Math.Round(b * (1 - amount) + 255 * amount));
}
