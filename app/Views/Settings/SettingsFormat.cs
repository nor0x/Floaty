using System.Globalization;
using Avalonia.Data.Converters;
using Floaty.IconFont;
using Floaty.Services;
using Floaty.ViewModels.Settings;

namespace Floaty.Views.Settings;

/// <summary>
/// Display helpers for the settings views: enum labels and the section switch.
/// </summary>
/// <remarks>
/// Razor could call methods on the page directly from markup; Avalonia's compiled bindings need a
/// converter. These stay presentation-only - nothing here decides behaviour.
/// </remarks>
public static class SettingsFormat
{
    /// <summary>Turns a PascalCase section name into a human label ("ModelProvider" -> "Model provider").</summary>
    public static readonly IValueConverter SectionLabel =
        new FuncValueConverter<SettingsViewModel.SettingsSection, string>(section => Humanize(section));

    /// <summary>True when the bound section equals the one named by the converter parameter.</summary>
    public static readonly IValueConverter IsSection = new SectionMatchConverter();

    /// <summary>The Tabler glyph shown beside a section in the rail.</summary>
    public static readonly IValueConverter SectionIcon =
        new FuncValueConverter<SettingsViewModel.SettingsSection, string>(section => section switch
        {
            SettingsViewModel.SettingsSection.Behavior => TablerLine.AdjustmentsHorizontal,
            SettingsViewModel.SettingsSection.Appearance => TablerLine.Palette,
            SettingsViewModel.SettingsSection.Sounds => TablerLine.Volume2,
            SettingsViewModel.SettingsSection.ModelProvider => TablerLine.Sparkles,
            SettingsViewModel.SettingsSection.ScreenHistory => TablerLine.History,
            SettingsViewModel.SettingsSection.VoiceInput => TablerLine.Microphone,
            SettingsViewModel.SettingsSection.Mcp => TablerLine.PlugConnected,
            SettingsViewModel.SettingsSection.Exec => TablerLine.Terminal2,
            SettingsViewModel.SettingsSection.Skills => TablerLine.Puzzle,
            SettingsViewModel.SettingsSection.Updates => TablerLine.Download,
            _ => TablerLine.Settings,
        });

    /// <summary>
    /// True when the two bound strings are the same value - a swatch's own value against the one
    /// currently selected. Used to light up the ring on the active accent colour and ring image,
    /// neither of which showed any selected state before.
    /// </summary>
    public static readonly IMultiValueConverter SameValue = new SameValueConverter();

    public static readonly IValueConverter EnumLabel =
        new FuncValueConverter<object?, string>(value => value is null ? string.Empty : Humanize(value));

    /// <summary>Every transport that chats takes a reasoning effort; local ONNX models only embed.</summary>
    public static readonly IValueConverter SupportsEffort =
        new FuncValueConverter<ProviderKind, bool>(kind => kind != ProviderKind.LocalOnnx);

    /// <summary>Output verbosity is an OpenAI (GPT-5 family) parameter; nothing else understands it.</summary>
    public static readonly IValueConverter SupportsVerbosity =
        new FuncValueConverter<ProviderKind, bool>(kind => kind == ProviderKind.OpenAI);

    /// <summary>"3 custom images" / "1 custom image" / "" — pluralisation the markup used to inline.</summary>
    public static readonly IValueConverter CustomCount =
        new FuncValueConverter<int, string>(n => n switch
        {
            <= 0 => string.Empty,
            1 => "1 custom file",
            _ => $"{n} custom files",
        });

    // Section and enum names that are acronyms, which the general PascalCase split would mangle
    // ("Mcp" rather than "MCP").
    private static readonly Dictionary<string, string> Acronyms = new(StringComparer.Ordinal)
    {
        ["Mcp"] = "MCP",
        ["Stt"] = "STT",
        ["OpenAi"] = "OpenAI",
        ["AzureOpenAI"] = "Azure OpenAI",
        ["OpenAiCompatible"] = "OpenAI-compatible",
        ["LocalOnnx"] = "Local ONNX",
    };

    private static string Humanize(object value)
    {
        var name = value.ToString() ?? string.Empty;
        if (name.Length == 0)
            return name;

        if (Acronyms.TryGetValue(name, out var acronym))
            return acronym;

        var builder = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (i > 0 && char.IsUpper(c))
            {
                builder.Append(' ');
                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private sealed class SameValueConverter : IMultiValueConverter
    {
        public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
            values.Count == 2
            && values[0] is string a
            && values[1] is string b
            && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class SectionMatchConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is SettingsViewModel.SettingsSection section
            && parameter is string name
            && string.Equals(section.ToString(), name, StringComparison.Ordinal);

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
