using Avalonia.Controls;
using Avalonia.Media;

namespace Floaty.Services;

/// <summary>
/// Pushes one <see cref="AccentPalette"/> into an application resource dictionary: Floaty's own
/// accent brushes, plus the FluentTheme keys that would otherwise paint themselves with the
/// *operating system's* accent colour.
/// </summary>
/// <remarks>
/// Fluent's control themes reference their brushes as <c>{DynamicResource SomeSemanticKey}</c> -
/// that is how they follow the light/dark variant - and Avalonia resolves a DynamicResource from the
/// control upwards, reaching <c>Application.Resources</c> before <c>Application.Styles</c>. Defining
/// the same keys on the application therefore shadows the theme's, and reassigning them repaints
/// live, which is what the accent preview in Settings needs.
///
/// There is no DevTools on Avalonia 12, so the key names below were read out of the theme assembly:
/// <code>
/// tr -d '\0' &lt; ~/.nuget/packages/avalonia.themes.fluent/12.1.1/lib/net10.0/Avalonia.Themes.Fluent.dll \
///   | grep -aoh "CheckBoxCheckBackgroundFill[A-Za-z]*"
/// </code>
/// If something in the UI is still stubbornly OS-blue, that grep is how to find the key it wants.
/// </remarks>
public static class AccentResources
{
    /// <summary>Floaty's own keys, consumed by the overlay, the chat panel and the settings styles.</summary>
    public static void Apply(IResourceDictionary resources, AccentPalette palette)
    {
        var accent = Color.Parse(palette.Base);
        var hover = Color.Parse(palette.Hover);
        var pressed = Color.Parse(palette.Deep);
        var onAccent = Color.Parse(palette.OnAccent);

        // Colors (kept as Color, not Brush: existing call sites bind them to Background directly and
        // rely on Avalonia's implicit conversion).
        resources["AccentColor"] = accent;
        resources["AccentIconOnDarkColor"] = Color.Parse(palette.IconOnDark);

        resources["AccentBrush"] = new SolidColorBrush(accent);
        resources["AccentHoverBrush"] = new SolidColorBrush(hover);
        resources["AccentPressedBrush"] = new SolidColorBrush(pressed);
        resources["OnAccentBrush"] = new SolidColorBrush(onAccent);

        // Tints carry their opacity in the colour rather than on the brush, so they composite the
        // same way over a light card and a dark one.
        resources["AccentSubtleBrush"] = Brush(palette.WithAlpha(0.16));
        resources["AccentSubtleHoverBrush"] = Brush(palette.WithAlpha(0.24));
        resources["AccentFaintBrush"] = Brush(palette.WithAlpha(0.09));
        resources["AccentBorderBrush"] = Brush(palette.WithAlpha(0.45));

        ApplyFluentAccent(resources, accent, hover, pressed, onAccent);
    }

    /// <summary>The FluentTheme keys that default to the OS accent.</summary>
    private static void ApplyFluentAccent(
        IResourceDictionary resources, Color accent, Color hover, Color pressed, Color onAccent)
    {
        // The six shades Fluent derives from the system accent. Anything reading these directly
        // (ProgressBar, focus visuals) follows along.
        resources["SystemAccentColor"] = accent;
        resources["SystemAccentColorLight1"] = MixWhite(accent, 0.20);
        resources["SystemAccentColorLight2"] = MixWhite(accent, 0.40);
        resources["SystemAccentColorLight3"] = MixWhite(accent, 0.60);
        resources["SystemAccentColorDark1"] = Darken(accent, 0.12);
        resources["SystemAccentColorDark2"] = Darken(accent, 0.24);
        resources["SystemAccentColorDark3"] = Darken(accent, 0.36);

        Set(resources, accent,
            "SystemControlHighlightAccentBrush",
            "SystemControlHighlightAltAccentBrush",
            "SystemControlForegroundAccentBrush",
            "SystemControlHighlightListAccentLowBrush",
            "SystemControlHighlightAltListAccentLowBrush",
            "CheckBoxCheckBackgroundFillChecked",
            "CheckBoxCheckBackgroundStrokeChecked",
            "RadioButtonOuterEllipseCheckedFill",
            "RadioButtonOuterEllipseCheckedStroke",
            "SliderTrackValueFill",
            "SliderThumbBackground",
            "ToggleSwitchFillOn",
            "ToggleSwitchStrokeOn",
            "ToggleButtonBackgroundChecked",
            "TabItemHeaderBackgroundSelected",
            "TextControlBorderBrushFocused",
            "TextControlSelectionHighlightColor",
            "AccentButtonBackground",
            "AccentButtonBorderBrush",
            "ProgressBarForeground");

        Set(resources, hover,
            "SystemControlHighlightListAccentMediumBrush",
            "SystemControlHighlightAltListAccentMediumBrush",
            "CheckBoxCheckBackgroundFillCheckedPointerOver",
            "CheckBoxCheckBackgroundStrokeCheckedPointerOver",
            "SliderTrackValueFillPointerOver",
            "SliderThumbBackgroundPointerOver",
            "ToggleSwitchFillOnPointerOver",
            "ToggleSwitchStrokeOnPointerOver",
            "AccentButtonBackgroundPointerOver");

        Set(resources, pressed,
            "SystemControlHighlightListAccentHighBrush",
            "SystemControlHighlightAltListAccentHighBrush",
            "CheckBoxCheckBackgroundFillCheckedPressed",
            "CheckBoxCheckBackgroundStrokeCheckedPressed",
            "SliderTrackValueFillPressed",
            "SliderThumbBackgroundPressed",
            "ToggleSwitchFillOnPressed",
            "ToggleSwitchStrokeOnPressed",
            "AccentButtonBackgroundPressed");

        // Glyphs and labels drawn on top of an accent fill.
        Set(resources, onAccent,
            "CheckBoxCheckGlyphForegroundChecked",
            "CheckBoxCheckGlyphForegroundCheckedPointerOver",
            "CheckBoxCheckGlyphForegroundCheckedPressed",
            "AccentButtonForeground",
            "AccentButtonForegroundPointerOver",
            "AccentButtonForegroundPressed");
    }

    private static void Set(IResourceDictionary resources, Color color, params string[] keys)
    {
        // One brush per key: a shared instance would be fine today, but Avalonia mutates
        // brush properties in a few control themes (opacity animations on Slider thumbs).
        foreach (var key in keys)
            resources[key] = new SolidColorBrush(color);
    }

    private static SolidColorBrush Brush(string hex) => new(Color.Parse(hex));

    // internal, not private: ButtonStateShade shades arbitrary button backgrounds with the same
    // three operations, and the app should have one definition of "12% darker".
    internal static Color MixWhite(Color c, double amount) => Color.FromRgb(
        (byte)Math.Round(c.R * (1 - amount) + 255 * amount),
        (byte)Math.Round(c.G * (1 - amount) + 255 * amount),
        (byte)Math.Round(c.B * (1 - amount) + 255 * amount));

    internal static Color Darken(Color c, double amount) => Color.FromRgb(
        (byte)Math.Round(c.R * (1 - amount)),
        (byte)Math.Round(c.G * (1 - amount)),
        (byte)Math.Round(c.B * (1 - amount)));

    /// <summary>WCAG relative luminance. Mirrors AccentPalette's, over a <see cref="Color"/>.</summary>
    internal static double Luminance(Color c) =>
        0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);

    private static double Channel(byte value)
    {
        var c = value / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}
