using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace Floaty.Services.Tools;

/// <summary>
/// <c>show_toast</c>: lets the model slide a toast out of the ring on its own initiative - progress on a
/// long job, a job finishing, something that wants a glance. The automatic reply preview is not this; it
/// is raised by the chat panel whenever a reply lands with the chat closed, model or no model.
/// </summary>
/// <remarks>
/// Images resolve only through <see cref="GeneratedImageUri"/>, the markdown renderer's own allowlist, so
/// model output can never point the overlay at an arbitrary file on disk.
/// </remarks>
public sealed class ToastTools : IChatToolset
{
    private const int MaxMessageChars = 300;
    private const int MaxTitleChars = 60;

    private readonly ToastService _toasts;
    private readonly SettingsService _settings;

    public ToastTools(ToastService toasts, SettingsService settings)
    {
        _toasts = toasts;
        _settings = settings;

        Tools = [AIFunctionFactory.Create(ShowToast, name: "show_toast")];
    }

    public bool IsAvailable => _toasts.IsAvailable;

    public IReadOnlyList<AITool> Tools { get; }

    public string Guidance =>
        "show_toast slides a short toast out of Floaty's ring, visible even while the chat is closed. Use it " +
        "for status the user should notice without looking at the chat: progress on a long multi-step task, " +
        "a job that finished, something that needs their attention. Keep it to a sentence or two. Don't use " +
        "it to repeat your answer - when the chat is closed Floaty already shows a preview of every reply. " +
        "For something at a later time, use notify instead.";

    [Description("Show a short toast that slides out of Floaty's ring on the desktop, for a status update " +
                 "the user should see even with the chat closed. Not for repeating your reply.")]
    private async Task<string> ShowToast(
        [Description("The toast text: one or two short sentences.")] string message,
        [Description("Optional short title, a few words.")] string? title = null,
        [Description("Optional picture: the floaty://image/<file> URL of an image generate_image or " +
                     "edit_image produced this conversation.")] string? image = null,
        [Description("info (default), success, warning or error.")] string? kind = null)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "Nothing to show: message is empty.";

        string? imagePath = null;
        var imageNote = string.Empty;
        if (!string.IsNullOrWhiteSpace(image))
        {
            var url = image.Trim();
            if (!url.StartsWith(GeneratedImageUri.Prefix, StringComparison.OrdinalIgnoreCase))
                url = GeneratedImageUri.For(Path.GetFileName(url));

            imagePath = GeneratedImageUri.ResolvePath(url);
            if (imagePath is null)
                imageNote = " The image was left out: only pictures from generate_image / edit_image can be shown.";
        }

        var toast = new FloatyToast(Clip(message.Trim(), MaxMessageChars))
        {
            Title = string.IsNullOrWhiteSpace(title) ? null : Clip(title.Trim(), MaxTitleChars),
            Kind = ParseKind(kind),
            ImagePath = imagePath,
        };

        var outcome = await _toasts.ShowAsync(toast);
        var seconds = Math.Clamp(_settings.Current.ToastDurationSeconds,
            FloatyConfig.MinToastSeconds, FloatyConfig.MaxToastSeconds);

        return outcome switch
        {
            ToastOutcome.Shown => $"Shown on the ring for about {seconds:0} seconds.{imageNote}",
            ToastOutcome.ShownInChat => "The chat is open, so it was shown as a status line in the chat instead." + imageNote,
            ToastOutcome.OverlayHidden => "Not shown: Floaty's ring is hidden right now.",
            _ => "Not shown: toasts aren't available yet.",
        };
    }

    // "reply" is the automatic preview's own look, so the model gets info in its place.
    private static ToastKind ParseKind(string? kind) =>
        Enum.TryParse<ToastKind>(kind?.Trim(), ignoreCase: true, out var parsed) && parsed != ToastKind.Reply
            ? parsed
            : ToastKind.Info;

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max].TrimEnd() + "…";
}
