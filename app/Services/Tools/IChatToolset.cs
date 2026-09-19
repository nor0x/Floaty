using Microsoft.Extensions.AI;

namespace Floaty.Services.Tools;

/// <summary>
/// A group of chat tools with the system guidance that tells the model when to use them. Registered in
/// DI and folded into every turn by <see cref="ChatService"/>, so new tools don't have to grow that class.
/// </summary>
public interface IChatToolset
{
    /// <summary>
    /// Checked every turn. False hides the tools and their guidance entirely, so the model is never
    /// offered something that cannot work here (the same rule the notification tools follow).
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>The tools this set contributes. Built once; the list is not re-created per turn.</summary>
    IReadOnlyList<AITool> Tools { get; }

    /// <summary>System-message text explaining the tools, or null when their descriptions suffice.</summary>
    string? Guidance { get; }
}
