using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Floaty.IconFont;
using Floaty.Services;

namespace Floaty;

/// <summary>
/// A row in the conversation switcher shown inside the message list (toggled by /chats). Either a saved
/// thread (with open + delete commands) or the "New conversation" action row.
/// </summary>
public sealed class ConversationItemVm
{
    public ConversationItemVm(string title, string subtitle, bool isCurrent, bool isNewAction,
        ICommand openCommand, ICommand? deleteCommand)
    {
        Title = title;
        Subtitle = subtitle;
        IsCurrent = isCurrent;
        IsNewAction = isNewAction;
        OpenCommand = openCommand;
        DeleteCommand = deleteCommand;
    }

    public string Title { get; }
    public string Subtitle { get; }
    public bool IsCurrent { get; }
    public bool IsNewAction { get; }
    public ICommand OpenCommand { get; }
    public ICommand? DeleteCommand { get; }

    public bool HasDelete => DeleteCommand is not null;
    public bool IsSavedThread => !IsNewAction;
}

/// <summary>
/// A memory source shown beneath an assistant answer. Offers up to two openable chips — the capture's
/// screenshot image and its text file — present only when the corresponding source file exists.
/// </summary>
public sealed class CitationVm
{
    public CitationVm(string title, ICommand? openImageCommand, ICommand? openTextCommand)
    {
        Title = title;
        OpenImageCommand = openImageCommand;
        OpenTextCommand = openTextCommand;
    }

    public string Title { get; }
    public ICommand? OpenImageCommand { get; }
    public ICommand? OpenTextCommand { get; }

    public bool HasImage => OpenImageCommand is not null;
    public bool HasText => OpenTextCommand is not null;
}

/// <summary>
/// A single chat bubble shown in the overlay's message list. <see cref="Text"/> is mutable so the
/// assistant's placeholder ("…") can be replaced in place once the LLM responds, and holds the raw
/// markdown: the Blazor bubble renders from it, and both persistence and the history sent to the model
/// are projected from it.
/// Alignment and colour come from <c>ChatBrushes</c> via the message template, not from here.
/// </summary>
public sealed class ChatMessageVm : INotifyPropertyChanged
{
    private string _text;
    private string _reasoning = string.Empty;
    private bool _isReasoningExpanded;
    private TimeSpan? _reasoningDuration;
    private IReadOnlyList<CitationVm> _citations = System.Array.Empty<CitationVm>();

    public ChatMessageVm(bool isUser, string text, bool isSystemNote = false)
    {
        IsUser = isUser;
        _text = text;
        IsSystemNote = isSystemNote;

        // Built here rather than wired from the panel, so a bubble rehydrated from disk is as clickable
        // as one that just streamed in.
        ToggleReasoningCommand = new RelayCommand(() =>
        {
            UserToggledReasoning = true;
            IsReasoningExpanded = !IsReasoningExpanded;
        });
    }

    public bool IsUser { get; }

    /// <summary>True for Floaty's own non-conversational bubbles (save/recall/capture notices); these are
    /// excluded when remembering the whole conversation.</summary>
    public bool IsSystemNote { get; }

    public string Text
    {
        get => _text;
        set
        {
            if (_text == value)
                return;
            _text = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// The model's reasoning for this answer, when it showed any. Streams live in its own dimmed section
    /// above the answer and collapses once the answer starts; never sent back to the model, since history
    /// is projected from <see cref="Text"/> alone.
    /// </summary>
    public string Reasoning
    {
        get => _reasoning;
        set
        {
            var next = value ?? string.Empty;
            if (_reasoning == next)
                return;
            _reasoning = next;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasReasoning));
        }
    }

    public bool HasReasoning => _reasoning.Length > 0;

    /// <summary>Whether the reasoning section is showing. Expanded while it streams, collapsed when the
    /// answer starts, and from then on the user's to open.</summary>
    public bool IsReasoningExpanded
    {
        get => _isReasoningExpanded;
        set
        {
            if (_isReasoningExpanded == value)
                return;
            _isReasoningExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ReasoningToggleGlyph));
        }
    }

    /// <summary>Set once the user clicks the header, so the automatic collapse doesn't fight their choice.</summary>
    public bool UserToggledReasoning { get; set; }

    /// <summary>How long the model thought before answering; null while it still is.</summary>
    public TimeSpan? ReasoningDuration
    {
        get => _reasoningDuration;
        set
        {
            if (_reasoningDuration == value)
                return;
            _reasoningDuration = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ReasoningHeader));
        }
    }

    public string ReasoningHeader => _reasoningDuration is { } elapsed
        ? $"Thought for {FormatThinkingTime(elapsed)}"
        : "Thinking…";

    /// <summary>The glyph swaps to carry the open/closed state; same idiom as the attachment chips.</summary>
    public string ReasoningToggleGlyph =>
        _isReasoningExpanded ? TablerLine.ChevronDown : TablerLine.ChevronRight;

    public ICommand ToggleReasoningCommand { get; }

    // Sub-second thinking reads as noise as "0s", and anything past a minute wants the minutes shown.
    private static string FormatThinkingTime(TimeSpan elapsed) => elapsed.TotalSeconds switch
    {
        < 1 => $"{elapsed.TotalMilliseconds:F0}ms",
        < 60 => $"{elapsed.TotalSeconds:F0}s",
        _ => $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s",
    };

    /// <summary>Memory sources cited for this answer; empty for user messages and non-RAG replies.</summary>
    public IReadOnlyList<CitationVm> Citations
    {
        get => _citations;
        set
        {
            _citations = value ?? System.Array.Empty<CitationVm>();
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasCitations));
        }
    }

    public bool HasCitations => _citations.Count > 0;

    /// <summary>
    /// Assistant answers render as markdown; the user's own messages and Floaty's system notes stay
    /// literal text, both because they were never markdown and because <c>/recall</c>'s "[1]" source
    /// markers would otherwise be eaten by the parser.
    /// </summary>
    public bool RendersMarkdown => !IsUser && !IsSystemNote;

    /// <inheritdoc cref="RendersMarkdown"/>
    public bool RendersLiteralText => !RendersMarkdown;

    /// <summary>Raw citation data backing <see cref="Citations"/>, kept so threads round-trip through persistence.</summary>
    public IReadOnlyList<MemoryCitation> CitationSources { get; set; } = System.Array.Empty<MemoryCitation>();

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
