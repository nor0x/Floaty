using Floaty.Services;

namespace Floaty;

/// <summary>
/// Transparent host page for the standalone fixed chat window.
/// </summary>
public partial class ChatWindowPage : ContentPage
{
	private readonly IChatWindowController _controller;
	private readonly ChatPanelView _panel;

	public ChatWindowPage(IChatWindowController controller, ChatPanelView panel)
	{
		InitializeComponent();
		_controller = controller;
		_panel = panel;

		_panel.VerticalOptions = LayoutOptions.Start;
		_panel.HorizontalOptions = LayoutOptions.Fill;
		Root.Children.Add(_panel);

		_controller.SetInteractiveHitTest(IsInteractiveAt);
	}

	public ChatPanelView Panel => _panel;

	public bool IsInteractiveAt(double x, double y) => _panel.IsInteractiveAt(x, y);

	protected override void OnDisappearing()
	{
		_controller.SetInteractiveHitTest(null);
		base.OnDisappearing();
	}
}
