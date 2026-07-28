namespace Floaty;

/// <summary>
/// Layout helpers shared by the overlay page and the chat panel, which both have to translate MAUI
/// element bounds into the window-client coordinates the native click-through hit-test works in.
/// </summary>
internal static class VisualElementExtensions
{
    /// <summary>
    /// Element bounds in page coordinates (== window-client DIPs, since the page fills the window):
    /// Frame is the post-margin arranged rect in parent coordinates, so accumulating it up the tree
    /// handles negative margins and Border padding automatically. Works from either host, since the
    /// walk simply stops at whichever page the element currently lives in.
    /// </summary>
    public static Rect BoundsInPage(this VisualElement element)
    {
        double x = 0, y = 0;
        Element? current = element;
        while (current is VisualElement visual)
        {
            x += visual.Frame.X + visual.TranslationX;
            y += visual.Frame.Y + visual.TranslationY;
            current = current.Parent;
            if (current is Page)
                break;
        }
        return new Rect(x, y, element.Width, element.Height);
    }
}
