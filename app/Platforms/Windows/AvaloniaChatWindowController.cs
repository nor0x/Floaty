using Floaty.Services;

namespace Floaty.Platforms.Windows;

/// <summary>
/// The standalone chat window (fixed placement). It needs nothing beyond the shared borderless
/// behavior; the distinct type exists so DI can hand the overlay and the chat panel their own
/// controller instances.
/// </summary>
public sealed class AvaloniaChatWindowController : AvaloniaBorderlessWindowController, IChatWindowController
{
}
