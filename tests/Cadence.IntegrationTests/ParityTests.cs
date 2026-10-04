using System.Globalization;
using Cadence.Core.Cron;
using Cadence.Core.Execution;
using Cadence.Core.Model;
using Cadence.Core.Scheduling;
using Cadence.Core.TaskXml;
using Cadence.Scheduler;
using Cadence.TestHarness;

namespace Cadence.IntegrationTests;

/// <summary>
/// PLAN 6.3 #3 and #4: the engine (NextRuns) against Task Scheduler. Both checks depend on the real clock or on a fixed October 2026
/// window, so they are written to tolerate the inherent races (see the comments). Tasks are composed disabled and never fire.
/// </summary>
public static class ParityTests
{
    static readonly TimeZoneInfo Tz = TimeZoneInfo.Local;

    /// <summary>
    /// NextRunTime of a disabled task equals the engine's first occurrence after "now". The one race: a trigger instant falling
    /// between our clock read and Task Scheduler's own (the minute boundary), so a mismatch is re-checked once with a fresh clock.
    /// </summary>
    public static void NextRunTimeMatchesTheEngine()
    {
        string[][] cases =
        [
            ["0 8 * * 1-5"], ["30 17 * * 1-5"], ["0 18 * * 1-5"], ["0 * * * *"], ["*/15 * * * *"], ["0 2 * * *"], ["0 9 * * 1"],
            ["0 0 L * *"], ["0 9 13 * 5"], ["0 17 * * 5L"], ["0 7 * * *", "30 19 * * *"], ["*/7 * * * *"], ["5,20,40 */2 * * *"],
            ["0 9-17 * * 1-5"], ["0-23,30 9,17 * * *"],
        ];
        int n = 0;
        foreach (string[] cron in cases)
        {
            n++;
            string label = "next run of " + string.Join(" + ", cron);
            string name = "par next " + n.ToString(CultureInfo.InvariantCulture);
            Fx.Case(label, () =>
            {
                var gw = Fx.Gateway();
                // Anchored today (a StartBoundary in the past is normal for a task saved yesterday).
                JobDefinition job = Fx.Job("Parity", cron, anchor: DateOnly.FromDateTime(DateTime.Today));
                TaskDefinitionModel model = TaskComposer.Compose(job, Fx.Ctx(gw, name));
                gw.Register(name, TaskXmlWriter.Write(model));
                DateTime? tsNext = null, engineNext = null;
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    DateTimeOffset now = DateTimeOffset.Now;
                    var occ = NextRuns.Next(model.Triggers, now, 1, Tz);
                    engineNext = occ.Count == 0 ? null : TimeZoneInfo.ConvertTime(occ[0].At, Tz).DateTime;
                    tsNext = gw.Get(name)?.NextRunTime;
                    if (tsNext == engineNext) break;
                }
                T.Eq(label, Fx.Iso(engineNext), Fx.Iso(tsNext));
                gw.Delete(name);
            });
        }
    }

    /// <summary>
    /// GetRunTimes (Appendix B) against the engine for CalendarTriggers. Measured: the oracle's range is exclusive at the start and inclusive
    /// at the end, so the windows run from one second before the first interesting instant to 23:59:59 of the last day (nothing fires at :59).
    /// The default window is October 2026 (before the 2026-11-01 DST change); the sparse monthly schedules get a winter window that contains
    /// their run. GetRunTimes is theoretical, so the real date does not matter. Known oracle defects (TimeTrigger repetition end, DST days)
    /// are avoided by construction: the compiler only emits calendar triggers here and no window contains a clock change (a case is skipped,
    /// with a note, if the machine's time zone changes clocks inside its window).
    /// </summary>
    public static void RunTimesOracleMatchesTheEngine()
    {
        var oct = (Start: new DateTime(2026, 9, 30, 23, 59, 59), End: new DateTime(2026, 10, 31, 23, 59, 59));
        var jan2027 = (Start: new DateTime(2026, 12, 31, 23, 59, 59), End: new DateTime(2027, 1, 31, 23, 59, 59));
        var feb2028 = (Start: new DateTime(2028, 1, 31, 23, 59, 59), End: new DateTime(2028, 3, 1, 0, 0, 0));   // a window ending 02-29 23:59:59 makes the oracle answer 0x41304 and nothing, though the run is at 02-29 00:00
        DateOnly anchor = new(2026, 10, 1);
        (string[] Cron, (DateTime Start, DateTime End) Window, DateOnly Anchor)[] cases =
        [
            (["0 8 * * 1-5"], oct, anchor), (["30 17 * * MON-FRI"], oct, anchor), (["0 * * * *"], oct, anchor), (["*/15 * * * *"], oct, anchor), (["0 9-17 * * 1-5"], oct, anchor),
            (["1-59/2 * * * *"], oct, anchor), (["*/7 * * * *"], oct, anchor), (["5,20,40 */2 * * *"], oct, anchor), (["0-58 * * * 1"], oct, anchor), (["0 0 L * *"], oct, anchor),
            (["0 17 * * 5L"], oct, anchor), (["0 9 13 * 5"], oct, anchor), (["0 0 * * 0,7"], oct, anchor), (["0 7 * * *", "30 19 * * *"], oct, anchor),
            (["0-23,30 9,17 * * *"], oct, anchor), (["0 9 * * 1#2"], oct, anchor),
            (["0 9 * 1,7 *"], jan2027, anchor), (["0 0 29 2 *"], feb2028, new DateOnly(2028, 1, 1)),
        ];
        int n = 0;
        foreach (var (cron, w, jobAnchor) in cases)
        {
            n++;
            string label = "oracle " + string.Join(" + ", cron);
            string name = "par oracle " + n.ToString(CultureInfo.InvariantCulture);
            Fx.Case(label, () =>
            {
                if (Tz.IsDaylightSavingTime(w.Start) != Tz.IsDaylightSavingTime(w.End) || Tz.GetUtcOffset(w.Start) != Tz.GetUtcOffset(w.End))
                {
                    Fx.Info(label + ": the local time zone changes clocks inside the window; skipped");
                    T.True(label + ": skipped (clock change in the window)", true);
                    return;
                }
                var gw = Fx.Gateway();
                JobDefinition job = Fx.Job("Oracle", cron, anchor: jobAnchor);
                TaskDefinitionModel model = TaskComposer.Compose(job, Fx.Ctx(gw, name));
                if (model.Triggers.Any(t => t is TimeTrigger or LogonTrigger))
                {
                    T.True(label + ": skipped (not calendar-only)", true);
                    return;
                }
                gw.Register(name, TaskXmlWriter.Write(model));

                List<DateTime> oracle = TestFolder.WithRawTask(name, t => RunTimesOracle.Get((object)t, w.Start, w.End, 100000));
                int hr = RunTimesOracle.LastHResult;
                var tsTimes = oracle.Distinct().Order().ToList();   // two triggers at one instant are listed twice by Task Scheduler

                var from = new DateTimeOffset(w.Start, Tz.GetUtcOffset(w.Start));
                var to = new DateTimeOffset(w.End, Tz.GetUtcOffset(w.End));
                var engine = NextRuns.Between(model.Triggers, from, to, Tz, 200_000)
                    .Select(o => TimeZoneInfo.ConvertTime(o.At, Tz).DateTime).Distinct().Order().ToList();

                // S_OK, S_FALSE (fewer than asked) and SCHED_S_TASK_NO_MORE_RUNS (0x41304: nothing in the window) are all answers.
                T.True(label + ": oracle answered (hr 0x" + ((uint)hr).ToString("X", CultureInfo.InvariantCulture) + ", " + tsTimes.Count + " runs)", hr is 0 or 1 or 0x41304);
                bool same = tsTimes.SequenceEqual(engine);
                T.True(label + ": engine == GetRunTimes (" + engine.Count + " runs)", same,
                    same ? null : "first difference: " + FirstDifference(engine, tsTimes));
                gw.Delete(name);
            });
        }
    }

    /// <summary>
    /// MEASURED TASK SCHEDULER QUIRK (reported, engine unchanged): for <c>0 0 29 2 *</c> (a monthly trigger on day 29, February only) whose StartBoundary
    /// lies before a non-leap February, Task Scheduler's NextRunTime and GetRunTimes list an extra run on 2028-02-01 00:00 besides the real 2028-02-29.
    /// With a StartBoundary inside 2028 the extra run disappears. Whether it also FIRES on 2028-02-01 cannot be observed before then.
    /// This test pins what is known without depending on the clock: the engine's run is always present, and the only extra TS may list is 02-01.
    /// </summary>
    public static void Feb29QuirkIsCharacterized()
    {
        Fx.Case("feb 29", () =>
        {
            var gw = Fx.Gateway();
            const string name = "par feb29";
            JobDefinition job = Fx.Job("Feb 29", ["0 0 29 2 *"], anchor: new DateOnly(2026, 10, 1));
            TaskDefinitionModel model = TaskComposer.Compose(job, Fx.Ctx(gw, name));
            gw.Register(name, TaskXmlWriter.Write(model));
            var from = new DateTime(2026, 10, 1);
            var to = new DateTime(2031, 1, 1);
            List<DateTime> oracle = TestFolder.WithRawTask(name, t => RunTimesOracle.Get((object)t, from, to, 1000));
            var engine = NextRuns.Between(model.Triggers, new DateTimeOffset(from, Tz.GetUtcOffset(from)), new DateTimeOffset(to, Tz.GetUtcOffset(to)), Tz)
                .Select(o => TimeZoneInfo.ConvertTime(o.At, Tz).DateTime).ToList();
            T.Eq("the engine finds the leap days 2028 and 2032 is outside the window: only 2028-02-29", "2028-02-29T00:00:00", string.Join(",", engine.Select(e => Fx.Iso(e))));
            T.True("every engine run is in the oracle's list", engine.All(oracle.Contains));
            var extras = oracle.Except(engine).Distinct().ToList();
            Fx.Info("Feb 29 quirk: extra runs listed by Task Scheduler = [" + string.Join(", ", extras.Select(e => Fx.Iso(e))) + "]");
            T.True("the only run Task Scheduler adds is 2028-02-01 00:00", extras.All(d => d == new DateTime(2028, 2, 1)));
            gw.Delete(name);
        });
    }

    /// <summary>CP-A: the Duration == Interval repetitions fire exactly twice a day per minute value (09:mm and 17:mm).</summary>
    public static void DurationEqualsIntervalFiresTwiceADay()
    {
        var windowStart = new DateTime(2026, 9, 30, 23, 59, 59);
        var windowEnd = new DateTime(2026, 10, 7, 23, 59, 59);
        Fx.Case("0-23,30 9,17", () =>
        {
            var gw = Fx.Gateway();
            const string name = "par twice";
            JobDefinition job = Fx.Job("Twice", ["0-23,30 9,17 * * *"], anchor: new DateOnly(2026, 10, 1));
            TaskDefinitionModel model = TaskComposer.Compose(job, Fx.Ctx(gw, name));
            gw.Register(name, TaskXmlWriter.Write(model));
            List<DateTime> oracle = TestFolder.WithRawTask(name, t => RunTimesOracle.Get((object)t, windowStart, windowEnd, 100000));
            var days = oracle.Distinct().GroupBy(d => d.Date).ToList();
            T.Eq("seven days of runs", 7, days.Count);
            int[] minutes = [.. Enumerable.Range(0, 24), 30];
            foreach (var day in days)
            {
                int[] expected = [.. minutes.SelectMany(m => new[] { 9 * 60 + m, 17 * 60 + m }).Order()];
                int[] actual = [.. day.Select(d => d.Hour * 60 + d.Minute).Order()];
                T.True(day.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ": each minute value fires at 9 and 17 only, 50 runs (" + actual.Length + ")",
                    expected.SequenceEqual(actual));
            }
            gw.Delete(name);
        });
    }

    static string FirstDifference(List<DateTime> engine, List<DateTime> ts)
    {
        int i = 0;
        while (i < engine.Count && i < ts.Count && engine[i] == ts[i]) i++;
        string e = i < engine.Count ? Fx.Iso(engine[i]) : "(end)";
        string t = i < ts.Count ? Fx.Iso(ts[i]) : "(end)";
        return "index " + i + ": engine " + e + ", task scheduler " + t;
    }
}
