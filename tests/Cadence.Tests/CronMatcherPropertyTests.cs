using System.Diagnostics;
using System.Globalization;
using Cadence.Core.Cron;
using Cadence.Core.Model;
using Cadence.Core.Scheduling;
using Cadence.Cron;
using Cadence.TestHarness;

namespace Cadence.Tests;

/// <summary>
/// The 3.6 property test: a direct cron matcher (CronMatcher) must agree, minute by minute, with NextRuns over the
/// compiled triggers. Deterministic: fixed window (2026-10-01 .. 2026-12-31, excluding the 2026-11-01 DST day),
/// explicit Eastern time zone, fixed anchor, fixed random seed. Never depends on today's date.
/// </summary>
public static class CronMatcherPropertyTests
{
    static readonly TimeZoneInfo Est = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
    static readonly DateOnly Anchor = new(2026, 9, 15);
    static readonly DateOnly FirstDay = new(2026, 10, 1);
    static readonly DateOnly LastDay = new(2026, 12, 31);
    static readonly DateOnly DstDay = new(2026, 11, 1);
    static readonly MapContext Ctx = new(@"TESTPC\tester");

    /// <summary>Every successful row of the 3.6 table.</summary>
    static readonly string[][] Table36 =
    [
        ["0 8 * * 1-5"], ["30 17 * * MON-FRI"], ["0 * * * *"], ["*/15 * * * *"], ["* * * * *"],
        ["0 9-17 * * 1-5"], ["1-59/2 * * * *"], ["*/7 * * * *"], ["5,20,40 */2 * * *"], ["0-58 * * * 1"],
        ["0 0 L * *"], ["0 17 * * 5L"], ["0 9 13 * 5"], ["0 0 * * 0,7"], ["0 0 29 2 *"], ["0 9 * 1,7 *"],
        ["0 7 * * *", "30 19 * * *"], ["@logon"], ["0 0 0 * * *"],
    ];

    /// <summary>The nine template crons of 3.5 (hard-coded here because the catalog belongs to a later phase).</summary>
    static readonly string[][] Templates =
    [
        ["0 8 * * 1-5"], ["30 17 * * 1-5"], ["0 18 * * 1-5"], ["0 * * * *"], ["*/15 * * * *"],
        ["0 2 * * *"], ["0 9 * * 1"], ["@logon"], ["0 9 * * *"],
    ];

    /// <summary>The remaining cron lines of the 3.5 renderer table, plus other shapes worth cross-checking.</summary>
    static readonly string[][] Extra =
    [
        ["@hourly"], ["@daily"], ["@weekly"], ["@monthly"], ["@yearly"], ["0 */2 * * *"], ["0 6 * * 0-6"],
        ["0 7,19 * * 1,3,5"], ["0 10 * * 0,6"], ["*/30 8-17 * * *"], ["0,30 9-17 * * *"], ["15 8-22/3 * * *"],
        ["0 9 1,15 * *"], ["0 9 31 * *"], ["0 9 * * 1#1"], ["0 9 * 1,4,7,10 1#1"], ["0 9 1 */3 *"],
        ["0 23 L * *"], ["0 9 30 * *"], ["0 9 29-31 * *"], ["0 9 * * 1,5L"], ["0 9 * * 1#1,2#2"], ["0 9 * * 1#1,1#3"],
        ["0 9 * 11,12 1-5"], ["0 9 L 11 1"], ["0 9 1-31 * 1"], ["0 9 * * 1,1L"], ["0 0,8,16 * * *"], ["0 1,2,4 * * *"],
        ["0,30 7,19 * * *"], ["0-10/5 1,2,4 * * *"], ["*/7 * 13 * 5"], ["0 0 * * 6L"], ["0 12 * 12 *"],
        ["0 12 * * 5#4"], ["59 23 L * *"], ["0 0 * 10-12 *"], ["*/10 22-23 * * *"],
    ];

    // [CP-A] A second window, Jan-Mar 2028 (a leap year, minus its 2028-03-12 DST day): the original Oct-Dec window has no February,
    // so day 29/30/31 and L rules in February (and "0 0 29 2 *", which had zero matches) were never really compared.
    // The day AFTER a spring-forward day is excluded too: an elapsed-time repetition that spans the 23-hour day ends one hour into
    // the next day (see DstSpringForwardLeakIsTheEnginePolicy), which is the R1 policy, not a compiler error.
    sealed record Window(DateOnly First, DateOnly Last, DateOnly Dst, DateTimeOffset From, DateTimeOffset To, bool SkipDayAfterDst = false)
    {
        public bool Excluded(DateOnly d) => d == Dst || (SkipDayAfterDst && d == Dst.AddDays(1));
    }

    static readonly Window Autumn2026 = new(FirstDay, LastDay, DstDay,
        new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.FromHours(-4)), new DateTimeOffset(2026, 12, 31, 23, 59, 0, TimeSpan.FromHours(-5)));
    static readonly Window Winter2028 = new(new DateOnly(2028, 1, 1), new DateOnly(2028, 3, 31), new DateOnly(2028, 3, 12),
        new DateTimeOffset(2028, 1, 1, 0, 0, 0, TimeSpan.FromHours(-5)), new DateTimeOffset(2028, 3, 31, 23, 59, 0, TimeSpan.FromHours(-4)), SkipDayAfterDst: true);

    // [CP-A] Pins the (unverified, R1) DST policy consequence found while widening the property test: on the spring-forward day a
    // full-day repetition is 23 elapsed hours long, so a weekday-restricted job also fires at 0:37 on the following Monday, which
    // cron would not do. On the fall-back day the last hour is missed instead. M-DST (2026-11-01) must check what Task Scheduler does.
    public static void DstSpringForwardLeakIsTheEnginePolicy()
    {
        var spec = new ScheduleSpec(ValueList.Of("37 * * * 0"), HolidayCalendar.None, Anchor);
        var compiled = CronCompiler.Compile(spec, Ctx);
        var runs = NextRuns.Between(compiled.Triggers, new DateTimeOffset(2028, 3, 12, 0, 0, 0, TimeSpan.FromHours(-5)),
            new DateTimeOffset(2028, 3, 13, 23, 59, 0, TimeSpan.FromHours(-4)), Est);
        var local = runs.Select(o => TimeZoneInfo.ConvertTime(o.At, Est).DateTime).ToList();
        T.Eq("Sunday 2028-03-12 has 23 runs (2:37 does not exist)", 23, local.Count(t => t.Date == new DateTime(2028, 3, 12)));
        T.True("[policy] one run leaks to Monday 0:37", local.Count(t => t.Date == new DateTime(2028, 3, 13)) == 1 && local.Contains(new DateTime(2028, 3, 13, 0, 37, 0)));

        var fall = NextRuns.Between(compiled.Triggers, new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.FromHours(-4)),
            new DateTimeOffset(2026, 11, 1, 23, 59, 0, TimeSpan.FromHours(-5)), Est);
        var fallLocal = fall.Select(o => TimeZoneInfo.ConvertTime(o.At, Est).DateTime).ToList();
        T.True("[policy] fall-back Sunday: 1:37 twice, last run 22:37 (23:37 missed)", fallLocal.Count == 24 && fallLocal[^1] == new DateTime(2026, 11, 1, 22, 37, 0));
    }

    static List<DateTime> ExpectedMinutes(IReadOnlyList<CronExpression> exprs, Window w)
    {
        var result = new List<DateTime>();
        for (var d = w.First; d <= w.Last; d = d.AddDays(1))
        {
            if (w.Excluded(d)) continue;
            var today = exprs.Where(e => CronMatcher.MatchesDate(e, d)).ToList();
            if (today.Count == 0) continue;
            for (int h = 0; h < 24; h++)
                for (int m = 0; m < 60; m++)
                {
                    var t = d.ToDateTime(new TimeOnly(h, m));
                    if (CronMatcher.Matches(today, t)) result.Add(t);
                }
        }
        return result;
    }

    static List<DateTime> EngineMinutes(ValueList<TaskTrigger> triggers, Window w, out bool anyDstAdjusted)
    {
        var occ = NextRuns.Between(triggers, w.From, w.To, Est, 1_000_000)
            .Where(o => !w.Excluded(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(o.At, Est).DateTime))).ToList();
        anyDstAdjusted = occ.Any(o => o.DstAdjusted);
        return occ.Select(o => TimeZoneInfo.ConvertTime(o.At, Est).DateTime).ToList();
    }

    /// <summary>Returns the number of matching minutes (or -1 when the case could not be compiled).</summary>
    static int Check(string name, string[] lines, bool mustCompile = true)
    {
        var spec = new ScheduleSpec(new ValueList<string>(lines), HolidayCalendar.None, Anchor);
        var compiled = CronCompiler.Compile(spec, Ctx);
        if (!compiled.Success)
        {
            if (mustCompile) T.True(name + " compiles", false, string.Join("; ", compiled.Diagnostics.Select(d => d.Message)));
            return -1;
        }

        var exprs = new List<CronExpression>();
        foreach (var (line, i) in lines.Select((l, i) => (l, i)))
        {
            var e = CronParser.Parse(line, i + 1, []);
            if (e is null) { T.True(name + " parses", false, line); return -1; }
            exprs.Add(e);
        }

        int first = CompareWindow(name, compiled, exprs, Autumn2026, "");
        CompareWindow(name, compiled, exprs, Winter2028, " [2028 Q1]");
        return first;
    }

    static int CompareWindow(string name, CompiledSchedule compiled, List<CronExpression> exprs, Window w, string tag)
    {
        var expected = ExpectedMinutes(exprs, w);
        var actual = EngineMinutes(compiled.Triggers, w, out bool adjusted);
        T.True(name + tag + ": no DST adjustment in the window", !adjusted);

        if (expected.SequenceEqual(actual))
        {
            T.True($"{name}{tag}: matcher == engine ({expected.Count} minutes, {compiled.Triggers.Count} triggers)", true);
            return expected.Count;
        }

        var exp = new HashSet<DateTime>(expected);
        var act = new HashSet<DateTime>(actual);
        var missing = expected.Where(t => !act.Contains(t)).Take(3).Select(Fmt);
        var extra = actual.Where(t => !exp.Contains(t)).Take(3).Select(Fmt);
        T.True($"{name}{tag}: matcher == engine", false,
            $"expected {expected.Count} minutes, engine gave {actual.Count}; engine missing [{string.Join(", ", missing)}], engine extra [{string.Join(", ", extra)}]");
        return expected.Count;
    }

    public static void LeapFebruaryCounts()
    {
        // [CP-A] Non-vacuous February checks in the 2028 window (exact counts, so an empty matcher and engine can't agree).
        (string Cron, int Count)[] known =
        [
            ("0 0 29 2 *", 1),        // 2028-02-29
            ("0 9 L 2 *", 1),         // 2028-02-29
            ("0 9 30 * *", 2),        // Jan 30, Mar 30 (none in February)
            ("0 9 31 * *", 2),        // Jan 31, Mar 31
            ("0 9 29-31 * *", 7),     // Jan 29-31, Feb 29, Mar 29-31
            ("0 9 * * 2L", 3),        // Jan 25, Feb 29, Mar 28
        ];
        foreach (var (cron, count) in known)
        {
            var spec = new ScheduleSpec(ValueList.Of(cron), HolidayCalendar.None, Anchor);
            var compiled = CronCompiler.Compile(spec, Ctx);
            var expr = CronParser.Parse(cron, 1, [])!;
            int n = CompareWindow("feb " + cron, compiled, [expr], Winter2028, " [2028 Q1]");
            T.Eq("2028 Q1 count " + cron, count, n);
        }
    }

    static string Fmt(DateTime t) => t.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    static string Name(string[] lines) => string.Join(" + ", lines);

    public static void Table36Rows()
    {
        foreach (var lines in Table36) Check(Name(lines), lines);
    }

    public static void TemplateCrons()
    {
        foreach (var lines in Templates) Check("template " + Name(lines), lines);
    }

    public static void RendererTableAndOtherShapes()
    {
        foreach (var lines in Extra) Check(Name(lines), lines);
    }

    public static void KnownCounts()
    {
        // Guard against a matcher and an engine that are both empty.
        var known = new (string[] Lines, int Count)[]
        {
            (["0 8 * * 1-5"], 66),                       // 22 + 21 + 23 weekdays
            (["*/15 * * * *"], 91 * 96),                 // 92 days minus the DST day
            (["0 * * * *"], 91 * 24),
            (["* * * * *"], 91 * 1440),
            (["0 0 L * *"], 3),                          // Oct 31, Nov 30, Dec 31
            (["0 17 * * 5L"], 3),                        // Oct 30, Nov 27, Dec 25
            (["0 9 13 * 5"], 13 + 2),                    // 13 Fridays + the 13ths of Oct and Dec (Nov 13 is a Friday)
            (["0 9 * 1,7 *"], 0),
            (["0 0 29 2 *"], 0),
            (["0 9 31 * *"], 2),                         // Oct 31, Dec 31
            (["0 9 * * 1#1"], 3),                        // Oct 5, Nov 2, Dec 7
            (["0 0 * * 0,7"], 12),                       // 13 Sundays minus Nov 1
            (["0 7 * * *", "30 19 * * *"], 2 * 91),
            (["@logon"], 0),
        };
        foreach (var (lines, count) in known)
        {
            int n = Check("count " + Name(lines), lines);
            T.Eq("count " + Name(lines), count, n);
        }
    }

    public static void GeneratedCorpus()
    {
        var sw = Stopwatch.StartNew();
        var rnd = new Random(20261001);
        int compiled = 0, skipped = 0, total = 0;
        for (int i = 0; i < 160; i++)
        {
            int lineCount = rnd.Next(10) < 7 ? 1 : rnd.Next(2, 4);
            var lines = new string[lineCount];
            for (int k = 0; k < lineCount; k++) lines[k] = RandomLine(rnd);
            total++;
            int n = Check($"random #{i} [{Name(lines)}]", lines, mustCompile: false);
            if (n < 0) skipped++; else compiled++;
        }
        T.True($"corpus: {compiled} compiled, {skipped} over the 48-trigger limit (skipped), {total} generated", compiled >= 120);
        Console.WriteLine($"INFO CronMatcherProperty corpus took {sw.ElapsedMilliseconds} ms");
    }

    // ----- generator -----------------------------------------------------------------------------

    static string RandomLine(Random r)
    {
        string minute = RandomField(r, 0, 59, [5, 10, 15, 20, 30], null);
        string hour = RandomField(r, 0, 23, [2, 3, 4, 6, 12], null);
        string dom = RandomDom(r);
        string month = RandomMonth(r);
        string dow = RandomDow(r);
        return $"{minute} {hour} {dom} {month} {dow}";
    }

    static string Num(int v) => v.ToString(CultureInfo.InvariantCulture);

    static string RandomField(Random r, int min, int max, int[] steps, string[]? names)
    {
        int roll = r.Next(100);
        int a = r.Next(min, max + 1);
        if (roll < 35) return Num(a);
        if (roll < 55) return string.Join(",", Enumerable.Range(0, r.Next(2, 5)).Select(_ => Num(r.Next(min, max + 1))));
        if (roll < 65) { int b = r.Next(a, max + 1); return $"{Num(a)}-{Num(b)}"; }
        if (roll < 75) return "*/" + Num(steps[r.Next(steps.Length)]);
        if (roll < 82) return "*";
        if (roll < 92) { int b = r.Next(a, max + 1); return $"{Num(a)}-{Num(b)}/{Num(steps[r.Next(steps.Length)])}"; }
        return $"{Num(a)}/{Num(steps[r.Next(steps.Length)])}";
    }

    static string RandomDom(Random r)
    {
        int roll = r.Next(100);
        if (roll < 55) return "*";
        if (roll < 70) return Num(r.Next(1, 32));
        if (roll < 80) return string.Join(",", Enumerable.Range(0, r.Next(2, 4)).Select(_ => Num(r.Next(1, 32))));
        if (roll < 85) return "L";
        if (roll < 90) { int a = r.Next(1, 25); return $"{Num(a)}-{Num(a + r.Next(0, 7))}"; }
        if (roll < 95) return "1,15,L";
        return "*/" + Num(r.Next(2, 16));
    }

    static string RandomMonth(Random r)
    {
        int roll = r.Next(100);
        if (roll < 70) return "*";
        if (roll < 85) return string.Join(",", Enumerable.Range(0, r.Next(1, 4)).Select(_ => Num(r.Next(1, 13))));
        if (roll < 95) { int a = r.Next(1, 12); return $"{Num(a)}-{Num(r.Next(a, 13))}"; }
        return new[] { "OCT-DEC", "nov", "JAN,JUL", "*/3" }[r.Next(4)];
    }

    static string RandomDow(Random r)
    {
        int roll = r.Next(100);
        if (roll < 50) return "*";
        if (roll < 60) return "1-5";
        if (roll < 70) return string.Join(",", Enumerable.Range(0, r.Next(1, 4)).Select(_ => Num(r.Next(0, 8))));
        if (roll < 80) return Num(r.Next(0, 7)) + "L";
        if (roll < 90) return Num(r.Next(0, 7)) + "#" + Num(r.Next(1, 5));
        if (roll < 95) return Num(r.Next(0, 7)) + "," + Num(r.Next(0, 7)) + "L";
        return new[] { "MON-FRI", "sat,sun", "FRI-SUN" }[r.Next(3)];
    }
}
