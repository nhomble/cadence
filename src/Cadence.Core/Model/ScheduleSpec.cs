namespace Cadence.Core.Model;

public sealed record ScheduleSpec(
    ValueList<string> CronLines,              // 1..10 lines; the union of all lines is the schedule (like crontab lines)
    HolidayCalendar SkipHolidays,             // occurrences on these holidays are skipped at run time (3.7)
    DateOnly Anchor);                         // date of first save; used as the StartBoundary date of every trigger
