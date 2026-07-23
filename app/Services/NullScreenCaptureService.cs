namespace Floaty.Services;

/// <summary>
/// Fallback for platforms without a desktop capture implementation (Android / iOS, and Mac for now).
/// Mac can later capture via <c>CGWindowListCreateImage</c> + <c>AXUIElement</c>.
/// </summary>
public sealed class NullScreenCaptureService : IScreenCaptureService
{
    public Task<CaptureResult?> CaptureUnderlyingWindowAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<CaptureResult?>(null);

    public Task<CaptureResult?> CaptureWindowAsync(
        nint hwnd,
        bool includeScreenshot,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<CaptureResult?>(null);

    public Task<IReadOnlyList<WindowInfo>> ListWindowsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<WindowInfo>>(Array.Empty<WindowInfo>());
}
