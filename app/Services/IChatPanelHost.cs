namespace Floaty.Services;

/// <summary>
/// The window a <c>ChatPanelView</c> currently lives in. The panel owns its own content and gestures
/// but knows nothing about windows: whenever it needs to grow, move, or close, it asks its host, which
/// translates that into the right native window operation.
/// </summary>
/// <remarks>
/// Two hosts exist, matching <see cref="ChatPanelPlacement"/>:
/// <list type="bullet">
/// <item><c>OverlayPage</c> — the panel shares the ring's window, so a size request resizes that window
/// with the ring's edge anchored, and the available space is measured from the ring outward.</item>
/// <item><c>ChatWindowHost</c> — the panel has its own borderless window, so a size request resizes that
/// window and the available space is measured from the window's own rect.</item>
/// </list>
/// </remarks>
public interface IChatPanelHost
{
    /// <summary>
    /// The panel measured itself at <paramref name="widthDip"/> × <paramref name="heightDip"/> and wants
    /// the window to match. The host adds its own chrome (the ring's base for the overlay, a margin for
    /// the standalone window) and picks the anchor. Ignored while the host is animating.
    /// </summary>
    void RequestPanelSize(double widthDip, double heightDip);

    /// <summary>Widest the panel may grow without crossing the work-area edge, in device-independent units.</summary>
    double AvailableWidthDip();

    /// <summary>
    /// Tallest the message list may grow without pushing the window past the top of the work area.
    /// <paramref name="chromeDip"/> is the panel's fixed height around the list (input row, padding…),
    /// captured at drag start so it isn't re-measured while the layout is in flux.
    /// </summary>
    double AvailableListHeightDip(double chromeDip);

    /// <summary>
    /// Pins the window input-opaque for the duration of a gesture. The corner grip and drag bar are small
    /// targets that a fast drag leaves behind, and the click-through poll would otherwise drop the gesture.
    /// </summary>
    void SetForceInteractive(bool force);

    /// <summary>
    /// Moves the panel's window by a drag delta in device-independent units (the drag bar). No-op for the
    /// overlay host, whose panel is positioned by the ring.
    /// </summary>
    void MoveWindowBy(double dxDip, double dyDip);

    /// <summary>The user asked to close the panel (collapse chevron). The host runs its own hide animation.</summary>
    void CollapseRequested();

    /// <summary>
    /// Toggles the "waiting for the first model token" ring loader. The ring lives with the overlay in
    /// both placements, so the standalone host forwards this back to the overlay page.
    /// </summary>
    void SetBusy(bool busy);

}

/// <summary>
/// The ring's share of the chat panel's feedback. The ring lives in the overlay window under both
/// placements, so a panel in its own window routes these back to the overlay page.
/// </summary>
public interface IRingFeedback
{
    /// <summary>Runs (or stops) the ring's "waiting for the first model token" spin loader.</summary>
    void SetBusy(bool busy);
}
