using System.Diagnostics;
using System.Globalization;
using Cadence.Core.Model;
using Cadence.Core.Scheduling;
using Cadence.TestHarness;

namespace Cadence.Tests;

public static class NextRunsTests
{
    static readonly TimeZoneInfo Est = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
    static readonly DateTimeOffset Base = DateTimeOffset.Parse("2026-09-30T22:00:00-04:00", CultureInfo.InvariantCulture);

    static DateTime Dt(string s) => DateTime.ParseExact(s, "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);
    static DateTimeOffset At(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture);
    static string F(Occurrence o) => TimeZoneInfo.ConvertTime(o.At, Est).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    static string L(IEnumerable<Occurrence> os) => string.Join(", ", os.Select(F));
    static ValueList<TaskTrigger> V(params TaskTrigger[] t) => new(t);

    static MonthlyTrigger Monthly(string start, int[] days, bool last, MonthSet months = MonthSet.All) =>
        new(new ValueList<int>(days), last, months) { StartBoundary = Dt(start) };

    static void Check(string name, string expected, IEnumerable<Occurrence> actual) => T.Eq(name, expected, L(actual));

    // ---- verified 2.3 cases -------------------------------------------------------------------

    public static void WeeklyMonToFri()
    {
        var tr = new WeeklyTrigger(1, WeekDays.Weekdays) { StartBoundary = Dt("2026-09-01T08:00") };
        Check("weekdays 08:00", "2026-10-01 08:00, 2026-10-02 08:00, 2026-10-05 08:00, 2026-10-06 08:00, 2026-10-07 08:00",
            NextRuns.Next(V(tr), Base, 5, Est));
    }

    public static void StrictlyAfter()
    {
        var tr = new DailyTrigger(1) { StartBoundary = Dt("2026-09-01T09:00") };
        Check("after exactly a run excludes it", "2026-10-02 09:00", NextRuns.Next(V(tr), At("2026-10-01T09:00:00-04:00"), 1, Est));
        Check("one tick before includes it", "2026-10-01 09:00", NextRuns.Next(V(tr), At("2026-10-01T09:00:00-04:00").AddTicks(-1), 1, Est));
    }

    public static void DailyRepetition15Min()
    {
        var tr = new DailyTrigger(1)
        {
            StartBoundary = Dt("2026-09-01T22:00"),
            Repetition = new Repetition(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(23 * 60 + 45), false),
        };
        Check("22:15 ... 23:00", "2026-09-30 22:15, 2026-09-30 22:30, 2026-09-30 22:45, 2026-09-30 23:00",
            NextRuns.Next(V(tr), At("2026-09-30T22:07:00-04:00"), 4, Est));
    }

    public static void BiweeklyMonday()
    {
        var tr = new WeeklyTrigger(2, WeekDays.Monday) { StartBoundary = Dt("2026-09-07T09:00") };
        Check("biweekly Mon", "2026-10-05 09:00, 2026-10-19 09:00", NextRuns.Next(V(tr), Base, 2, Est));
    }

    public static void BiweeklySunMonSatAnchoredOnSundayWeek()
    {
        var tr = new WeeklyTrigger(2, WeekDays.Sunday | WeekDays.Monday | WeekDays.Saturday) { StartBoundary = Dt("2026-09-02T09:00") };
        Check("Sun/Mon/Sat every 2 weeks from Wed 09-02",
            "2026-09-05 09:00, 2026-09-13 09:00, 2026-09-14 09:00, 2026-09-19 09:00, 2026-09-27 09:00, 2026-09-28 09:00, 2026-10-03 09:00",
            NextRuns.Next(V(tr), At("2026-08-30T00:00:00-04:00"), 7, Est));
    }

    public static void DailyEveryThreeDays()
    {
        var tr = new DailyTrigger(3) { StartBoundary = Dt("2026-09-29T09:00") };
        Check("every 3 days", "2026-10-02 09:00, 2026-10-05 09:00, 2026-10-08 09:00", NextRuns.Next(V(tr), Base, 3, Est));
    }

    public static void MonthlyLastDay()
    {
        var tr = Monthly("2026-09-01T23:00", [], true);
        Check("Last 23:00", "2026-09-30 23:00, 2026-10-31 23:00, 2026-11-30 23:00, 2026-12-31 23:00, 2027-01-31 23:00, 2027-02-28 23:00",
            NextRuns.Next(V(tr), Base, 6, Est));
    }

    public static void MonthlyDay31SkipsShortMonths()
    {
        var tr = Monthly("2026-09-01T09:00", [31], false);
        Check("day 31 skips, not clamps", "2026-10-31 09:00, 2026-12-31 09:00, 2027-01-31 09:00, 2027-03-31 09:00",
            NextRuns.Next(V(tr), Base, 4, Est));
    }

    public static void MonthlyDay30And29SkipFebruary()
    {
        Check("day 30", "2027-01-30 09:00, 2027-03-30 09:00",
            NextRuns.Next(V(Monthly("2026-09-01T09:00", [30], false)), At("2026-12-31T00:00:00-05:00"), 2, Est));
        Check("day 29 in 2027 skips Feb", "2027-01-29 09:00, 2027-03-29 09:00",
            NextRuns.Next(V(Monthly("2026-09-01T09:00", [29], false)), At("2026-12-31T00:00:00-05:00"), 2, Est));
        Check("day 29 in leap year", "2028-02-29 09:00",
            NextRuns.Next(V(Monthly("2026-09-01T09:00", [29], false)), At("2028-02-01T00:00:00-05:00"), 1, Est));
    }

    public static void MonthlyDay29PlusLastInFebruaryDeduplicates()
    {
        var tr = Monthly("2026-09-01T09:00", [29], true, MonthSet.February);
        Check("Feb 29 + Last", "2027-02-28 09:00, 2028-02-29 09:00", NextRuns.Next(V(tr), Base, 2, Est));
    }

    public static void MonthlyDaysList()
    {
        var tr = Monthly("2026-09-01T09:00", [1, 15], true);
        Check("1, 15, Last", "2026-10-01 09:00, 2026-10-15 09:00, 2026-10-31 09:00, 2026-11-01 09:00, 2026-11-15 09:00",
            NextRuns.Next(V(tr), Base, 5, Est));
    }

    public static void MonthlyMonthRestriction()
    {
        var tr = Monthly("2026-09-01T09:00", [1], false, MonthSet.January | MonthSet.July);
        Check("only Jan and Jul", "2027-01-01 09:00, 2027-07-01 09:00, 2028-01-01 09:00", NextRuns.Next(V(tr), Base, 3, Est));
    }

    public static void MonthlyDowLastFriday()
    {
        var tr = new MonthlyDowTrigger(ValueList.Of(WeekOfMonth.Last), WeekDays.Friday, MonthSet.All) { StartBoundary = Dt("2026-09-01T09:00") };
        Check("last Friday", "2026-10-30 09:00, 2026-11-27 09:00, 2026-12-25 09:00", NextRuns.Next(V(tr), Base, 3, Est));
    }

    public static void MonthlyDowWeeksFourAndLast()
    {
        var tr = new MonthlyDowTrigger(ValueList.Of(WeekOfMonth.Fourth, WeekOfMonth.Last), WeekDays.Monday,
            MonthSet.October | MonthSet.November | MonthSet.December) { StartBoundary = Dt("2026-09-01T09:00") };
        Check("weeks 4 and Last Monday Oct-Dec", "2026-10-26 09:00, 2026-11-23 09:00, 2026-11-30 09:00, 2026-12-28 09:00",
            NextRuns.Next(V(tr), Base, 4, Est));
    }

    public static void MonthlyDowFirstTuesdayAndSecondWednesday()
    {
        var first = new MonthlyDowTrigger(ValueList.Of(WeekOfMonth.First), WeekDays.Tuesday, MonthSet.All) { StartBoundary = Dt("2026-09-01T09:00") };
        Check("first Tuesday", "2026-10-06 09:00, 2026-11-03 09:00, 2026-12-01 09:00", NextRuns.Next(V(first), Base, 3, Est));
        var second = new MonthlyDowTrigger(ValueList.Of(WeekOfMonth.Second), WeekDays.Wednesday, MonthSet.All) { StartBoundary = Dt("2026-09-01T09:00") };
        Check("second Wednesday", "2026-10-14 09:00, 2026-11-11 09:00", NextRuns.Next(V(second), Base, 2, Est));
    }

    public static void WeeklyWeekdaysWithHourlyWindow()
    {
        var tr = new WeeklyTrigger(1, WeekDays.Weekdays)
        {
            StartBoundary = Dt("2026-09-01T09:00"),
            Repetition = new Repetition(TimeSpan.FromHours(1), TimeSpan.FromHours(8), false),
        };
        var runs = NextRuns.Next(V(tr), Base, 10, Est);
        Check("09:00..17:00 inclusive (9 runs) then next day",
            "2026-10-01 09:00, 2026-10-01 10:00, 2026-10-01 11:00, 2026-10-01 12:00, 2026-10-01 13:00, 2026-10-01 14:00, 2026-10-01 15:00, 2026-10-01 16:00, 2026-10-01 17:00, 2026-10-02 09:00",
            runs);
    }

    public static void WindowCrossesMidnight()
    {
        var tr = new WeeklyTrigger(1, WeekDays.Friday)
        {
            StartBoundary = Dt("2026-09-01T22:00"),
            Repetition = new Repetition(TimeSpan.FromHours(1), TimeSpan.FromHours(4), false),
        };
        Check("Fri 22:00 PT1H/PT4H", "2026-10-02 22:00, 2026-10-02 23:00, 2026-10-03 00:00, 2026-10-03 01:00, 2026-10-03 02:00, 2026-10-09 22:00",
            NextRuns.Next(V(tr), Base, 6, Est));
        Check("started in a window that began yesterday", "2026-10-03 00:00, 2026-10-03 01:00, 2026-10-03 02:00, 2026-10-09 22:00",
            NextRuns.Next(V(tr), At("2026-10-03T00:00:00-04:00").AddMinutes(-5), 4, Est));
    }

    public static void TimeTriggerRepetitionEndIsExclusive()
    {
        var tr = new TimeTrigger
        {
            StartBoundary = Dt("2026-09-30T22:50"),
            Repetition = new Repetition(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), false),
        };
        Check("[time] PT1M/PT2M gives 22:50, 22:51 only", "2026-09-30 22:50, 2026-09-30 22:51", NextRuns.Next(V(tr), Base.AddHours(-1), 10, Est));
        var tr3 = tr with { Repetition = new Repetition(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(3), false) };
        T.Eq("[time] PT1M/PT3M gives 3 runs", 3, NextRuns.Next(V(tr3), Base.AddHours(-1), 10, Est).Count);
    }

    public static void CalendarRepetitionEndIsInclusive()
    {
        var tr = new DailyTrigger(1)
        {
            StartBoundary = Dt("2026-09-30T22:50"),
            Repetition = new Repetition(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), false),
        };
        Check("[daily] PT1M/PT2M gives 22:50, 22:51, 22:52", "2026-09-30 22:50, 2026-09-30 22:51, 2026-09-30 22:52, 2026-10-01 22:50",
            NextRuns.Next(V(tr), Base.AddHours(-1), 4, Est));
    }

    public static void TimeTriggerIndefiniteRepetitionJumps()
    {
        var tr = new TimeTrigger
        {
            StartBoundary = Dt("2026-09-01T00:00"),
            Repetition = new Repetition(TimeSpan.FromMinutes(90), null, false),
        };
        // 2026-10-01 00:00 EDT is 30 days = 720 h after the start; 720 h / 1.5 h = 480 exactly.
        Check("indefinite 90 min", "2026-10-01 00:00, 2026-10-01 01:30, 2026-10-01 03:00",
            NextRuns.Next(V(tr), At("2026-09-30T23:59:00-04:00"), 3, Est));
    }

    public static void TimeTriggerWithoutRepetitionRunsOnce()
    {
        var tr = new TimeTrigger { StartBoundary = Dt("2026-10-05T10:00") };
        Check("once", "2026-10-05 10:00", NextRuns.Next(V(tr), Base, 5, Est));
        Check("none after it", "", NextRuns.Next(V(tr), At("2026-10-05T10:00:00-04:00"), 5, Est));
    }

    public static void EndBoundaryIsInclusive()
    {
        var tr = new DailyTrigger(1) { StartBoundary = Dt("2026-09-01T09:00"), EndBoundary = Dt("2026-10-03T09:00") };
        Check("last run 10-03 09:00", "2026-10-01 09:00, 2026-10-02 09:00, 2026-10-03 09:00", NextRuns.Next(V(tr), Base, 10, Est));
        var before = tr with { EndBoundary = Dt("2026-10-03T08:59") };
        Check("one minute earlier excludes 10-03", "2026-10-01 09:00, 2026-10-02 09:00", NextRuns.Next(V(before), Base, 10, Est));
    }

    public static void EndBoundaryCutsRepetition()
    {
        var tr = new TimeTrigger
        {
            StartBoundary = Dt("2026-10-01T09:00"),
            EndBoundary = Dt("2026-10-01T09:30"),
            Repetition = new Repetition(TimeSpan.FromMinutes(10), null, false),
        };
        Check("09:00 .. 09:30", "2026-10-01 09:00, 2026-10-01 09:10, 2026-10-01 09:20, 2026-10-01 09:30", NextRuns.Next(V(tr), Base, 10, Est));
    }

    public static void StartBoundaryInTheFutureAndPast()
    {
        var future = new DailyTrigger(1) { StartBoundary = Dt("2026-10-10T09:00") };
        Check("no runs before the StartBoundary", "2026-10-10 09:00, 2026-10-11 09:00", NextRuns.Next(V(future), Base, 2, Est));
        var sameDayLater = new DailyTrigger(1) { StartBoundary = Dt("2026-10-01T09:00") };
        Check("starts the same day it is created", "2026-10-01 09:00", NextRuns.Next(V(sameDayLater), Base, 1, Est));
    }

    public static void SevenMinuteRepetitionRealignsEachDay()
    {
        var tr = new DailyTrigger(1)
        {
            StartBoundary = Dt("2026-09-01T08:15"),
            Repetition = new Repetition(TimeSpan.FromMinutes(7), TimeSpan.FromDays(1), false),
        };
        Check("...08:03, 08:10, 08:15 (once), 08:22", "2026-10-01 08:03, 2026-10-01 08:10, 2026-10-01 08:15, 2026-10-01 08:22",
            NextRuns.Next(V(tr), At("2026-10-01T07:56:00-04:00"), 4, Est));
    }

    public static void IndefiniteCalendarRepetitionStopsAtNextBase()
    {
        var tr = new DailyTrigger(1)
        {
            StartBoundary = Dt("2026-09-01T08:00"),
            Repetition = new Repetition(TimeSpan.FromHours(10), null, false),
        };
        Check("18:00 + 10 h = 04:00 until the next 08:00 base", "2026-10-01 04:00, 2026-10-01 08:00, 2026-10-01 18:00, 2026-10-02 04:00",
            NextRuns.Next(V(tr), Base, 4, Est));
        var weekly = new WeeklyTrigger(1, WeekDays.Monday)
        {
            StartBoundary = Dt("2026-09-01T08:00"),
            Repetition = new Repetition(TimeSpan.FromHours(12), null, false),
        };
        Check("weekly indefinite continues until the next Monday", "2026-10-02 08:00, 2026-10-02 20:00",
            NextRuns.Next(V(weekly), At("2026-10-02T07:00:00-04:00"), 2, Est));
    }

    public static void DuplicateInstantsAreDeduplicated()
    {
        var a = new DailyTrigger(1) { StartBoundary = Dt("2026-09-01T09:00") };
        var b = new WeeklyTrigger(1, WeekDays.All) { StartBoundary = Dt("2026-09-01T09:00") };
        var runs = NextRuns.Next(V(a, b), Base, 3, Est);
        Check("identical triggers", "2026-10-01 09:00, 2026-10-02 09:00, 2026-10-03 09:00", runs);
        T.True("lowest trigger index kept", runs.All(o => o.TriggerIndex == 0));
        var runsBA = NextRuns.Next(V(b, a), Base, 1, Est);
        T.Eq("index follows list order", 0, runsBA[0].TriggerIndex);
    }

    public static void MultipleTriggersMergeInTimeOrder()
    {
        var a = new DailyTrigger(1) { StartBoundary = Dt("2026-09-01T09:00") };
        var b = new DailyTrigger(1) { StartBoundary = Dt("2026-09-01T07:30") };
        var runs = NextRuns.Next(V(a, b), Base, 4, Est);
        Check("merged", "2026-10-01 07:30, 2026-10-01 09:00, 2026-10-02 07:30, 2026-10-02 09:00", runs);
        T.Eq("second trigger index", 1, runs[0].TriggerIndex);
    }

    public static void DisabledLogonAndUnsupportedYieldNothing()
    {
        var disabled = new DailyTrigger(1) { StartBoundary = Dt("2026-09-01T09:00"), Enabled = false };
        T.Eq("disabled", 0, NextRuns.Next(V(disabled), Base, 5, Est).Count);
        T.Eq("logon", 0, NextRuns.Next(V(new LogonTrigger("TESTPC\\tester", null) { StartBoundary = Dt("2026-09-01T09:00") }), Base, 5, Est).Count);
        T.Eq("unsupported", 0, NextRuns.Next(V(new UnsupportedTrigger("BootTrigger", "<BootTrigger/>") { StartBoundary = Dt("2026-09-01T09:00") }), Base, 5, Est).Count);
        var mixed = NextRuns.Next(V(disabled, new DailyTrigger(1) { StartBoundary = Dt("2026-09-01T10:00") }), Base, 1, Est);
        T.Eq("trigger index skips disabled", 1, mixed[0].TriggerIndex);
        T.Eq("empty trigger list", 0, NextRuns.Next(ValueList<TaskTrigger>.Empty, Base, 5, Est).Count);
    }

    public static void CountAndHorizon()
    {
        var tr = new DailyTrigger(1) { StartBoundary = Dt("2026-09-01T09:00") };
        T.Eq("count 0", 0, NextRuns.Next(V(tr), Base, 0, Est).Count);
        T.Eq("count 3", 3, NextRuns.Next(V(tr), Base, 3, Est).Count);
        T.Eq("horizon of 3 days from 09-30 reaches 10-03", 3, NextRuns.Next(V(tr), Base, 50, Est, 3).Count);
        T.Eq("horizon 0 covers only the rest of today", 0, NextRuns.Next(V(tr), Base, 50, Est, 0).Count);
        var yearly = Monthly("2026-09-01T09:00", [31], false, MonthSet.February);
        T.Eq("never fires: Feb 31", 0, NextRuns.Next(V(yearly), Base, 5, Est).Count);
    }

    public static void RandomDelayIsReported()
    {
        var tr = new DailyTrigger(1) { StartBoundary = Dt("2026-09-01T09:00"), RandomDelay = TimeSpan.FromMinutes(10) };
        T.Eq("RandomDelayUpTo", TimeSpan.FromMinutes(10), NextRuns.Next(V(tr), Base, 1, Est)[0].RandomDelayUpTo);
        T.Eq("zero without delay", TimeSpan.Zero, NextRuns.Next(V(tr with { RandomDelay = null }), Base, 1, Est)[0].RandomDelayUpTo);
    }

    public static void AfterInAnotherOffsetAndOtherZones()
    {
        var tr = new DailyTrigger(1) { StartBoundary = Dt("2026-09-01T09:00") };
        // 2026-10-01T13:00Z is 09:00 EDT exactly, so the next run is the following day.
        Check("UTC input", "2026-10-02 09:00", NextRuns.Next(V(tr), At("2026-10-01T13:00:00Z"), 1, Est));
        Check("UTC input, a tick earlier", "2026-10-01 09:00", NextRuns.Next(V(tr), At("2026-10-01T13:00:00Z").AddTicks(-1), 1, Est));
        var runs = NextRuns.Next(V(tr), Base, 1, TimeZoneInfo.Utc);
        T.Eq("UTC zone offset", TimeSpan.Zero, runs[0].At.Offset);
        T.Eq("UTC zone time", "2026-10-01 09:00", runs[0].At.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
    }

    // ---- DST policy ([policy], PLAN 3.4 item 6) -----------------------------------------------

    public static void PolicySpringForwardGapShiftsForward()
    {
        var tr = new DailyTrigger(1) { StartBoundary = Dt("2027-03-01T02:30") };
        var runs = NextRuns.Next(V(tr), At("2027-03-13T00:00:00-05:00"), 3, Est);
        Check("[policy] 02:30 on 2027-03-14 becomes 03:30", "2027-03-13 02:30, 2027-03-14 03:30, 2027-03-15 02:30", runs);
        T.Eq("[policy] gap run offset is EDT", TimeSpan.FromHours(-4), runs[1].At.Offset);
        T.True("[policy] gap run DstAdjusted", runs[1].DstAdjusted);
        T.True("[policy] normal run not adjusted (before)", !runs[0].DstAdjusted);
        T.True("[policy] normal run not adjusted (after)", !runs[2].DstAdjusted);
        T.Eq("[policy] 03:30 EDT is 07:30Z", At("2027-03-14T07:30:00Z"), runs[1].At);
    }

    public static void PolicyFallBackAmbiguousRunsOnce()
    {
        var tr = new DailyTrigger(1) { StartBoundary = Dt("2026-10-01T01:30") };
        var runs = NextRuns.Next(V(tr), At("2026-10-30T12:00:00-04:00"), 4, Est);
        Check("[policy] 01:30 on 2026-11-01 once", "2026-10-31 01:30, 2026-11-01 01:30, 2026-11-02 01:30, 2026-11-03 01:30", runs);
        T.Eq("[policy] ambiguous run uses the first (EDT) offset", TimeSpan.FromHours(-4), runs[1].At.Offset);
        T.Eq("[policy] 01:30 EDT is 05:30Z", At("2026-11-01T05:30:00Z"), runs[1].At);
        T.Eq("[policy] next day is EST", TimeSpan.FromHours(-5), runs[2].At.Offset);
        T.True("[policy] ambiguous run is not flagged adjusted", !runs[1].DstAdjusted);
    }

    public static void PolicyHourlyWindowIsElapsedTimeAcrossSpringForward()
    {
        var tr = new DailyTrigger(1)
        {
            StartBoundary = Dt("2027-03-01T00:00"),
            Repetition = new Repetition(TimeSpan.FromHours(1), TimeSpan.FromHours(5), false),
        };
        var runs = NextRuns.Next(V(tr), At("2027-03-13T23:00:00-05:00"), 6, Est);
        Check("[policy] 00:00, 01:00, 03:00, 04:00, 05:00, 06:00", "2027-03-14 00:00, 2027-03-14 01:00, 2027-03-14 03:00, 2027-03-14 04:00, 2027-03-14 05:00, 2027-03-14 06:00", runs);
        T.True("[policy] repetition offsets are never flagged adjusted", runs.All(o => !o.DstAdjusted));
    }

    public static void PolicyHourlyWindowAcrossFallBack()
    {
        var tr = new DailyTrigger(1)
        {
            StartBoundary = Dt("2026-10-01T00:00"),
            Repetition = new Repetition(TimeSpan.FromHours(1), TimeSpan.FromHours(3), false),
        };
        var runs = NextRuns.Next(V(tr), At("2026-10-31T23:00:00-04:00"), 4, Est);
        Check("[policy] 00:00, 01:00 EDT, 01:00 EST, 02:00 EST", "2026-11-01 00:00, 2026-11-01 01:00, 2026-11-01 01:00, 2026-11-01 02:00", runs);
        T.Eq("[policy] second 01:00 is EST", TimeSpan.FromHours(-5), runs[2].At.Offset);
    }

    public static void PolicyTimeTriggerInGap()
    {
        var tr = new TimeTrigger { StartBoundary = Dt("2027-03-14T02:15") };
        var runs = NextRuns.Next(V(tr), At("2027-03-14T00:00:00-05:00"), 1, Est);
        Check("[policy] one-off in the gap", "2027-03-14 03:15", runs);
        T.True("[policy] flagged", runs[0].DstAdjusted);
    }

    public static void WallClockResolve()
    {
        var gap = WallClock.Resolve(Dt("2027-03-14T02:00"), Est);
        T.Eq("gap start shifts to 03:00 EDT", At("2027-03-14T03:00:00-04:00"), gap.At);
        T.True("gap adjusted", gap.Adjusted);
        var edge = WallClock.Resolve(Dt("2027-03-14T03:00"), Est);
        T.True("03:00 is valid", !edge.Adjusted);
        T.Eq("01:59 EST is valid", TimeSpan.FromHours(-5), WallClock.Resolve(Dt("2027-03-14T01:59"), Est).At.Offset);
        var amb = WallClock.Resolve(Dt("2026-11-01T01:00"), Est);
        T.Eq("ambiguous takes daylight offset", TimeSpan.FromHours(-4), amb.At.Offset);
        T.True("ambiguous not adjusted", !amb.Adjusted);
        T.Eq("after the overlap is EST", TimeSpan.FromHours(-5), WallClock.Resolve(Dt("2026-11-01T02:00"), Est).At.Offset);
        T.Eq("UTC never adjusts", false, WallClock.Resolve(Dt("2027-03-14T02:30"), TimeZoneInfo.Utc).Adjusted);
    }

    // ---- holiday marking ---------------------------------------------------------------------

    public static void HolidayMarkingThanksgivingNyse()
    {
        var tr = new WeeklyTrigger(1, WeekDays.Weekdays) { StartBoundary = Dt("2026-11-01T18:00") };
        var runs = NextRuns.Next(V(tr), HolidayCalendar.Nyse, At("2026-11-20T23:00:00-05:00"), 5, Est);
        Check("dates", "2026-11-23 18:00, 2026-11-24 18:00, 2026-11-25 18:00, 2026-11-26 18:00, 2026-11-27 18:00", runs);
        T.Eq("Thanksgiving marked", "Thanksgiving Day", runs[3].SkippedHoliday);
        T.True("others not marked", runs.Where((_, i) => i != 3).All(o => o.SkippedHoliday is null));
    }

    public static void HolidayMarkingFederalAndNone()
    {
        var tr = new WeeklyTrigger(1, WeekDays.Weekdays) { StartBoundary = Dt("2026-10-01T09:00") };
        var fed = NextRuns.Next(V(tr), HolidayCalendar.UsFederal, At("2026-10-08T00:00:00-04:00"), 5, Est);
        T.Eq("federal: Columbus Day marked", "Columbus Day", fed.First(o => F(o) == "2026-10-12 09:00").SkippedHoliday);
        var nyse = NextRuns.Next(V(tr), HolidayCalendar.Nyse, At("2026-10-08T00:00:00-04:00"), 5, Est);
        T.True("nyse: Columbus Day open", nyse.All(o => o.SkippedHoliday is null));
        var none = NextRuns.Next(V(tr), HolidayCalendar.None, At("2026-10-08T00:00:00-04:00"), 5, Est);
        T.True("None marks nothing", none.All(o => o.SkippedHoliday is null));
        T.Eq("marked occurrences still count toward count", 5, fed.Count);
    }

    public static void HolidayMarkingUsesLocalDate()
    {
        // 23:30 EST on Thanksgiving is 04:30Z on Friday; the local date decides.
        var tr = new DailyTrigger(1) { StartBoundary = Dt("2026-11-01T23:30") };
        var runs = NextRuns.Next(V(tr), HolidayCalendar.Nyse, At("2026-11-26T12:00:00-05:00"), 2, Est);
        T.Eq("Thanksgiving 23:30 marked", "Thanksgiving Day", runs[0].SkippedHoliday);
        T.True("Friday not marked", runs[1].SkippedHoliday is null);
    }

    // ---- Between -----------------------------------------------------------------------------

    public static void BetweenIsInclusiveOnBothEnds()
    {
        var tr = new DailyTrigger(1) { StartBoundary = Dt("2026-09-01T09:00") };
        var runs = NextRuns.Between(V(tr), At("2026-10-01T09:00:00-04:00"), At("2026-10-03T09:00:00-04:00"), Est);
        Check("both ends included", "2026-10-01 09:00, 2026-10-02 09:00, 2026-10-03 09:00", runs);
        Check("tick inside excludes", "2026-10-02 09:00", NextRuns.Between(V(tr), At("2026-10-01T09:00:00-04:00").AddTicks(1), At("2026-10-03T09:00:00-04:00").AddTicks(-1), Est));
        T.Eq("max respected", 2, NextRuns.Between(V(tr), At("2026-10-01T00:00:00-04:00"), At("2026-10-31T00:00:00-04:00"), Est, 2).Count);
        T.Eq("inverted range is empty", 0, NextRuns.Between(V(tr), At("2026-10-03T00:00:00-04:00"), At("2026-10-01T00:00:00-04:00"), Est).Count);
        T.Eq("a month of daily runs", 31, NextRuns.Between(V(tr), At("2026-10-01T00:00:00-04:00"), At("2026-10-31T23:59:00-04:00"), Est).Count);
    }

    public static void BetweenMinuteRepetitionCount()
    {
        var tr = new TimeTrigger
        {
            StartBoundary = Dt("2026-10-01T00:00"),
            Repetition = new Repetition(TimeSpan.FromMinutes(1), null, false),
        };
        T.Eq("one day of minutes", 1440, NextRuns.Between(V(tr), At("2026-10-02T00:00:00-04:00"), At("2026-10-02T23:59:00-04:00"), Est).Count);
    }

    // ---- performance -------------------------------------------------------------------------

    static double TimeNext(ValueList<TaskTrigger> triggers)
    {
        NextRuns.Next(triggers, Base, 50, Est);   // warm-up (JIT)
        var sw = Stopwatch.StartNew();
        var runs = NextRuns.Next(triggers, Base, 50, Est);
        sw.Stop();
        T.Eq("returned 50 runs", 50, runs.Count);
        return sw.Elapsed.TotalMilliseconds;
    }

    public static void PerformanceOneMinuteRepetition()
    {
        var timeTrigger = new TimeTrigger
        {
            StartBoundary = Dt("2026-09-01T00:00"),
            Repetition = new Repetition(TimeSpan.FromMinutes(1), null, false),
        };
        double ms = TimeNext(V(timeTrigger));
        Console.WriteLine($"INFO Next(50) TimeTrigger 1-minute repetition: {ms.ToString("F2", CultureInfo.InvariantCulture)} ms");
        T.True("TimeTrigger 1-minute Next(50) < 20 ms", ms < 20, $"{ms} ms");

        var daily = new DailyTrigger(1)
        {
            StartBoundary = Dt("2026-09-01T00:00"),
            Repetition = new Repetition(TimeSpan.FromMinutes(1), null, false),
        };
        ms = TimeNext(V(daily));
        Console.WriteLine($"INFO Next(50) daily trigger 1-minute indefinite repetition: {ms.ToString("F2", CultureInfo.InvariantCulture)} ms");
        T.True("daily 1-minute Next(50) < 20 ms", ms < 20, $"{ms} ms");
    }

    public static void PerformanceMonthlyDay31()
    {
        double ms = TimeNext(V(Monthly("2026-09-01T09:00", [31], false)));
        Console.WriteLine($"INFO Next(50) monthly day 31: {ms.ToString("F2", CultureInfo.InvariantCulture)} ms");
        T.True("monthly day 31 Next(50) < 20 ms", ms < 20, $"{ms} ms");
    }
}
