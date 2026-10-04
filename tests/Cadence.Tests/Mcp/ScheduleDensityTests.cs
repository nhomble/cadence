using Cadence.Core.Cron;
using Cadence.Core.Model;
using Cadence.Core.Scheduling;
using Cadence.TestHarness;

namespace Cadence.Tests.Mcp;

/// <summary>PLAN 12.4.5: the smallest gap between two runs, from a fixed time (never today's date).</summary>
public static class ScheduleDensityTests
{
    static readonly DateTimeOffset From = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    static ValueList<TaskTrigger> Triggers(params string[] cron)
    {
        CompiledSchedule c = CronCompiler.Compile(new ScheduleSpec(new ValueList<string>(cron), HolidayCalendar.None, new DateOnly(2026, 10, 1)), new MapContext("HOST\\user"));
        if (!c.Success) throw new InvalidOperationException("sample does not compile: " + string.Join("; ", c.Diagnostics.Select(d => d.Message)));
        return c.Triggers;
    }

    static double? Minutes(params string[] cron) => ScheduleDensity.MinimumGap(Triggers(cron), From, TimeZoneInfo.Utc)?.TotalMinutes;

    public static void TheTableOfThePlan()
    {
        T.Eq("every minute", 1.0, Minutes("* * * * *") ?? -1);
        T.Eq("every 15 minutes", 15.0, Minutes("*/15 * * * *") ?? -1);
        T.Eq("7:00 and 19:00", 720.0, Minutes("0 7,19 * * *") ?? -1);
        T.Eq("9:00 and 9:01", 1.0, Minutes("0,1 9 * * *") ?? -1);
    }

    public static void MoreShapes()
    {
        T.Eq("hourly", 60.0, Minutes("@hourly") ?? -1);
        T.Eq("daily", 1440.0, Minutes("0 0 * * *") ?? -1);
        T.Eq("weekdays: Friday to Monday is longer, so the smallest gap is a day", 1440.0, Minutes("0 18 * * 1-5") ?? -1);
        T.Eq("weekly (two Mondays in 14 days)", 10080.0, Minutes("0 18 * * 1") ?? -1);
        T.Eq("two lines interleave", 30.0, Minutes("0 9 * * *", "30 9 * * *") ?? -1);
        T.Eq("two lines at the same instant count once", 1440.0, Minutes("0 9 * * *", "0 9 * * *") ?? -1);
        T.Eq("every 90 seconds does not exist, every 2 minutes does", 2.0, Minutes("*/2 * * * *") ?? -1);
    }

    public static void FewerThanTwoRunsGiveNull()
    {
        T.True("@logon has no clock time", Minutes("@logon") is null);
        T.True("yearly: none in 14 days", Minutes("0 0 1 1 *") is null);
        T.True("monthly on the 1st: one run in the window", Minutes("0 0 1 * *") is null);
        T.True("no triggers", ScheduleDensity.MinimumGap(ValueList<TaskTrigger>.Empty, From, TimeZoneInfo.Utc) is null);
    }

    public static void DaysWindowCanBeWidened()
    {
        // Monthly on the 1st and 15th, from 1 Oct 09:00: the runs are 15 Oct, 1 Nov, 15 Nov. A 3-day window holds none, a 50-day window holds all three.
        T.True("narrow window: null", ScheduleDensity.MinimumGap(Triggers("0 0 1,15 * *"), From, TimeZoneInfo.Utc, days: 3) is null);
        T.Eq("wide window: the smallest gap is 14 days (1 Nov to 15 Nov)", 14.0 * 1440, ScheduleDensity.MinimumGap(Triggers("0 0 1,15 * *"), From, TimeZoneInfo.Utc, days: 50)?.TotalMinutes ?? -1);
    }
}
