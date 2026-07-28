namespace Floaty.Services;

/// <summary>
/// Fallback for platforms without a selection reader. Returning <c>null</c> makes the summon hotkey
/// behave exactly as it did before the feature existed: the ring glides over, just without a chip.
/// </summary>
public sealed class NullSelectionCaptureService : ISelectionCaptureService
{
    public Task<SelectedText?> TryCaptureAsync(nint foregroundHwnd, CancellationToken cancellationToken = default) =>
        Task.FromResult<SelectedText?>(null);
}
