using Floaty.IconFont;

namespace Floaty.Services.Tools;

/// <summary>
/// Something a chat tool wants to do that the user has to confirm first: a shell command, a rewrite of
/// the system prompt, an update-and-restart. Rendered by the chat panel's approval card, whose labels
/// are filled straight from these fields.
/// </summary>
/// <param name="Header">The question on the card, e.g. "Run this command in PowerShell?".</param>
/// <param name="Detail">The exact thing being approved, shown verbatim in a monospace box - and folded
/// under the approved/declined note afterwards, so the conversation keeps a record of it.</param>
/// <param name="SubDetail">Optional small print under the box (a working directory, a version).</param>
/// <param name="ConfirmLabel">Text on the confirm button.</param>
/// <param name="ApprovedNote">System note added to the chat once approved.</param>
/// <param name="DeclinedNote">System note added to the chat once declined.</param>
/// <param name="Icon">Tabler glyph shown beside the header.</param>
/// <param name="GrantKey">When set, the card also offers "This chat" and "Always allow", and a
/// conversation-wide grant is remembered under this key. Only exec passes one: blanket-approving a
/// system-prompt rewrite or an update makes little sense.</param>
/// <param name="PreApproved">The user already allowed this (exec in automatic mode): no card, only the
/// note, so a command that ran without asking still shows up in the conversation.</param>
public sealed record ToolApprovalRequest(
    string Header,
    string Detail,
    string? SubDetail,
    string ConfirmLabel,
    string ApprovedNote,
    string DeclinedNote,
    string Icon = TablerLine.Settings,
    string? GrantKey = null,
    bool PreApproved = false);

/// <summary>
/// How the user answered an approval card. <see cref="Conversation"/> is remembered by the chat panel
/// for the current thread; <see cref="Always"/> is the caller's to persist, since only it knows which
/// setting the grant maps to.
/// </summary>
public enum ToolApprovalDecision
{
    Declined,
    Once,
    Conversation,
    Always,
}

/// <summary>
/// The current turn's approval callback. Set by <see cref="ChatService"/> at the start of a streaming
/// reply and read by any tool that needs the user's go-ahead; it flows through the async call chain into
/// the function-invocation middleware, the same way the citation and image sinks do.
/// </summary>
public static class ToolApproval
{
    private static readonly AsyncLocal<Func<ToolApprovalRequest, Task<ToolApprovalDecision>>?> _current = new();

    /// <summary>Null when the turn has no UI to ask (tools must then refuse, never proceed).</summary>
    public static Func<ToolApprovalRequest, Task<ToolApprovalDecision>>? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }

    /// <summary>
    /// Asks the user, or returns null when there is nobody to ask — callers turn that into a refusal
    /// sentence rather than going ahead unapproved.
    /// </summary>
    public static async Task<ToolApprovalDecision?> RequestAsync(ToolApprovalRequest request)
    {
        var approve = Current;
        if (approve is null)
            return null;

        return await approve(request);
    }
}
