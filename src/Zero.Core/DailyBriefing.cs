using Microsoft.Extensions.Logging;

namespace Zero.Core;

/// <summary>
/// Generates a spoken daily briefing when ZERO starts in the morning (5am–11am).
/// Briefing includes: greeting, day/date, motivational line, and a quick agenda reminder
/// built from the system clock — all fully offline, no API calls.
/// </summary>
public static class DailyBriefing
{
    /// <summary>
    /// Returns a briefing string if today qualifies (morning hours + not already
    /// briefed today). Returns null if briefing should be skipped.
    /// </summary>
    public static string? TryGenerate(ILogger? log = null)
    {
        var now = DateTime.Now;

        // Only deliver briefing in the morning window (5am–11am)
        if (now.Hour < 5 || now.Hour >= 11) return null;

        // Only once per day — track via a small stamp file
        if (!ShouldDeliver(now)) return null;

        MarkDelivered(now);

        var day        = now.DayOfWeek;
        var dateStr    = now.ToString("dddd, MMMM d");
        var weekNumber = System.Globalization.ISOWeek.GetWeekOfYear(now);

        var opener = day switch
        {
            DayOfWeek.Monday    => "New week, new wins.",
            DayOfWeek.Tuesday   => "Tuesday. We're warmed up. Let's go.",
            DayOfWeek.Wednesday => "Midweek checkpoint. You're halfway there.",
            DayOfWeek.Thursday  => "Thursday. Almost at the finish line.",
            DayOfWeek.Friday    => "Friday! One last push before the weekend.",
            DayOfWeek.Saturday  => "It's Saturday. Rest is productive too.",
            DayOfWeek.Sunday    => "Sunday — great time to plan ahead.",
            _                   => "Another day, another shot at greatness."
        };

        var lines = new List<string>
        {
            $"Good morning, Hasan. Today is {dateStr}, week {weekNumber}.",
            opener,
            "I'm online and all systems are go.",
            "Say 'Hey Jarvis' anytime you need me, or press Ctrl Shift Z for text input.",
        };

        var briefing = string.Join(" ", lines);
        log?.LogInformation("Daily briefing generated for {Date}", dateStr);
        return briefing;
    }

    // ── Delivery stamp ────────────────────────────────────────────────────────

    private static readonly string StampFile =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "ZERO", "briefing.stamp");

    private static bool ShouldDeliver(DateTime now)
    {
        if (!File.Exists(StampFile)) return true;
        try
        {
            var stamp = DateTime.Parse(File.ReadAllText(StampFile).Trim());
            return stamp.Date < now.Date; // new day → deliver
        }
        catch { return true; }
    }

    private static void MarkDelivered(DateTime now)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StampFile)!);
            File.WriteAllText(StampFile, now.ToString("o"));
        }
        catch { /* non-critical */ }
    }
}
