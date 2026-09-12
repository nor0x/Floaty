using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Floaty.Services;

/// <summary>
/// Derives a button's hover / pressed fill from the colour it already has, so a state never
/// replaces the button's identity with a neutral grey.
/// </summary>
/// <remarks>
/// Fluent paints Button states by overwriting <c>PART_ContentPresenter</c>'s Background with a flat
/// <c>ButtonBackgroundPointerOver</c>, which throws away whatever the call site set. Styles/ButtonStates.axaml
/// rebinds that setter through this converter instead; see that file for the precedence rules.
///
/// The awkward case is a transparent button - there is no colour to shade. The Foreground answers
/// it: a glyph is by definition legible against the surface behind it, so its RGB is the right
/// polarity for a wash. That is what lets the same rule work on the chat panel's always-dark chrome
/// and on Settings' light cards without either of them knowing which it is.
/// </remarks>
public sealed class ButtonStateShade : IMultiValueConverter
{
    /// <summary>Hover. <see cref="_shade"/> matches <see cref="AccentPalette.HoverDarken"/> by construction.</summary>
    public static readonly IMultiValueConverter Hover =
        new ButtonStateShade(alphaRaise: 0.18, shade: AccentPalette.HoverDarken, wash: 0.14);

    /// <summary>Pressed: the same moves, further along.</summary>
    public static readonly IMultiValueConverter Pressed =
        new ButtonStateShade(alphaRaise: 0.32, shade: AccentPalette.DeepDarken, wash: 0.22);

    /// <summary>Backgrounds at or above this alpha are shaded by mixing rather than by raising alpha.</summary>
    private const double OpaqueAlpha = 0.9;

    /// <summary>Below this luminance, darkening is invisible, so shade toward white instead.</summary>
    private const double NearBlackLuminance = 0.05;

    private readonly double _alphaRaise;
    private readonly double _shade;
    private readonly double _wash;

    private ButtonStateShade(double alphaRaise, double shade, double wash) =>
        (_alphaRaise, _shade, _wash) = (alphaRaise, shade, wash);

    /// <summary>values[0] is the Button's Background, values[1] its Foreground.</summary>
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count != 2)
            return AvaloniaProperty.UnsetValue;

        var background = Unwrap(values[0]);
        var foreground = Unwrap(values[1]);

        // A gradient or image fill has no single colour to shade. Hand it back untouched rather than
        // leaving it unset, which would let Fluent's grey back in - the very bug this exists to fix.
        if (background is not null and not ISolidColorBrush)
            return background;

        var alpha = EffectiveAlpha(background as ISolidColorBrush);
        if (alpha <= 0)
            return Wash(foreground);

        var color = ((ISolidColorBrush)background!).Color;

        if (alpha < OpaqueAlpha)
        {
            // Move a fraction of the remaining way to opaque. The multiplicative form (a * (1 + k))
            // does nothing on a near-transparent fill and overshoots 1.0 on a near-opaque one.
            var raised = alpha + (1 - alpha) * _alphaRaise;
            return new ImmutableSolidColorBrush(
                Color.FromArgb((byte)Math.Round(Math.Clamp(raised, 0, 1) * 255), color.R, color.G, color.B));
        }

        var shaded = AccentResources.Luminance(color) < NearBlackLuminance
            ? AccentResources.MixWhite(color, _shade)
            : AccentResources.Darken(color, _shade);

        return new ImmutableSolidColorBrush(Color.FromArgb(color.A, shaded.R, shaded.G, shaded.B));
    }

    /// <summary>A translucent veil in the content's own colour, for buttons with no fill of their own.</summary>
    private ImmutableSolidColorBrush Wash(object? foreground)
    {
        // The foreground's own alpha is discarded: a Dim (#99FFFFFF) glyph and a White one should
        // produce the same veil, since both say "the surface behind me is dark".
        var color = foreground is ISolidColorBrush brush ? brush.Color : Colors.White;
        return new ImmutableSolidColorBrush(
            Color.FromArgb((byte)Math.Round(_wash * 255), color.R, color.G, color.B));
    }

    /// <summary>
    /// Alpha as it will actually render. Brushes carry transparency either in the colour or on
    /// Opacity - Fluent's own ButtonBackgroundPointerOver is opaque black at Opacity 0.1 - so a
    /// converter that reads only the colour misjudges half of them.
    /// </summary>
    private static double EffectiveAlpha(ISolidColorBrush? brush) =>
        brush is null ? 0 : brush.Color.A / 255.0 * Math.Clamp(brush.Opacity, 0, 1);

    private static object? Unwrap(object? value) =>
        value == AvaloniaProperty.UnsetValue || value is null ? null : value;
}
