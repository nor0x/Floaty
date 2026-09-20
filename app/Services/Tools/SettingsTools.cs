using System.ComponentModel;
using System.Text;
using Floaty.IconFont;
using Microsoft.Extensions.AI;

namespace Floaty.Services.Tools;

/// <summary>
/// Lets the chat change Floaty's own look and feel: accent, ring, sounds, voice, and the system prompt.
/// </summary>
/// <remarks>
/// Deliberately limited to cosmetic settings plus the prompt. Provider keys, the shell, screen history,
/// autostart and MCP servers stay out of reach: a prompt-injected capture or MCP result must never be
/// able to widen what Floaty records or runs. Cosmetic writes apply immediately (easy to undo, and the
/// reply says what changed); a system-prompt write persists into every future chat, so it asks first.
///
/// Every write is mutate-then-<see cref="SettingsService.Save"/> on the live config — the same path the
/// ring's context menu takes. The Changed event reaches the overlay and chat panel, which re-apply on the
/// UI thread, and an open Settings window adopts the new values (see SettingsViewModel.AdoptExternalState).
/// </remarks>
public sealed class SettingsTools : IChatToolset
{
    // Characters of the resulting prompt shown on the approval card. The whole thing is written; this
    // only keeps a long prompt from pushing the buttons off-screen.
    private const int ApprovalPreviewChars = 1200;

    private static readonly string[] SpeechVoices =
        ["alloy", "ash", "ballad", "coral", "echo", "fable", "nova", "onyx", "sage", "shimmer", "verse"];

    private readonly SettingsService _settings;
    private readonly AiClientFactory _clients;
    private readonly UpdateService _updates;

    public SettingsTools(SettingsService settings, AiClientFactory clients, UpdateService updates)
    {
        _settings = settings;
        _clients = clients;
        _updates = updates;

        Tools =
        [
            AIFunctionFactory.Create(GetSettings, name: "get_settings"),
            AIFunctionFactory.Create(SetAppearance, name: "set_appearance"),
            AIFunctionFactory.Create(SetRingImage, name: "set_ring_image"),
            AIFunctionFactory.Create(SetSound, name: "set_sound"),
            AIFunctionFactory.Create(SetVoiceOutput, name: "set_voice_output"),
            AIFunctionFactory.Create(GetSystemPrompt, name: "get_system_prompt"),
            AIFunctionFactory.Create(UpdateSystemPrompt, name: "update_system_prompt"),
        ];
    }

    public bool IsAvailable => true;

    public IReadOnlyList<AITool> Tools { get; }

    public string Guidance =>
        "You can change Floaty's own settings when the user asks: call get_settings first to see the " +
        "current values and the valid choices, then set_appearance (accent color, ring size, always on " +
        "top, chat placement), set_ring_image, set_sound (capture and reply-finished sounds, volume) or " +
        "set_voice_output. Convert color names to a hex value yourself. Tell the user what you changed. " +
        "To change how you behave in every future chat, use update_system_prompt: prefer mode 'append' " +
        "for a new instruction, because 'replace' discards the built-in guidance about Floaty's tools. " +
        "The user confirms prompt changes in the UI; call the tool directly rather than asking first. " +
        "API keys, model providers, the shell, screen history and autostart can only be changed by the " +
        "user in Settings (⚙) — say so if asked.";

    [Description("Show Floaty's current appearance, sound and voice settings with the valid choices for " +
                 "each. Call before changing a setting.")]
    private string GetSettings()
    {
        var c = _settings.Current;
        var sb = new StringBuilder();

        sb.AppendLine($"Floaty version: {_updates.CurrentVersion}");
        sb.AppendLine();
        sb.AppendLine("Appearance:");
        sb.AppendLine($"- accent_color: {AccentPalette.Normalize(c.AccentColor)}");
        sb.AppendLine($"- ring_size: {SettingsService.ClampRingSize(c.RingSize):0} " +
                      $"(range {SettingsService.RingMinSize:0}-{SettingsService.RingMaxSize:0}, default {SettingsService.RingDefaultSize:0})");
        sb.AppendLine($"- ring_image: {(string.IsNullOrWhiteSpace(c.RingImageFileName) ? "(default)" : c.RingImageFileName)}");
        sb.AppendLine($"  choices: {string.Join(", ", _settings.GetBuiltInRingImages().Concat(_settings.GetAvailableRingImages()))}");
        sb.AppendLine($"- always_on_top: {c.AlwaysOnTop}");
        sb.AppendLine($"- chat_placement: {c.ChatPanelPlacement} (Floating = attached to the ring, Fixed = its own window)");
        sb.AppendLine();
        sb.AppendLine("Sounds:");
        sb.AppendLine($"- capture: {(c.CaptureSoundEnabled ? "on" : "off")}, " +
                      $"{Or(c.CaptureSoundFileName, SettingsService.DefaultCaptureSound)}");
        sb.AppendLine($"- reply (assistant reply finished): {(c.AssistantDoneSoundEnabled ? "on" : "off")}, " +
                      $"{Or(c.AssistantDoneSoundFileName, SettingsService.DefaultAssistantDoneSound)}");
        sb.AppendLine($"- volume: {SettingsService.ClampSoundVolume(c.SoundVolume) * 100:0}%");
        sb.AppendLine($"  choices: {string.Join(", ", _settings.GetBuiltInSounds().Concat(_settings.GetAvailableSounds()))}");

        sb.AppendLine();
        if (_clients.IsConfigured(ModelRole.Speech))
        {
            sb.AppendLine("Voice output (reading replies aloud):");
            sb.AppendLine($"- enabled: {c.VoiceOutputEnabled}");
            sb.AppendLine($"- voice: {Or(c.SpeechVoice, "alloy")} (choices: {string.Join(", ", SpeechVoices)})");
            sb.AppendLine($"- speed: {c.SpeechSpeed:0.##} (0.25-4)");
        }
        else
        {
            sb.AppendLine("Voice output: unavailable — no speech model is assigned in Settings → Model provider.");
        }

        return sb.ToString();
    }

    [Description("Change how Floaty looks. Pass only the settings to change; the rest stay as they are.")]
    private string SetAppearance(
        [Description("Accent color as hex, e.g. '#14b8a6'. Used for buttons, chat bubbles and highlights.")] string? accent_color = null,
        [Description("Ring diameter, 50-288 (default 148).")] double? ring_size = null,
        [Description("Keep the ring above other windows.")] bool? always_on_top = null,
        [Description("'Floating' (chat attached to the ring) or 'Fixed' (chat in its own window).")] string? chat_placement = null)
    {
        var config = _settings.Current;
        var changes = new List<string>();

        if (!string.IsNullOrWhiteSpace(accent_color))
        {
            if (!AccentPalette.IsValid(accent_color))
                return $"'{accent_color}' is not a hex color. Pass something like '#14b8a6'.";

            config.AccentColor = AccentPalette.Normalize(accent_color);
            changes.Add($"accent color {config.AccentColor}");
        }

        if (ring_size is { } size)
        {
            config.RingSize = SettingsService.ClampRingSize(size);
            changes.Add($"ring size {config.RingSize:0}");
        }

        if (always_on_top is { } onTop)
        {
            config.AlwaysOnTop = onTop;
            changes.Add(onTop ? "always on top" : "not always on top");
        }

        if (!string.IsNullOrWhiteSpace(chat_placement))
        {
            if (!Enum.TryParse<ChatPanelPlacement>(chat_placement.Trim(), ignoreCase: true, out var placement))
                return $"Unknown chat placement '{chat_placement}'. Use 'Floating' or 'Fixed'.";

            config.ChatPanelPlacement = placement;
            changes.Add($"chat placement {placement}");
        }

        if (changes.Count == 0)
            return "Nothing to change — pass at least one setting.";

        _settings.Save(config);
        return $"Updated: {string.Join(", ", changes)}.";
    }

    [Description("Set the image of Floaty's floating ring overlay. Pass a file name returned by " +
                 "generate_image or edit_image, a name from get_settings' ring choices, or 'default'. " +
                 "The overlay updates immediately and the choice persists.")]
    private string SetRingImage(
        [Description("The image file name to use as the ring, or 'default'.")] string file)
    {
        // NormalizeRingName so a model still holding the pre-rename names ("ring3.png", from an older
        // transcript or the user asking for "ring 3") lands on the built-in rather than an error.
        var name = SettingsService.NormalizeRingName(Path.GetFileName((file ?? string.Empty).Trim()));
        if (string.IsNullOrWhiteSpace(name))
            return "No image file was specified.";

        string ringName;
        if (string.Equals(name, "default", StringComparison.OrdinalIgnoreCase))
        {
            ringName = string.Empty;
        }
        else if (_settings.IsBuiltInRingImage(name) || _settings.GetRingImageFullPath(name) is not null)
        {
            ringName = name;
        }
        else
        {
            // Only generated images may be promoted to rings: this tool acts on model output, so its
            // reach is deliberately limited to the folder Floaty itself writes.
            var source = Path.Combine(FloatyPaths.GeneratedImages, name);
            if (!File.Exists(source))
                return $"No image named '{name}'. Call generate_image first, or pick one of get_settings' ring choices.";

            // A distinct prefix in the destination, so a ring the user put there themselves can never
            // be clobbered by a generated one that happens to share a name.
            ringName = $"ring-{Path.GetFileNameWithoutExtension(name)}.png";
            try
            {
                File.Copy(source, Path.Combine(FloatyPaths.RingImages, ringName), overwrite: true);
            }
            catch (Exception ex)
            {
                return $"Could not copy the image into the ring folder: {ex.Message}";
            }
        }

        var config = _settings.Current;
        config.RingImageFileName = ringName;
        _settings.Save(config);

        return ringName.Length == 0 ? "Floaty's ring is back to the default." : $"Floaty's ring is now '{ringName}'.";
    }

    [Description("Change one of Floaty's sounds: the capture shutter or the reply-finished chime. The new " +
                 "sound is played once so the user hears it. Pass only what should change.")]
    private string SetSound(
        [Description("Which sound: 'capture' (window captured) or 'reply' (assistant reply finished).")] string slot,
        [Description("Sound file name from get_settings' choices, or 'default'.")] string? sound = null,
        [Description("Turn this sound on or off.")] bool? enabled = null,
        [Description("Volume for all of Floaty's sounds, 0-100.")] double? volume_percent = null)
    {
        var config = _settings.Current;
        var isCapture = slot?.Trim().ToLowerInvariant() switch
        {
            "capture" or "shutter" or "screenshot" => true,
            "reply" or "done" or "assistant" or "finished" => false,
            _ => (bool?)null,
        };
        if (isCapture is null)
            return $"Unknown sound '{slot}'. Use 'capture' or 'reply'.";

        var changes = new List<string>();

        if (!string.IsNullOrWhiteSpace(sound))
        {
            var name = string.Equals(sound.Trim(), "default", StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : Path.GetFileName(sound.Trim());

            // Match case-insensitively against the real names so "Chime.wav" or "chime" both land.
            if (name.Length > 0)
            {
                var all = _settings.GetBuiltInSounds().Concat(_settings.GetAvailableSounds()).ToList();
                var match = all.FirstOrDefault(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase))
                            ?? all.FirstOrDefault(s => string.Equals(Path.GetFileNameWithoutExtension(s), name, StringComparison.OrdinalIgnoreCase));
                if (match is null || !_settings.IsValidSoundSelection(match))
                    return $"No sound named '{sound}'. Choices: {string.Join(", ", all)}.";
                name = match;
            }

            if (isCapture.Value)
                config.CaptureSoundFileName = name;
            else
                config.AssistantDoneSoundFileName = name;
            changes.Add($"sound {(name.Length == 0 ? "default" : name)}");
        }

        if (enabled is { } on)
        {
            if (isCapture.Value)
                config.CaptureSoundEnabled = on;
            else
                config.AssistantDoneSoundEnabled = on;
            changes.Add(on ? "on" : "off");
        }

        if (volume_percent is { } percent)
        {
            config.SoundVolume = SettingsService.ClampSoundVolume(percent / 100.0);
            changes.Add($"volume {config.SoundVolume * 100:0}%");
        }

        if (changes.Count == 0)
            return "Nothing to change — pass sound, enabled or volume_percent.";

        _settings.Save(config);

        var soundEnabled = isCapture.Value ? config.CaptureSoundEnabled : config.AssistantDoneSoundEnabled;
        if (soundEnabled)
        {
            var file = isCapture.Value
                ? Or(config.CaptureSoundFileName, SettingsService.DefaultCaptureSound)
                : Or(config.AssistantDoneSoundFileName, SettingsService.DefaultAssistantDoneSound);
            _settings.PreviewSound(file, config.SoundVolume);
        }

        return $"{(isCapture.Value ? "Capture" : "Reply-finished")} sound updated: {string.Join(", ", changes)}.";
    }

    [Description("Turn reading replies aloud on or off, or change the voice and speed.")]
    private string SetVoiceOutput(
        [Description("Read assistant replies aloud.")] bool? enabled = null,
        [Description("Voice name, e.g. alloy, coral, nova, onyx, shimmer.")] string? voice = null,
        [Description("Speech speed, 0.25-4 (1 is normal).")] double? speed = null)
    {
        if (!_clients.IsConfigured(ModelRole.Speech))
            return "Voice output needs a speech model. The user can assign one in Settings → Model provider.";

        var config = _settings.Current;
        var changes = new List<string>();

        if (enabled is { } on)
        {
            config.VoiceOutputEnabled = on;
            changes.Add(on ? "on" : "off");
        }

        if (!string.IsNullOrWhiteSpace(voice))
        {
            // Not restricted to OpenAI's list: a compatible endpoint may have its own voices.
            config.SpeechVoice = voice.Trim().ToLowerInvariant();
            changes.Add($"voice {config.SpeechVoice}");
        }

        if (speed is { } s)
        {
            config.SpeechSpeed = double.IsNaN(s) ? 1.0 : Math.Clamp(s, 0.25, 4.0);
            changes.Add($"speed {config.SpeechSpeed:0.##}");
        }

        if (changes.Count == 0)
            return "Nothing to change — pass enabled, voice or speed.";

        _settings.Save(config);
        return $"Voice output updated: {string.Join(", ", changes)}.";
    }

    [Description("Read Floaty's current system prompt — the standing instructions sent with every chat.")]
    private string GetSystemPrompt()
    {
        var prompt = _settings.GetSystemPrompt(SettingsService.DefaultSystemPrompt);
        var isDefault = string.Equals(prompt.Trim(), SettingsService.DefaultSystemPrompt, StringComparison.Ordinal);
        return (isDefault ? "The built-in default prompt is in use:\n\n" : "A custom prompt is in use:\n\n") + prompt;
    }

    [Description("Change Floaty's system prompt, the standing instructions for every future chat. The user " +
                 "confirms the change in the UI before it is saved.")]
    private async Task<string> UpdateSystemPrompt(
        [Description("'append' adds text to the end of the current prompt (preferred), 'replace' swaps the " +
                     "whole prompt for text, 'reset' restores the built-in default and ignores text.")] string mode,
        [Description("The instruction text to append, or the full new prompt for 'replace'.")] string? text = null)
    {
        var current = _settings.GetSystemPrompt(SettingsService.DefaultSystemPrompt);
        string next;
        string header;

        switch (mode?.Trim().ToLowerInvariant())
        {
            case "append":
                if (string.IsNullOrWhiteSpace(text))
                    return "Nothing to append — pass the instruction as text.";
                next = $"{current.TrimEnd()}\n\n{text.Trim()}";
                header = "Add this to Floaty's system prompt?";
                break;

            case "replace":
                if (string.IsNullOrWhiteSpace(text))
                    return "A replacement prompt cannot be empty. Use mode 'reset' to restore the default.";
                next = text.Trim();
                header = "Replace Floaty's system prompt?";
                break;

            case "reset":
                next = SettingsService.DefaultSystemPrompt;
                header = "Reset Floaty's system prompt to the default?";
                break;

            default:
                return $"Unknown mode '{mode}'. Use 'append', 'replace' or 'reset'.";
        }

        if (string.Equals(next, current, StringComparison.Ordinal))
            return "The system prompt already says exactly that; nothing changed.";

        // For an append the card shows only what is being added — that is the part to judge.
        var detail = mode!.Trim().Equals("append", StringComparison.OrdinalIgnoreCase) ? text!.Trim() : next;
        if (detail.Length > ApprovalPreviewChars)
            detail = detail[..ApprovalPreviewChars] + "…";

        var approved = await ToolApproval.RequestAsync(new ToolApprovalRequest(
            Header: header,
            Detail: detail,
            SubDetail: "Applies to every future chat. Editable in Settings → Behavior.",
            ConfirmLabel: "Save",
            ApprovedNote: "✏️ System prompt updated",
            DeclinedNote: "🚫 System prompt change declined",
            Icon: TablerLine.Pencil));

        if (approved is null)
            return "Cannot change the system prompt: no approval channel is available in this context.";
        if (approved == false)
            return "The user declined the system prompt change. Nothing was saved.";

        try
        {
            // The default is stored as an empty file so it keeps tracking future built-in updates.
            _settings.SaveSystemPrompt(mode.Trim().Equals("reset", StringComparison.OrdinalIgnoreCase) ? string.Empty : next);
        }
        catch (Exception ex)
        {
            return $"Could not save the system prompt: {ex.Message}";
        }

        return "System prompt saved. It takes effect from the next message.";
    }

    private static string Or(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;
}
