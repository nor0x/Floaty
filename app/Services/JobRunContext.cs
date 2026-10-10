namespace Floaty.Services;

/// <summary>
/// The recurring job the current async flow is running on behalf of, or null in an ordinary chat turn.
/// Set by <see cref="JobScheduler"/> around a run and read by <see cref="Tools.JobTools"/>, which hides
/// itself inside a run so a job can never create, rewrite or delete jobs - an unattended prompt fed by
/// whatever it reads must not be able to schedule more of itself.
/// </summary>
/// <remarks>Flows like <see cref="Tools.ToolApproval.Current"/>: through the async call chain into the tools.</remarks>
public static class JobRunContext
{
    private static readonly AsyncLocal<string?> _current = new();

    public static string? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }
}
