using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Floaty;

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

    public ChatMessageVm(bool isUser, string text)
    {
        IsUser = isUser;
        _text = text;
    }

    public bool IsUser { get; }

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

    /// <summary>User bubbles hug the right edge, assistant bubbles the left.</summary>
    public LayoutOptions Alignment => IsUser ? LayoutOptions.End : LayoutOptions.Start;

    /// <summary>Blue for the user, neutral gray for the assistant.</summary>
    public Color BubbleColor => IsUser ? Color.FromArgb("#3A6DF0") : Color.FromArgb("#3A3A3F");

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
