using System.Collections.ObjectModel;
using System.Diagnostics;

namespace Floaty.Services;

/// <summary>
/// The contract between the native <c>ChatPanelView</c> and the Blazor message list it hosts in a
/// <c>BlazorWebView</c>. Deliberately a plain object owned by the panel rather than a DI singleton:
/// the panel is transient and is torn down and rebuilt on every chat-placement change, so a shared
/// singleton would have to be re-pointed at the new panel each time. Passing this through the root
/// component's parameters instead makes the lifetime structural — new panel, new webview, new bridge.
/// </summary>
/// <remarks>
/// Data flows one way through <see cref="Messages"/> (the panel's own collection, observed directly by
/// the component) and the other way through the events below, which the panel translates into native
/// window operations. Everything is raised on the MAUI main thread except the JS callbacks, which the
/// panel marshals.
/// </remarks>
public sealed class ChatSurfaceBridge
{
    // Logged once if the webview's CSS pixels turn out not to equal MAUI's device-independent units,
    // so the assumption behind the height pipeline is observable rather than silently wrong.
    private bool _scaleMismatchLogged;

    public ChatSurfaceBridge(ObservableCollection<ChatMessageVm> messages)
    {
        Messages = messages;
    }

    /// <summary>The panel's live message collection; the component observes it directly.</summary>
    public ObservableCollection<ChatMessageVm> Messages { get; }

    /// <summary>Accent shades as CSS custom properties, applied as an inline style on the component's
    /// root so a settings preview recolours bubbles without a JS round-trip.</summary>
    public string AccentCss { get; private set; } =
        AccentPalette.From(AccentPalette.DefaultHex).ToCssVariables();

    /// <summary>The component finished its first render; the panel uses this to reveal the webview.</summary>
    public event EventHandler? Ready;

    public event EventHandler? AccentChanged;

    /// <summary>Jump the list to the newest message. The payload is whether to animate.</summary>
    public event EventHandler<bool>? ScrollRequested;

    /// <summary>The rendered content's height, already converted to device-independent units.</summary>
    public event EventHandler<double>? ContentHeightReported;

    /// <summary>A link inside a message was clicked; the panel opens it in the default browser.</summary>
    public event EventHandler<string>? ExternalLinkRequested;

    public void SetAccent(AccentPalette palette)
    {
        var css = palette.ToCssVariables();
        if (css == AccentCss)
            return;

        AccentCss = css;
        AccentChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SignalReady() => Ready?.Invoke(this, EventArgs.Empty);

    public void RequestScroll(bool smooth = true) => ScrollRequested?.Invoke(this, smooth);

    public void RequestOpenLink(string href) => ExternalLinkRequested?.Invoke(this, href);

    /// <summary>
    /// Called from JS whenever the rendered content resizes. CSS pixels are expected to equal MAUI's
    /// device-independent units, but the ratio is recomputed per report rather than assumed so that
    /// moving the overlay to a differently-scaled monitor stays correct.
    /// </summary>
    public void ReportContentHeight(double cssPixels, double devicePixelRatio)
    {
        var density = DeviceDisplay.Current.MainDisplayInfo.Density;
        var factor = density > 0 && devicePixelRatio > 0 ? devicePixelRatio / density : 1.0;

        if (!_scaleMismatchLogged && Math.Abs(factor - 1) > 0.01)
        {
            _scaleMismatchLogged = true;
            Debug.WriteLine($"[Floaty] Chat webview CSS px != DIP (dpr {devicePixelRatio}, density {density}).");
        }

        ContentHeightReported?.Invoke(this, cssPixels * factor);
    }

    /// <summary>Drops every subscriber, so a torn-down panel stops hearing from its webview.</summary>
    public void Dispose()
    {
        Ready = null;
        AccentChanged = null;
        ScrollRequested = null;
        ContentHeightReported = null;
        ExternalLinkRequested = null;
    }
}
