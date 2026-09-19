using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using Floaty.IconFont;
using Floaty.Services;

namespace Floaty.Views.Chat;

/// <summary>
/// Kinds of <c>/</c> command offered by the suggestion picker.
/// </summary>
public enum SlashKind
{
    Action, // built-in commands executed immediately (e.g. /new, /settings)
    Server, // an MCP server: selecting it fills the "/name " prefix to scope the next message
    Memory, // memory commands taking free text (/remember, /recall): prefix-filled, handled on send
    Skill,  // an agent skill (SKILL.md): scopes the next message to that skill's instructions
}

/// <summary>One row in the slash-command picker.</summary>
/// <remarks>
/// Public and top-level, unlike the MAUI original, because an Avalonia <c>DataTemplate</c> declares the
/// type it binds through <c>DataType</c> and compiled bindings need to reach it.
/// </remarks>
public sealed class SlashCommand
{
    public SlashCommand(string name, string description, SlashKind kind = SlashKind.Action, string? icon = null)
    {
        Name = name;
        Description = description;
        Kind = kind;
        Icon = icon ?? TablerLine.Bolt;
    }

    public string Name { get; }
    public string Description { get; }
    public SlashKind Kind { get; }
    public string Icon { get; }
    public string Token => $"/{Name}";
}

public enum AttachmentKind
{
    Window,    // a window tagged with @; captured on the spot
    File,      // a file dropped on the ring or the panel; read and text-extracted on the spot
    Selection, // text selected in another app, read as the summon hotkey fired
}

/// <summary>
/// Something riding along on the pending prompt: a window the user tagged with @, a file they dropped,
/// or the text they had selected when they summoned Floaty. Windows and files start their work
/// (capture / ingest) the moment the chip appears, and the send path awaits it — so what you saw when
/// you attached it is what gets sent, even if the window closes or the file moves in between. A
/// selection is already plain text by the time the chip exists and carries no task.
/// </summary>
/// <remarks>
/// Implements INPC because the per-file persist toggle mutates the chip after it is realized.
/// </remarks>
public sealed class PromptAttachmentVm : INotifyPropertyChanged
{
    public AttachmentKind Kind { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Glyph { get; init; } = TablerLine.AppWindow;
    public ICommand? RemoveCommand { get; set; }

    // Window attachments.
    public nint Hwnd { get; init; }
    public Task<CaptureResult?>? CaptureTask { get; set; }

    // File attachments. SourcePath doubles as the de-duplication key.
    public string? SourcePath { get; init; }
    public Task<DroppedFile?>? IngestTask { get; set; }
    public ICommand? TogglePersistCommand { get; set; }

    // Selection attachments: the full selected text (Title only holds a short preview of it) and the
    // title of the window it came from, so the model is told where it is looking.
    public string? SelectionText { get; init; }
    public string? SourceTitle { get; init; }

    /// <summary>
    /// Only dropped files show the toggle: @-tagged windows follow <c>RememberTaggedCaptures</c> and
    /// are already written to memory by the time their chip settles.
    /// </summary>
    public bool ShowPersistToggle => Kind == AttachmentKind.File;

    private bool _persist;

    /// <summary>
    /// Whether this file is also written to memory on send. Seeded from
    /// <c>FloatyConfig.RememberDroppedFiles</c> and overridable per chip.
    /// </summary>
    public bool Persist
    {
        get => _persist;
        set
        {
            if (_persist == value)
                return;
            _persist = value;
            Raise(nameof(Persist), nameof(PersistGlyph), nameof(PersistOpacity), nameof(PersistDescription));
        }
    }

    private bool _isReady;

    /// <summary>False until the capture/ingest finishes, which dims the chip.</summary>
    public bool IsReady
    {
        get => _isReady;
        set
        {
            if (_isReady == value)
                return;
            _isReady = value;
            Raise(nameof(IsReady), nameof(ChipOpacity));
        }
    }

    // The glyph swaps and opacity carries the on/off state; the colour stays a DynamicResource so an
    // accent change still recolours already-rendered chips.
    public string PersistGlyph => Persist ? TablerLine.DatabasePlus : TablerLine.Database;
    public double PersistOpacity => Persist ? 1.0 : 0.4;
    public double ChipOpacity => IsReady ? 1.0 : 0.55;
    public string PersistDescription => Persist ? "Also save to memory" : "Use once, don't save";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(params string[] names)
    {
        foreach (var name in names)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// Message layout helpers for the chat list's DataTemplate.
/// </summary>
/// <remarks>
/// Per-role fill and alignment live in the <c>Border.msg</c> styles in ChatPanelView.axaml; what XAML
/// can't express on its own - a width fraction - is a converter here.
/// </remarks>
public static class ChatBrushes
{
    /// <summary>Caps a message at 80% of the list's width.</summary>
    public static readonly IValueConverter MessageMaxWidth =
        new FuncValueConverter<double, double>(w => w > 0 ? w * 0.8 : double.PositiveInfinity);
}
