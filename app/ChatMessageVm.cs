using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
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
/// assistant's placeholder ("…") can be replaced in place once the LLM responds.
/// </summary>
public sealed class ChatMessageVm : INotifyPropertyChanged
{
    private string _text;
    private IReadOnlyList<CitationVm> _citations = System.Array.Empty<CitationVm>();

    public ChatMessageVm(bool isUser, string text, bool isSystemNote = false)
    {
        IsUser = isUser;
        _text = text;
        IsSystemNote = isSystemNote;
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

    /// <summary>Raw citation data backing <see cref="Citations"/>, kept so threads round-trip through persistence.</summary>
    public IReadOnlyList<MemoryCitation> CitationSources { get; set; } = System.Array.Empty<MemoryCitation>();

    /// <summary>User bubbles hug the right edge, assistant bubbles the left.</summary>
    public LayoutOptions Alignment => IsUser ? LayoutOptions.End : LayoutOptions.Start;

    /// <summary>Fixed width for assistant bubbles: 80% of the message list. Pushed by OverlayPage
    /// whenever the chat panel is resized; -1 (auto) until the list has been measured.</summary>
    public static double AssistantBubbleWidth { get; set; } = -1;

    /// <summary>Assistant answers are fixed at <see cref="AssistantBubbleWidth"/>; user messages and
    /// system notes size to their content (-1 = auto).</summary>
    public double BubbleWidthRequest => !IsUser && !IsSystemNote ? AssistantBubbleWidth : -1;

    /// <summary>Content-sized bubbles keep the classic 420 cap; assistant bubbles are governed by
    /// <see cref="BubbleWidthRequest"/> instead.</summary>
    public double BubbleMaxWidth => !IsUser && !IsSystemNote ? double.PositiveInfinity : 420;

    /// <summary>Re-raises <see cref="BubbleWidthRequest"/> after the panel is resized so bound bubbles reflow.</summary>
    public void RefreshBubbleWidth() => OnPropertyChanged(nameof(BubbleWidthRequest));

    /// <summary>Current accent for user bubbles; set by OverlayPage from settings/preview.</summary>
    public static Color UserBubbleColor { get; set; } = Color.FromArgb(Floaty.Services.AccentPalette.DefaultHex);

    /// <summary>Accent for the user, neutral gray for the assistant.</summary>
    public Color BubbleColor => IsUser ? UserBubbleColor : Color.FromArgb("#3A3A3F");

    /// <summary>Re-raises <see cref="BubbleColor"/> after the accent changes so bound bubbles repaint.</summary>
    public void RefreshBubbleColor() => OnPropertyChanged(nameof(BubbleColor));

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
