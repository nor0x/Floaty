using Avalonia.Controls;
using Avalonia.Layout;
using Floaty.Services;

namespace Floaty.Views.Chat;

/// <summary>
/// Transparent host window for the standalone (fixed placement) chat panel.
/// </summary>
/// <remarks>
/// Replaces the MAUI <c>ChatWindowPage</c> plus the <c>Window</c> that wrapped it: in Avalonia the
/// window is the thing itself, so there is no page/window pair and no <c>NativeWindowBinder</c> dance
/// to route the controller at creation time.
/// </remarks>
public partial class ChatWindow : Window
{
    private readonly IChatWindowController _controller;

    public ChatWindow(IChatWindowController controller, ChatPanelView panel)
    {
        InitializeComponent();
        _controller = controller;
        Panel = panel;

        panel.VerticalAlignment = VerticalAlignment.Top;
        panel.HorizontalAlignment = HorizontalAlignment.Stretch;
        Root.Children.Add(panel);

        _controller.SetInteractiveHitTest(IsInteractiveAt);
        Closed += (_, _) => _controller.SetInteractiveHitTest(null);
    }

    public ChatPanelView Panel { get; }

    public bool IsInteractiveAt(double x, double y) => Panel.IsInteractiveAt(x, y);
}
