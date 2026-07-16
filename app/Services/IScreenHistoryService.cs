namespace Floaty.Services;

/// <summary>
/// Automatic screen history: watches for foreground-window (and title) changes and records the
/// active window into memory according to <see cref="FloatyConfig.ScreenHistoryMode"/>. The
/// platform implementation hooks its own lifecycle; this marker exists so shared code can resolve
/// the service without referencing platform types.
/// </summary>
public interface IScreenHistoryService
{
}
