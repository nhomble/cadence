using Cadence.Core.Model;

namespace Cadence.Core.Text;

/// <summary>A starting point for a new job. Templates only produce cron and a label; a job remembers just its cron.</summary>
public sealed record Template(string Id, string Title, string Subtitle, string Cron, string SuggestedName, HolidayCalendar SuggestedSkip);

public static class Templates
{
    /// <summary>The nine templates in display order. Ids are stable.</summary>
    public static IReadOnlyList<Template> All { get; } =
    [
        new("good-morning", "Good morning", "Every weekday at 8:00 - open your apps, sync your notes",
            "0 8 * * 1-5", "Good morning", HolidayCalendar.None),
        new("end-of-day", "End of day", "Every weekday at 17:30 - back up, tidy up",
            "30 17 * * 1-5", "End of day", HolidayCalendar.None),
        new("weekdays-6pm", "Weekdays at 6pm", "After the market close - e.g. ingest today's stock prices (tip: skip NYSE holidays)",
            "0 18 * * 1-5", "Weekdays at 6pm", HolidayCalendar.Nyse),
        new("hourly", "On the hour", "Every hour, on the hour",
            "0 * * * *", "Hourly job", HolidayCalendar.None),
        new("every-15-minutes", "Every 15 minutes", "Every 15 minutes, all day",
            "*/15 * * * *", "Every 15 minutes", HolidayCalendar.None),
        new("nightly-2am", "Nightly at 2am", "Every day at 2:00 - e.g. download your YouTube feed",
            "0 2 * * *", "Nightly job", HolidayCalendar.None),
        new("weekly-monday-9am", "Weekly, Monday 9am", "Every Monday at 9:00",
            "0 9 * * 1", "Weekly job", HolidayCalendar.None),
        new("at-login", "At login", "When you sign in",
            "@logon", "At login", HolidayCalendar.None),
        new("blank", "Start from scratch", "Pick your own time",
            "0 9 * * *", "New job", HolidayCalendar.None),
    ];
}
