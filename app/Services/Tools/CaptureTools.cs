using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.AI;

namespace Floaty.Services.Tools;

/// <summary>
/// Capturing named windows from chat: once right now ("what's in my Notepad window?"), or as a standing
/// rule ("capture Visual Studio every 5 minutes", "capture Notepad every time it opens").
/// </summary>
/// <remarks>
/// Rules are created without an approval card, by the user's choice. What keeps that safe is in
/// <see cref="CaptureRuleService.CaptureAsync"/>: password managers and private-browsing windows are
/// refused whatever a rule says, and captured text is redacted like screen history's.
/// </remarks>
public sealed class CaptureTools : IChatToolset
{
    // How much of a one-off capture's text goes back to the model. It is also in memory in full, so
    // search_captures / read_capture can reach the rest.
    private const int MaxReturnedChars = 3000;

    private readonly CaptureRuleService _rules;
    private readonly IScreenCaptureService _screens;
    private readonly SettingsService _settings;

    public CaptureTools(CaptureRuleService rules, IScreenCaptureService screens, SettingsService settings)
    {
        _rules = rules;
        _screens = screens;
        _settings = settings;

        Tools =
        [
            AIFunctionFactory.Create(CaptureWindow, name: "capture_window"),
            AIFunctionFactory.Create(CreateCaptureRule, name: "create_capture_rule"),
            AIFunctionFactory.Create(ListCaptureRules, name: "list_capture_rules"),
            AIFunctionFactory.Create(SetCaptureRuleEnabled, name: "set_capture_rule_enabled"),
            AIFunctionFactory.Create(DeleteCaptureRule, name: "delete_capture_rule"),
        ];
    }

    // The null capture service (non-Windows builds) can't list or capture anything.
    public bool IsAvailable => _screens is not NullScreenCaptureService;

    public IReadOnlyList<AITool> Tools { get; }

    public string Guidance =>
        "You can capture a specific open window into memory: capture_window does it once, right now, and " +
        "returns its text, so use it when the user asks about a window that is open but not in front. " +
        "create_capture_rule sets up a standing capture — when a matching window opens, once N minutes " +
        "after it opens, or every N minutes while it is open. Match on the app's process name where you know it (Visual Studio is " +
        "devenv, VS Code is Code, Word is WINWORD, Excel is EXCEL, Notepad is notepad), otherwise on a " +
        "word from its window title. Include a screenshot unless the user asks for text only. Rule " +
        "captures land in memory, where search_captures finds them. Rules can also be managed in " +
        "Settings → Screen history. Password managers and private browser windows are never captured.";

    [Description("Capture an open window into memory right now and return its text. Use when the user asks " +
                 "about, or wants to save, a specific window — including one that is not in front.")]
    private async Task<string> CaptureWindow(
        [Description("App process name (e.g. 'devenv', 'notepad') or a word from the window title.")] string match,
        [Description("Also save a screenshot of the window.")] bool include_screenshot = true)
    {
        if (string.IsNullOrWhiteSpace(match))
            return "Say which window to capture.";

        var windows = await _rules.FindWindowsAsync(match);
        if (windows.Count == 0)
            return $"No open window matches '{match}'. Call list_windows to see what is open (minimized windows can't be captured).";

        var window = windows[0];
        var outcome = await _rules.CaptureAsync(
            window, include_screenshot, IMemoryService.ManualCaptureSource, skipUnchanged: false);
        if (!outcome.Stored || outcome.Capture is not { } capture)
            return outcome.Message;

        var sb = new StringBuilder($"Captured \"{window.Title}\" ({window.ProcessName}) into memory");
        sb.Append(string.IsNullOrEmpty(capture.ImagePath) ? " (text only)." : " with a screenshot.");
        if (windows.Count > 1)
            sb.Append($" {windows.Count - 1} other matching window(s) were left out; name one more precisely to capture it.");

        var text = capture.Content.Trim();
        sb.Append("\n\n");
        if (text.Length == 0)
            sb.Append("The window exposes no readable text.");
        else if (text.Length <= MaxReturnedChars)
            sb.Append(text);
        else
            sb.Append(text[..MaxReturnedChars])
              .Append($"\n[truncated — call read_capture with file '{Path.GetFileName(capture.TextPath)}' for the rest]");

        return sb.ToString();
    }

    [Description("Set up a standing capture of an app's windows into memory: once each time a matching " +
                 "window opens, once a set delay after it opens, or repeatedly every N minutes while one is open. Runs in the background " +
                 "until deleted or expired, even with screen history off.")]
    private async Task<string> CreateCaptureRule(
        [Description("App process name (e.g. 'devenv', 'notepad') or a word from the window title.")] string match,
        [Description("'on_open' to capture each newly opened window once, 'after_open' to capture it once " +
                     "delay_seconds after it opens, or 'interval' to capture every interval_minutes.")] string trigger,
        [Description("For 'interval': minutes between captures, 1-1440 (default 5).")] int interval_minutes = 5,
        [Description("For 'after_open': seconds to wait after the window opens, 1-86400 (default 30; 5 minutes is 300).")] int delay_seconds = 30,
        [Description("Also save screenshots, not just the text.")] bool include_screenshot = true,
        [Description("Optional: stop after this many hours. Leave at 0 to keep running until deleted.")] double expires_in_hours = 0,
        [Description("The user's request in a few words, shown in rule lists.")] string? note = null)
    {
        if (string.IsNullOrWhiteSpace(match))
            return "A rule needs something to match, e.g. 'notepad' or 'Visual Studio'.";

        CaptureRuleTrigger kind;
        switch (trigger?.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_"))
        {
            case "on_open" or "open" or "opens" or "when_opened":
                kind = CaptureRuleTrigger.OnOpen;
                break;
            case "after_open" or "after_opened" or "delay" or "delayed":
                kind = CaptureRuleTrigger.AfterOpen;
                break;
            case "interval" or "every" or "periodic" or "repeat":
                kind = CaptureRuleTrigger.Interval;
                break;
            default:
                return $"Unknown trigger '{trigger}'. Use 'on_open', 'after_open' or 'interval'.";
        }

        var rule = new CaptureRule
        {
            Id = NewId(),
            Match = match.Trim(),
            Trigger = kind,
            IntervalMinutes = Math.Clamp(interval_minutes, 1, 1440),
            DelaySeconds = Math.Clamp(delay_seconds, 1, 86400),
            IncludeScreenshot = include_screenshot,
            ExpiresAt = expires_in_hours > 0 ? DateTimeOffset.Now.AddHours(Math.Min(expires_in_hours, 24 * 365)) : null,
            Note = note?.Trim() ?? string.Empty,
            CreatedAt = DateTimeOffset.Now,
        };

        var config = _settings.Current;
        config.CaptureRules.Add(rule);
        _settings.Save(config);

        var open = await _rules.FindWindowsAsync(rule.Match);
        var sb = new StringBuilder($"Created capture rule {rule.Id}: {Describe(rule)}.");

        if (open.Count == 0)
        {
            sb.Append($" No window matches '{rule.Match}' right now");
            sb.Append(rule.Trigger switch
            {
                CaptureRuleTrigger.OnOpen => "; the next one that opens will be captured about 20 seconds after it appears.",
                CaptureRuleTrigger.AfterOpen => $"; the next one that opens will be captured about {FormatDelay(rule.DelaySeconds)} later.",
                _ => "; capturing starts once one opens.",
            });
        }
        else
        {
            sb.Append($" Currently matching: {string.Join("; ", open.Take(5).Select(w => $"\"{w.Title}\" ({w.ProcessName})"))}.");
            sb.Append(rule.Trigger == CaptureRuleTrigger.Interval
                ? " The first capture happens within about 20 seconds."
                : " Those are already open, so they are skipped; windows opened from now on are captured.");
        }

        if (open.Count > 0 && !open.Any(w => string.Equals(w.ProcessName, rule.Match, StringComparison.OrdinalIgnoreCase)))
            sb.Append(" Check those are the right windows — if not, delete this rule and match on the process name instead.");

        return sb.ToString();
    }

    [Description("List the capture rules with their ids, what they match, when they fire and when they last captured.")]
    private string ListCaptureRules()
    {
        var rules = _settings.Current.CaptureRules;
        if (rules.Count == 0)
            return "There are no capture rules.";

        var now = DateTimeOffset.Now;
        var sb = new StringBuilder($"{rules.Count} capture rule(s):");
        foreach (var r in rules)
        {
            sb.Append("\n- ").Append(r.Id).Append(": ").Append(Describe(r));
            if (!r.Enabled)
                sb.Append(" [paused]");
            if (r.ExpiresAt is { } exp)
                sb.Append(exp <= now ? " [expired]" : $" [until {exp:ddd d MMM HH:mm}]");
            if (_rules.LastCaptured(r.Id) is { } last)
                sb.Append($", last captured {last:HH:mm}");
            if (!string.IsNullOrWhiteSpace(r.Note))
                sb.Append(" — \"").Append(r.Note).Append('"');
        }

        return sb.ToString();
    }

    [Description("Pause or resume a capture rule without deleting it.")]
    private string SetCaptureRuleEnabled(
        [Description("The rule id from list_capture_rules.")] string id,
        [Description("True to resume, false to pause.")] bool enabled)
    {
        var config = _settings.Current;
        var rule = config.CaptureRules.FirstOrDefault(r => string.Equals(r.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (rule is null)
            return $"No capture rule '{id}'. Call list_capture_rules for the ids.";

        rule.Enabled = enabled;
        _settings.Save(config);
        return $"Capture rule {rule.Id} {(enabled ? "resumed" : "paused")}.";
    }

    [Description("Delete a capture rule by id, or all of them with 'all'. Captures already taken stay in memory.")]
    private string DeleteCaptureRule(
        [Description("The rule id from list_capture_rules, or 'all'.")] string id)
    {
        var config = _settings.Current;
        var key = id?.Trim() ?? string.Empty;

        int removed;
        if (string.Equals(key, "all", StringComparison.OrdinalIgnoreCase))
        {
            removed = config.CaptureRules.Count;
            config.CaptureRules.Clear();
        }
        else
        {
            removed = config.CaptureRules.RemoveAll(r => string.Equals(r.Id, key, StringComparison.OrdinalIgnoreCase));
        }

        if (removed == 0)
            return $"No capture rule '{id}'. Call list_capture_rules for the ids.";

        _settings.Save(config);
        return removed == 1 ? "Capture rule deleted." : $"Deleted {removed} capture rules.";
    }

    /// <summary>"notepad · when it opens · screenshot + text" — shared with the Settings list.</summary>
    public static string Describe(CaptureRule rule)
    {
        var when = rule.Trigger switch
        {
            CaptureRuleTrigger.OnOpen => "when it opens",
            CaptureRuleTrigger.AfterOpen => $"{FormatDelay(rule.DelaySeconds)} after it opens",
            _ => $"every {Math.Clamp(rule.IntervalMinutes, 1, 1440)} min",
        };
        var what = rule.IncludeScreenshot ? "screenshot + text" : "text only";
        return $"{rule.Match} · {when} · {what}";
    }

    /// <summary>"45 s", "5 min", "1 h 30 min" — a delay in seconds as people would say it.</summary>
    public static string FormatDelay(int seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 86400));
        if (span.TotalMinutes < 1)
            return $"{span.Seconds} s";
        if (span.TotalHours < 1)
            return span.Seconds == 0 ? $"{span.Minutes} min" : $"{span.Minutes} min {span.Seconds} s";
        return span.Minutes == 0 ? $"{(int)span.TotalHours} h" : $"{(int)span.TotalHours} h {span.Minutes} min";
    }

    /// <summary>Short, readable, and unique enough for a handful of rules.</summary>
    public static string NewId() => $"r-{Guid.NewGuid():N}"[..6];
}
