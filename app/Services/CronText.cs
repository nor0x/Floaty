using System.Globalization;
using Cronos;

namespace Floaty.Services;

/// <summary>
/// Cron helpers for recurring jobs: validation and next-occurrence math (both Cronos, in the local
/// time zone, so "0 7 * * *" means 07:00 wherever the user is and survives DST), plus the English
/// rendering shown in Settings and returned by the job tools - the spec's <c>describeCron</c>.
/// </summary>
/// <remarks>Pure apart from Cronos, so it can be exercised in isolation.</remarks>
public static class CronText
{
    private static readonly string[] DayNames =
        ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

    private static readonly string[] DayAbbreviations = ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];

    /// <summary>
    /// Parses a standard 5-field expression (minute hour day-of-month month day-of-week) or a Cronos
    /// macro such as <c>@daily</c>. <paramref name="error"/> is a sentence worth showing the user.
    /// </summary>
    public static bool TryParse(string? expression, out CronExpression? cron, out string? error)
    {
        cron = null;
        error = null;

        var text = expression?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            error = "A schedule is required, e.g. \"0 7 * * *\" for every day at 07:00.";
            return false;
        }

        if (!text.StartsWith('@'))
        {
            var fields = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            if (fields != 5)
            {
                error = $"\"{text}\" has {fields} field(s); a schedule needs 5: minute hour day-of-month month day-of-week.";
                return false;
            }
        }

        try
        {
            cron = CronExpression.Parse(text, CronFormat.Standard);
            return true;
        }
        catch (CronFormatException ex)
        {
            error = $"\"{text}\" is not a valid schedule: {ex.Message}";
            return false;
        }
    }

    /// <summary>The first firing strictly after <paramref name="after"/>, in local time; null if it never fires again.</summary>
    public static DateTimeOffset? Next(CronExpression cron, DateTimeOffset after) =>
        cron.GetNextOccurrence(after, TimeZoneInfo.Local);

    /// <summary>The next <paramref name="count"/> firings after <paramref name="after"/>.</summary>
    public static IReadOnlyList<DateTimeOffset> NextFew(CronExpression cron, DateTimeOffset after, int count)
    {
        var result = new List<DateTimeOffset>(count);
        var cursor = after;
        while (result.Count < count && Next(cron, cursor) is { } next)
        {
            result.Add(next);
            cursor = next;
        }

        return result;
    }

    /// <summary>
    /// "Every day at 07:00", "Weekdays at 17:00", "Every 4 hours" - the common shapes in plain English,
    /// and the expression itself for anything more exotic rather than a wrong guess.
    /// </summary>
    public static string Describe(string? expression)
    {
        var text = expression?.Trim() ?? string.Empty;
        switch (text.ToLowerInvariant())
        {
            case "":
                return "No schedule";
            case "@every_minute":
                return "Every minute";
            case "@hourly":
                return "Every hour on the hour";
            case "@daily" or "@midnight":
                return "Every day at 00:00";
            case "@weekly":
                return "Every Sunday at 00:00";
            case "@monthly":
                return "On the 1st of every month at 00:00";
            case "@yearly" or "@annually":
                return "Every 1 January at 00:00";
        }

        var f = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (f.Length != 5)
            return text;

        var (minute, hour, dom, month, dow) = (f[0], f[1], f[2], f[3], f[4]);
        var everyDay = dom == "*" && month == "*" && dow == "*";

        if (everyDay)
        {
            if (minute == "*" && hour == "*")
                return "Every minute";
            if (Step(minute) is { } m && hour == "*")
                return m == 1 ? "Every minute" : $"Every {m} minutes";
            if (Int(minute) is { } atMinute && hour == "*")
                return atMinute == 0 ? "Every hour on the hour" : $"Every hour at :{atMinute:00}";
            if (Int(minute) is { } stepMinute && Step(hour) is { } h)
                return (h == 1 ? "Every hour" : $"Every {h} hours") + (stepMinute == 0 ? string.Empty : $" at :{stepMinute:00}");
        }

        // Everything below fires at fixed clock times.
        if (Times(minute, hour) is not { } times)
            return text;

        if (everyDay)
            return $"Every day at {times}";

        if (month != "*")
            return text;

        if (dom == "*" && Days(dow) is { } days)
        {
            if (days.SetEquals([1, 2, 3, 4, 5]))
                return $"Weekdays at {times}";
            if (days.SetEquals([0, 6]))
                return $"Weekends at {times}";
            if (days.Count == 7)
                return $"Every day at {times}";
            return $"Every {JoinWords(days.OrderBy(d => d == 0 ? 7 : d).Select(d => DayNames[d]).ToList())} at {times}";
        }

        if (dow == "*" && Int(dom) is { } day and >= 1 and <= 31)
            return $"On the {Ordinal(day)} of every month at {times}";

        return text;
    }

    // "*/N" → N.
    private static int? Step(string field) =>
        field.StartsWith("*/", StringComparison.Ordinal) && Int(field[2..]) is { } n and > 0 ? n : null;

    private static int? Int(string field) =>
        int.TryParse(field, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

    // A fixed minute and one or more fixed hours → "07:00" / "09:00 and 17:00".
    private static string? Times(string minute, string hour)
    {
        if (Int(minute) is not { } m || m > 59)
            return null;

        var hours = new List<int>();
        foreach (var part in hour.Split(','))
        {
            if (Int(part) is not { } h || h > 23)
                return null;
            hours.Add(h);
        }

        return JoinWords(hours.Order().Select(h => $"{h:00}:{m:00}").ToList());
    }

    // "1-5", "MON,WED", "0,6" → day numbers 0 (Sunday) .. 6. Null for steps or anything unexpected.
    private static HashSet<int>? Days(string field)
    {
        var days = new HashSet<int>();
        foreach (var part in field.Split(','))
        {
            var range = part.Split('-');
            if (range.Length is < 1 or > 2 || part.Contains('/') || part.Contains('#'))
                return null;

            if (Day(range[0]) is not { } start)
                return null;

            var end = start;
            if (range.Length == 2)
            {
                if (Day(range[1]) is not { } e)
                    return null;
                end = e;
            }

            if (end < start)
                return null;

            for (var d = start; d <= end; d++)
                days.Add(d % 7);
        }

        return days;
    }

    private static int? Day(string token)
    {
        if (Int(token) is { } n)
            return n <= 7 ? n : null;

        var index = Array.FindIndex(DayAbbreviations, a => string.Equals(a, token, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : null;
    }

    private static string JoinWords(IReadOnlyList<string> words) => words.Count switch
    {
        0 => string.Empty,
        1 => words[0],
        _ => string.Join(", ", words.Take(words.Count - 1)) + " and " + words[^1],
    };

    private static string Ordinal(int n) => n + (n % 100 is 11 or 12 or 13 ? "th" : (n % 10) switch
    {
        1 => "st",
        2 => "nd",
        3 => "rd",
        _ => "th",
    });
}
