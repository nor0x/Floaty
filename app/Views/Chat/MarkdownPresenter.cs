using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Floaty.IconFont;
using Floaty.Services;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Floaty.Views.Chat;

/// <summary>
/// Renders an assistant message by walking the Markdig document <see cref="MarkdownRenderer"/> hands
/// back and building Avalonia controls from it.
/// </summary>
/// <remarks>
/// Written by hand rather than using an off-the-shelf markdown control because every such control
/// brings its own parser, which would bypass <see cref="MarkdownRenderer"/>'s fence repair and URL
/// allowlist - the two things standing between untrusted model output and the overlay. It handles the
/// subset a chat reply actually produces; anything unrecognised falls back to its literal text, so an
/// exotic construct degrades to something readable rather than vanishing.
/// </remarks>
public sealed class MarkdownPresenter : ContentControl
{
    /// <summary>
    /// Raised when a link inside the rendered text is clicked. A routed event rather than a plain CLR
    /// one because the presenter is created inside a DataTemplate: the panel adds a single handler on
    /// its outer Border and every bubble's links bubble up to it.
    /// </summary>
    public static readonly RoutedEvent<LinkClickedEventArgs> LinkClickedEvent =
        RoutedEvent.Register<MarkdownPresenter, LinkClickedEventArgs>(
            nameof(LinkClicked), RoutingStrategies.Bubble);

    public event EventHandler<LinkClickedEventArgs> LinkClicked
    {
        add => AddHandler(LinkClickedEvent, value);
        remove => RemoveHandler(LinkClickedEvent, value);
    }

    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownPresenter, string?>(nameof(Markdown));

    /// <summary>Raw markdown. Replaced wholesale on every streaming repaint.</summary>
    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    // What the currently rendered tree was built from. Reference equality is enough: ChatMessageVm
    // replaces Text wholesale rather than mutating it, so a new string means new content.
    private string? _renderedFrom;

    private static readonly FontFamily MonoFont = new("Cascadia Mono,Consolas,Courier New,monospace");
    private static readonly IBrush CodeBackground = new SolidColorBrush(Color.Parse("#33000000"));
    private static readonly IBrush RuleBrush = new SolidColorBrush(Color.Parse("#33FFFFFF"));
    private static readonly IBrush QuoteBarBrush = new SolidColorBrush(Color.Parse("#55FFFFFF"));
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#CCFFFFFF"));

    static MarkdownPresenter()
    {
        MarkdownProperty.Changed.AddClassHandler<MarkdownPresenter>((presenter, _) => presenter.Rebuild());
    }

    public MarkdownPresenter()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
    }

    private void Rebuild()
    {
        var markdown = Markdown;
        if (ReferenceEquals(markdown, _renderedFrom))
            return;
        _renderedFrom = markdown;

        // A full rebuild per streaming repaint. At ~30fps over a few KB this is comfortably cheap, and
        // diffing a markdown AST for a document that is append-only in practice would cost more in
        // complexity than it saves.
        var blocks = new StackPanel { Spacing = 6 };
        foreach (var block in MarkdownRenderer.Parse(markdown))
        {
            if (BuildBlock(block) is { } control)
                blocks.Children.Add(control);
        }

        Content = blocks;
    }

    // --- Blocks ---

    private Control? BuildBlock(Block block) => block switch
    {
        HeadingBlock heading => BuildHeading(heading),
        ParagraphBlock paragraph => BuildParagraph(paragraph),
        ListBlock list => BuildList(list),
        QuoteBlock quote => BuildQuote(quote),
        CodeBlock code => BuildCodeBlock(code),
        Table table => BuildTable(table),
        ThematicBreakBlock => new Rectangle { Height = 1, Fill = RuleBrush, Margin = new Thickness(0, 4) },
        _ => null,
    };

    private Control BuildHeading(HeadingBlock heading)
    {
        var text = NewTextBlock();
        // Markdown headings inside a chat bubble are section labels, not page titles, so the scale is
        // compressed - h1 in a two-line answer should not dwarf the answer.
        text.FontSize = heading.Level switch { 1 => 17, 2 => 15.5, 3 => 14.5, _ => 13.5 };
        text.FontWeight = FontWeight.SemiBold;
        AppendInlines(text.Inlines!, heading.Inline);
        return text;
    }

    private Control BuildParagraph(ParagraphBlock paragraph)
    {
        var text = NewTextBlock();
        AppendInlines(text.Inlines!, paragraph.Inline);
        return text;
    }

    private Control BuildList(ListBlock list)
    {
        var panel = new StackPanel { Spacing = 2 };
        var index = list.IsOrdered && int.TryParse(list.OrderedStart, out var start) ? start : 1;

        foreach (var item in list.OfType<ListItemBlock>())
        {
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                Margin = new Thickness(2, 0, 0, 0),
            };

            var marker = NewTextBlock();
            marker.Margin = new Thickness(0, 0, 6, 0);
            marker.Foreground = MutedBrush;
            marker.Text = list.IsOrdered ? $"{index++}." : "•";

            var content = new StackPanel { Spacing = 2 };
            foreach (var child in item)
            {
                if (BuildBlock(child) is { } control)
                    content.Children.Add(control);
            }

            // A task-list item replaces the bullet with its checkbox glyph.
            if (item.FirstOrDefault() is ParagraphBlock { Inline: { } inline }
                && inline.FirstChild is TaskList task)
            {
                marker.Text = task.Checked ? TablerLine.SquareCheck : TablerLine.SquareRounded;
                marker.FontFamily = (FontFamily?)Application.Current?.FindResource("TablerIcons") ?? marker.FontFamily;
            }

            Grid.SetColumn(marker, 0);
            Grid.SetColumn(content, 1);
            row.Children.Add(marker);
            row.Children.Add(content);
            panel.Children.Add(row);
        }

        return panel;
    }

    private Control BuildQuote(QuoteBlock quote)
    {
        var content = new StackPanel { Spacing = 4 };
        foreach (var child in quote)
        {
            if (BuildBlock(child) is { } control)
                content.Children.Add(control);
        }

        return new Border
        {
            BorderBrush = QuoteBarBrush,
            BorderThickness = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(8, 2, 0, 2),
            Child = content,
        };
    }

    private Control BuildCodeBlock(CodeBlock code)
    {
        var text = ExtractCode(code);

        var body = new SelectableTextBlock
        {
            Text = text,
            FontFamily = MonoFont,
            FontSize = 12,
            TextWrapping = TextWrapping.NoWrap,
            Foreground = Foreground,
        };

        // Long lines scroll rather than forcing the whole panel wider - the panel's width is the
        // user's, set by the resize grip, and a wide code block must not fight it.
        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = body,
        };

        // A copy button: the WebView version never had one, and a code block the user cannot easily
        // lift out of the overlay is most of the point of asking for it.
        var copy = new Button
        {
            Content = new TextBlock
            {
                Text = TablerLine.Copy,
                FontFamily = (FontFamily?)Application.Current?.FindResource("TablerIcons") ?? MonoFont,
                FontSize = 13,
            },
            Padding = new Thickness(5, 2),
            Background = Brushes.Transparent,
            Foreground = MutedBrush,
            BorderThickness = default,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 2, 0),
        };
        ToolTip.SetTip(copy, "Copy code");
        copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(text);
        };

        return new Border
        {
            Background = CodeBackground,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 6),
            Child = new Panel { Children = { scroller, copy } },
        };
    }

    private static string ExtractCode(CodeBlock code)
    {
        var builder = new StringBuilder();
        foreach (var line in code.Lines.Lines)
        {
            var slice = line.Slice;
            if (slice.Text is null)
                continue;
            builder.AppendLine(slice.Text.Substring(slice.Start, slice.Length));
        }

        return builder.ToString().TrimEnd('\r', '\n');
    }

    private Control BuildTable(Table table)
    {
        var grid = new Grid();
        var columnCount = table.OfType<TableRow>().Max(r => r.Count);
        for (var i = 0; i < columnCount; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        var rowIndex = 0;
        foreach (var row in table.OfType<TableRow>())
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var columnIndex = 0;
            foreach (var cell in row.OfType<TableCell>())
            {
                var content = new StackPanel();
                foreach (var child in cell)
                {
                    if (BuildBlock(child) is { } control)
                    {
                        if (row.IsHeader && control is TextBlock header)
                            header.FontWeight = FontWeight.SemiBold;
                        content.Children.Add(control);
                    }
                }

                var bordered = new Border
                {
                    BorderBrush = RuleBrush,
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Padding = new Thickness(6, 3),
                    Child = content,
                };

                Grid.SetRow(bordered, rowIndex);
                Grid.SetColumn(bordered, columnIndex++);
                grid.Children.Add(bordered);
            }

            rowIndex++;
        }

        // Same reasoning as code blocks: a wide table scrolls inside the bubble.
        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = grid,
        };
    }

    private TextBlock NewTextBlock() => new()
    {
        TextWrapping = TextWrapping.Wrap,
        FontSize = 13,
        Foreground = Foreground,
        Inlines = [],
    };

    // --- Inlines ---

    private void AppendInlines(InlineCollection target, ContainerInline? container)
    {
        if (container is null)
            return;

        foreach (var inline in container)
            AppendInline(target, inline);
    }

    private void AppendInline(InlineCollection target, Markdig.Syntax.Inlines.Inline inline)
    {
        switch (inline)
        {
            case LiteralInline literal:
                target.Add(new Run(literal.Content.ToString()));
                break;

            case EmphasisInline emphasis:
                {
                    var span = new Span();
                    // Markdig reports the delimiter and how many of them: ** is bold, * is italic,
                    // ~~ is strikethrough (from UseEmphasisExtras).
                    if (emphasis.DelimiterChar is '~')
                        span.TextDecorations = TextDecorations.Strikethrough;
                    else if (emphasis.DelimiterCount >= 2)
                        span.FontWeight = FontWeight.Bold;
                    else
                        span.FontStyle = FontStyle.Italic;

                    AppendInlines(span.Inlines, emphasis);
                    target.Add(span);
                    break;
                }

            case CodeInline code:
                target.Add(new Run(code.Content) { FontFamily = MonoFont, Background = CodeBackground });
                break;

            case LineBreakInline lineBreak:
                // UseSoftlineBreakAsHardlineBreak means a single newline is a real break here.
                target.Add(lineBreak.IsHard ? new LineBreak() : new LineBreak());
                break;

            case TaskList:
                // Rendered as the list marker instead; see BuildList.
                break;

            case LinkInline { IsImage: true } image:
                AppendImage(target, image);
                break;

            case LinkInline link:
                AppendLink(target, link);
                break;

            case AutolinkInline autolink:
                AppendRunLink(target, autolink.Url, autolink.Url);
                break;

            case ContainerInline container:
                AppendInlines(target, container);
                break;

            default:
                // Unknown constructs degrade to their literal text rather than disappearing.
                if (inline.ToString() is { Length: > 0 } fallback)
                    target.Add(new Run(fallback));
                break;
        }
    }

    private void AppendLink(InlineCollection target, LinkInline link)
    {
        var label = new StringBuilder();
        foreach (var child in link)
        {
            if (child is LiteralInline literal)
                label.Append(literal.Content.ToString());
        }

        var text = label.Length > 0 ? label.ToString() : link.Url ?? string.Empty;
        AppendRunLink(target, text, link.Url);
    }

    private void AppendRunLink(InlineCollection target, string text, string? url)
    {
        // MarkdownRenderer blanks the URL of any link that fails the scheme allowlist, keeping the
        // text. Such a link renders as plain text rather than as something clickable.
        if (string.IsNullOrEmpty(url))
        {
            target.Add(new Run(text));
            return;
        }

        var run = new Run(text)
        {
            Foreground = Application.Current?.FindResource("AccentIconOnDarkColor") is Color accent
                ? new SolidColorBrush(accent)
                : Foreground,
            TextDecorations = TextDecorations.Underline,
        };

        // Inlines are not interactive in Avalonia, so the click is handled on the parent TextBlock by
        // hit-testing the character index back to this run.
        _linkTargets[run] = url;
        target.Add(run);
    }

    private readonly Dictionary<Run, string> _linkTargets = new();

    private void AppendImage(InlineCollection target, LinkInline image)
    {
        // Only inline data: URIs survive MarkdownRenderer's allowlist, so this never touches the
        // network. Anything else has already had its URL blanked.
        if (string.IsNullOrEmpty(image.Url) || !image.Url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            target.Add(new Run(image.Title ?? "[image]"));
            return;
        }

        try
        {
            var comma = image.Url.IndexOf(',');
            if (comma < 0)
                return;

            var bytes = Convert.FromBase64String(image.Url[(comma + 1)..]);
            using var stream = new MemoryStream(bytes);
            target.Add(new InlineUIContainer(new Image
            {
                Source = new Bitmap(stream),
                MaxWidth = 360,
                Stretch = Stretch.Uniform,
            }));
        }
        catch
        {
            // A malformed data URI is model output, not a bug: show the alt text and move on.
            target.Add(new Run(image.Title ?? "[image]"));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (_linkTargets.Count == 0 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        // Walk the hit visual up to a TextBlock, then map the click to the run under it.
        if (e.Source is not Visual source)
            return;

        var text = source as TextBlock ?? source.FindAncestorOfType<TextBlock>();
        if (text?.Inlines is null)
            return;

        var position = e.GetPosition(text);
        var hit = text.TextLayout.HitTestPoint(position);
        if (!hit.IsInside)
            return;

        var offset = 0;
        foreach (var inline in Flatten(text.Inlines))
        {
            if (inline is not Run run)
                continue;

            var length = run.Text?.Length ?? 0;
            if (hit.TextPosition >= offset && hit.TextPosition < offset + length)
            {
                if (_linkTargets.TryGetValue(run, out var url))
                {
                    RaiseEvent(new LinkClickedEventArgs(LinkClickedEvent, url));
                    e.Handled = true;
                }

                return;
            }

            offset += length;
        }
    }

    private static IEnumerable<Avalonia.Controls.Documents.Inline> Flatten(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            yield return inline;
            if (inline is Span span)
            {
                foreach (var child in Flatten(span.Inlines))
                    yield return child;
            }
        }
    }
}

/// <summary>Carries the URL of a link clicked inside a rendered markdown bubble.</summary>
public sealed class LinkClickedEventArgs : RoutedEventArgs
{
    public LinkClickedEventArgs(RoutedEvent routedEvent, string url)
        : base(routedEvent) => Url = url;

    public string Url { get; }
}
