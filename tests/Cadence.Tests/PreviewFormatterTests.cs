using Cadence.Core.Cron;
using Cadence.Core.Model;
using Cadence.Core.Scheduling;
using Cadence.Core.Text;
using Cadence.TestHarness;

namespace Cadence.Tests;

public static class PreviewFormatterTests
{
    static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
    static readonly DateTimeOffset Now = new(2026, 9, 30, 22, 0, 0, TimeSpan.FromHours(-4));
    static readonly TimeSpan Edt = TimeSpan.FromHours(-4);
    static readonly TimeSpan Est = TimeSpan.FromHours(-5);

    static Occurrence Run(DateTimeOffset at, bool dst = false, string? holiday = null, TimeSpan? delay = null) =>
        new(at, 0, dst, delay ?? TimeSpan.Zero, holiday);

    public static void FormatOneTable()
    {
        var rows = new (string Name, Occurrence Run, string Text)[]
        {
            ("plain", Run(new(2026, 10, 1, 8, 0, 0, Edt)), "Thu 1 Oct 8:00"),
            ("evening", Run(new(2026, 10, 5, 18, 0, 0, Edt)), "Mon 5 Oct 18:00"),
            ("minutes padded", Run(new(2026, 10, 2, 9, 5, 0, Edt)), "Fri 2 Oct 9:05"),
            ("midnight", Run(new(2026, 10, 4, 0, 0, 0, Edt)), "Sun 4 Oct 0:00"),
            ("other year", Run(new(2027, 1, 4, 9, 0, 0, Est)), "Mon 4 Jan 2027 9:00"),
            ("clock change", Run(new(2027, 3, 14, 3, 30, 0, Edt), dst: true), "Sun 14 Mar 2027 3:30 (clock change)"),
            ("holiday", Run(new(2026, 11, 26, 18, 0, 0, Est), holiday: "Thanksgiving Day"), "Thu 26 Nov 18:00 - skipped (Thanksgiving Day)"),
            ("random delay", Run(new(2026, 10, 1, 8, 0, 0, Edt), delay: TimeSpan.FromMinutes(10)), "Thu 1 Oct 8:00 (+ up to 10 min)"),
            ("clock change and holiday", Run(new(2027, 3, 14, 3, 30, 0, Edt), dst: true, holiday: "X"), "Sun 14 Mar 2027 3:30 (clock change) - skipped (X)"),
        };
        foreach (var (name, run, text) in rows) T.Eq(name, text, PreviewFormatter.FormatOne(run, Now, Eastern));
    }

    public static void ConvertsToTheGivenZone()
    {
        // 2026-10-01T12:00Z is 8:00 EDT; a UTC-stamped occurrence still prints in the zone's wall clock.
        var run = Run(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
        T.Eq("utc input", "Thu 1 Oct 8:00", PreviewFormatter.FormatOne(run, Now, Eastern));
        // the year test uses now's year in the same zone: 2027-01-01T03:00Z is still 2026 in New York
        var nye = Run(new DateTimeOffset(2027, 1, 1, 3, 0, 0, TimeSpan.Zero));
        T.Eq("year in zone", "Thu 31 Dec 22:00", PreviewFormatter.FormatOne(nye, Now, Eastern));
        var lateNow = new DateTimeOffset(2027, 1, 1, 3, 30, 0, TimeSpan.Zero);
        T.Eq("now in zone", "Thu 31 Dec 22:00", PreviewFormatter.FormatOne(nye, lateNow, Eastern));
    }

    public static void FormatListJoinsWithCommas()
    {
        var runs = new[] { Run(new(2026, 10, 1, 8, 0, 0, Edt)), Run(new(2026, 10, 2, 8, 0, 0, Edt)) };
        T.Eq("two", "Thu 1 Oct 8:00, Fri 2 Oct 8:00", PreviewFormatter.FormatList(runs, Now, Eastern));
        T.Eq("empty", "", PreviewFormatter.FormatList([], Now, Eastern));
    }

    public static void FormatsRealEngineRuns()
    {
        var spec = new ScheduleSpec(ValueList.Of("0 18 * * 1-5"), HolidayCalendar.Nyse, new DateOnly(2026, 9, 30));
        var compiled = CronCompiler.Compile(spec, new MapContext("u"));
        var after = new DateTimeOffset(2026, 11, 20, 23, 0, 0, Est);
        var runs = NextRuns.Next(compiled.Triggers, HolidayCalendar.Nyse, after, 5, Eastern);
        T.Eq("thanksgiving week",
            "Mon 23 Nov 18:00, Tue 24 Nov 18:00, Wed 25 Nov 18:00, Thu 26 Nov 18:00 - skipped (Thanksgiving Day), Fri 27 Nov 18:00",
            PreviewFormatter.FormatList(runs, after, Eastern));

        var gap = CronCompiler.Compile(new ScheduleSpec(ValueList.Of("30 2 * * *"), HolidayCalendar.None, new DateOnly(2026, 9, 30)), new MapContext("u"));
        var afterGap = new DateTimeOffset(2027, 3, 13, 12, 0, 0, Est);
        var gapRuns = NextRuns.Next(gap.Triggers, afterGap, 2, Eastern);
        T.Eq("spring forward", "Sun 14 Mar 3:30 (clock change), Mon 15 Mar 2:30",
            PreviewFormatter.FormatList(gapRuns, afterGap, Eastern));
    }
}
