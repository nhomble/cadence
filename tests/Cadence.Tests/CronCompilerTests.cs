using System.Globalization;
using Cadence.Core.Cron;
using Cadence.Core.Model;
using Cadence.Core.Text;
using Cadence.TestHarness;

namespace Cadence.Tests;

public static class CronCompilerTests
{
    static readonly DateOnly Anchor = new(2026, 9, 30);
    static readonly MapContext Ctx = new(@"TESTPC\tester");

    static CompiledSchedule Compile(params string[] lines) =>
        CronCompiler.Compile(new ScheduleSpec(new ValueList<string>(lines), HolidayCalendar.None, Anchor), Ctx);

    static DateTime Start(int h, int m) => Anchor.ToDateTime(new TimeOnly(h, m));

    static IEnumerable<CronDiagnostic> Of(CompiledSchedule c, CronSeverity s) => c.Diagnostics.Where(d => d.Severity == s);

    static bool HasInfo(CompiledSchedule c, string text) =>
        Of(c, CronSeverity.Info).Any(d => d.Message.Contains(text, StringComparison.Ordinal));

    static bool HasError(CompiledSchedule c, string text) =>
        Of(c, CronSeverity.Error).Any(d => d.Message.Contains(text, StringComparison.Ordinal));

    static string Fmt(TaskTrigger t)
    {
        string kind = t switch
        {
            DailyTrigger d => $"Daily({d.DaysInterval})",
            WeeklyTrigger w => $"Weekly({w.WeeksInterval},{w.Days})",
            MonthlyTrigger m => $"Monthly([{string.Join(",", m.Days)}],last={m.LastDay},{m.Months})",
            MonthlyDowTrigger md => $"MonthlyDow([{string.Join(",", md.Weeks)}],{md.Days},{md.Months})",
            LogonTrigger l => $"Logon({l.UserId})",
            _ => t.GetType().Name,
        };
        string rep = t.Repetition is null ? "" :
            $" every {t.Repetition.Interval.TotalMinutes.ToString(CultureInfo.InvariantCulture)}m for {t.Repetition.Duration?.TotalMinutes.ToString(CultureInfo.InvariantCulture)}m stopAtEnd={t.Repetition.StopAtDurationEnd}";
        return kind + " @" + t.StartBoundary.ToString("HH:mm", CultureInfo.InvariantCulture) + rep;
    }

    static string Shapes(CompiledSchedule c) => string.Join(" | ", c.Triggers.Select(Fmt));

    // ----- every 3.6 row: success and trigger count ----------------------------------------------

    public static void Table36()
    {
        var rows = new (string[] Lines, bool Success, int Triggers)[]
        {
            (["0 8 * * 1-5"], true, 1),
            (["30 17 * * MON-FRI"], true, 1),
            (["0 * * * *"], true, 1),
            (["*/15 * * * *"], true, 1),
            (["* * * * *"], true, 1),
            (["0 9-17 * * 1-5"], true, 1),
            (["1-59/2 * * * *"], true, 1),
            (["*/7 * * * *"], true, 9),
            (["5,20,40 */2 * * *"], true, 3),
            (["0-58 * * * 1"], true, 24),
            (["0 0 L * *"], true, 1),
            (["0 17 * * 5L"], true, 1),
            (["0 9 13 * 5"], true, 2),
            (["0 0 * * 0,7"], true, 1),
            (["0 0 29 2 *"], true, 1),
            (["0 9 * 1,7 *"], true, 1),
            (["0 7 * * *", "30 19 * * *"], true, 2),
            (["@logon"], true, 1),
            (["0 0 0 * * *"], true, 1),
            (["@reboot"], false, 0),
            (["*/30 * * * * *"], false, 0),
            (["0 9 15W * *"], false, 0),
            (["0 9 LW * *"], false, 0),
            (["0 9 * * 1#5"], false, 0),
            (["*/90 * * * *"], false, 0),
            (["61 * * * *"], false, 0),
            (["0 22-2 * * *"], false, 0),
            (["0 9 * * * 2027"], false, 0),
            (["hello"], false, 0),
        };
        foreach (var (lines, success, triggers) in rows)
        {
            var c = Compile(lines);
            string name = string.Join(" + ", lines);
            T.Eq(name + " success", success, c.Success);
            T.Eq(name + " trigger count", triggers, c.Triggers.Count);
            if (!success)
            {
                T.True(name + " has an Error", Of(c, CronSeverity.Error).Any());
                T.True(name + " Error is a plain sentence naming the line", Of(c, CronSeverity.Error).All(d => d.Message.StartsWith("line ", StringComparison.Ordinal)));
                T.Eq(name + " no groups", 0, c.Groups.Count);
                T.Eq(name + " AtLogon false", false, c.AtLogon);
            }
            else
            {
                T.True(name + " no Error", !Of(c, CronSeverity.Error).Any());
            }
        }
    }

    public static void Table36With49Lines()
    {
        // The plan's "49 lines" row: 49 lines would also break the 10-line limit, so both Errors appear.
        var lines = Enumerable.Range(0, 49).Select(m => m.ToString(CultureInfo.InvariantCulture) + " 0 1 1 *").ToArray();
        var c = Compile(lines);
        T.Eq("49 lines: failed", false, c.Success);
        T.Eq("49 lines: no triggers", 0, c.Triggers.Count);
        T.True("49 lines: needs 49 triggers", HasError(c, "needs 49 triggers"));
        T.True("49 lines: line limit", HasError(c, "between 1 and 10 cron lines"));
    }

    // ----- trigger shapes ------------------------------------------------------------------------

    static void Shape(string cron, string expected) => T.Eq("shape " + cron, expected, Shapes(Compile(cron)));

    public static void Shapes36()
    {
        Shape("0 8 * * 1-5", "Weekly(1,Weekdays) @08:00");
        Shape("30 17 * * MON-FRI", "Weekly(1,Weekdays) @17:30");
        Shape("0 * * * *", "Daily(1) @00:00 every 60m for 1380m stopAtEnd=False");
        Shape("*/15 * * * *", "Daily(1) @00:00 every 15m for 1425m stopAtEnd=False");   // PT23H45M
        Shape("* * * * *", "Daily(1) @00:00 every 1m for 1439m stopAtEnd=False");       // PT23H59M
        Shape("0 9-17 * * 1-5", "Weekly(1,Weekdays) @09:00 every 60m for 480m stopAtEnd=False");   // PT8H
        Shape("1-59/2 * * * *", "Daily(1) @00:01 every 2m for 1438m stopAtEnd=False");
        Shape("0 0 L * *", "Monthly([],last=True,All) @00:00");
        Shape("0 17 * * 5L", "MonthlyDow([Last],Friday,All) @17:00");
        Shape("0 0 * * 0,7", "Weekly(1,Sunday) @00:00");
        Shape("0 0 29 2 *", "Monthly([29],last=False,February) @00:00");
        Shape("0 9 * 1,7 *", "Monthly([" + string.Join(",", Enumerable.Range(1, 31)) + "],last=False,January, July) @09:00");
        Shape("0 0 0 * * *", "Daily(1) @00:00");
        Shape("@logon", @"Logon(TESTPC\tester) @00:00");
    }

    public static void ShapeOrSemantics()
    {
        Shape("0 9 13 * 5", "Monthly([13],last=False,All) @09:00 | Weekly(1,Friday) @09:00");
    }

    public static void ShapeStrategyC()
    {
        // */7: one hourly repetition per minute value, 0:m through 23:m (PT23H each).
        var c = Compile("*/7 * * * *");
        var expected = string.Join(" | ", new[] { 0, 7, 14, 21, 28, 35, 42, 49, 56 }
            .Select(m => $"Daily(1) @00:{m:00} every 60m for 1380m stopAtEnd=False"));
        T.Eq("*/7 shapes", expected, Shapes(c));
        T.Eq("*/7 groups", 9, c.Groups.Count);
        T.True("*/7 is Info 'represented exactly with 9 triggers'", HasInfo(c, "represented exactly with 9 triggers"));

        var c2 = Compile("5,20,40 */2 * * *");
        T.Eq("5,20,40 */2 shapes",
            "Daily(1) @00:05 every 120m for 1320m stopAtEnd=False | Daily(1) @00:20 every 120m for 1320m stopAtEnd=False | Daily(1) @00:40 every 120m for 1320m stopAtEnd=False",
            Shapes(c2));
        T.True("5,20,40 */2 Info", HasInfo(c2, "represented exactly with 3 triggers"));
    }

    public static void ShapeStrategyD()
    {
        var c = Compile("0-58 * * * 1");
        T.Eq("0-58 trigger count (D beats C)", 24, c.Triggers.Count);
        T.Eq("first", "Weekly(1,Monday) @00:00 every 1m for 58m stopAtEnd=False", Fmt(c.Triggers[0]));
        T.Eq("last", "Weekly(1,Monday) @23:00 every 1m for 58m stopAtEnd=False", Fmt(c.Triggers[23]));
        T.True("0-58 Info", HasInfo(c, "represented exactly with 24 triggers"));
    }

    public static void StrategyChoices()
    {
        var one = Compile("0 9 * * *");
        T.Eq("A: one time", "Daily(1) @09:00", Shapes(one));
        T.True("A has no Info", !Of(one, CronSeverity.Info).Any());
        T.True("A group is AtTimes", one.Groups[0].Times is AtTimes);

        var two = Compile("0 7,19 * * *");
        T.Eq("two times stay two plain times", "Daily(1) @07:00 | Daily(1) @19:00", Shapes(two));
        T.True("two times: one AtTimes group", two.Groups.Count == 1 && two.Groups[0].Times is AtTimes { Times.Count: 2 });

        var twoMin = Compile("0,30 9 * * *");
        T.Eq("two minutes stay two plain times", "Daily(1) @09:00 | Daily(1) @09:30", Shapes(twoMin));

        var b = Compile("0,30 9-17 * * *");
        T.Eq("B: 0,30 9-17", "Daily(1) @09:00 every 30m for 510m stopAtEnd=False", Shapes(b));
        T.True("B group is RepeatEvery", b.Groups[0].Times is RepeatEvery r && r.Every == TimeSpan.FromMinutes(30) && r.First == new TimeOnly(9, 0) && r.Last == new TimeOnly(17, 30));

        T.Eq("B: 0 */2", "Daily(1) @00:00 every 120m for 1320m stopAtEnd=False", Shapes(Compile("0 */2 * * *")));
        T.Eq("B: 15 8-22/3", "Daily(1) @08:15 every 180m for 720m stopAtEnd=False", Shapes(Compile("15 8-22/3 * * *")));
        T.Eq("B: three hours 0,8,16", "Daily(1) @00:00 every 480m for 960m stopAtEnd=False", Shapes(Compile("0 0,8,16 * * *")));

        // Not a progression: E lists the times (here: 1,2,4 gaps), 3 triggers.
        var e = Compile("0 1,2,4 * * *");
        T.Eq("E: 1,2,4", "Daily(1) @01:00 | Daily(1) @02:00 | Daily(1) @04:00", Shapes(e));
        T.True("E has no Info", !Of(e, CronSeverity.Info).Any());

        // [CP-A] Two hours and two minutes: a two-element progression would repeat exactly once (Duration == Interval, "every 12 hours
        // from 7:00 to 19:00"), so the four times are listed (E), like the |T| = 2 rule.
        var c2 = Compile("0,30 7,19 * * *");
        T.Eq("two-element progressions are listed", "Daily(1) @07:00 | Daily(1) @07:30 | Daily(1) @19:00 | Daily(1) @19:30", Shapes(c2));
        T.Eq("two-element progressions: English", "Every day at 7:00, 7:30, 19:00 and 19:30", EnglishRenderer.Describe(c2, HolidayCalendar.None));
        T.True("no repetition has Duration == Interval", c2.Triggers.All(t => t.Repetition is null));

        // C and D tie at 3 triggers (hours 1,9,17 and minutes 0,20,40, both progressions): C is preferred (A < B < C < D < E).
        var c = Compile("0,20,40 1,9,17 * * *");
        T.True("tie prefers C", c.Triggers.Count == 3 && c.Groups.All(g => g.Times is RepeatEvery r && r.Every == TimeSpan.FromHours(8)));

        // ... but when listing would not fit in 48 triggers, a two-element progression is still used.
        var wide = Compile("0-23,30 9,17 * * *");
        T.True("25 minutes x 2 hours: C with 25 triggers instead of 50 listed times", wide.Success && wide.Triggers.Count == 25, Shapes(wide));

        // D cheaper than C: minutes progression, hours not (hours 1,2,4 so no C).
        var d = Compile("0-10/5 1,2,4 * * *");
        T.Eq("D when hours are not a progression", "Daily(1) @01:00 every 5m for 10m stopAtEnd=False | Daily(1) @02:00 every 5m for 10m stopAtEnd=False | Daily(1) @04:00 every 5m for 10m stopAtEnd=False", Shapes(d));
    }

    // ----- days ----------------------------------------------------------------------------------

    public static void DayRuleTable()
    {
        Shape("0 9 * * *", "Daily(1) @09:00");
        Shape("0 9 * 1,7 *", "Monthly([" + string.Join(",", Enumerable.Range(1, 31)) + "],last=False,January, July) @09:00");
        Shape("0 9 * * 1,3,5", "Weekly(1,Monday, Wednesday, Friday) @09:00");
        Shape("0 9 * 1,7 1-5", "MonthlyDow([First,Second,Third,Fourth,Last],Weekdays,January, July) @09:00");
        Shape("0 9 1,15,L * *", "Monthly([1,15],last=True,All) @09:00");
        Shape("0 9 31 * *", "Monthly([31],last=False,All) @09:00");
        Shape("0 9 * * 1#1", "MonthlyDow([First],Monday,All) @09:00");
        Shape("0 9 * 1,4,7,10 1#1", "MonthlyDow([First],Monday,January, April, July, October) @09:00");
        Shape("0 9 1 */3 *", "Monthly([1],last=False,January, April, July, October) @09:00");
        Shape("0 9 * * 1#1,1#3", "MonthlyDow([First,Third],Monday,All) @09:00");
        Shape("0 9 * * 1#1,2#1", "MonthlyDow([First],Monday, Tuesday,All) @09:00");
        Shape("0 9 * * 1#1,2#2", "MonthlyDow([First],Monday,All) @09:00 | MonthlyDow([Second],Tuesday,All) @09:00");
        Shape("0 9 * * 1,5L", "Weekly(1,Monday) @09:00 | MonthlyDow([Last],Friday,All) @09:00");
        Shape("0 9 * * 1,1L", "Weekly(1,Monday) @09:00");                            // Nth on a plain weekday adds nothing
        Shape("0 9 * * 1#1,1#2,1#3,1#4,1L", "Weekly(1,Monday) @09:00");              // every week = plain weekday
        Shape("0 9 * * 0-6", "Daily(1) @09:00");
        Shape("0 9 1-31 * *", "Daily(1) @09:00");
        Shape("0 9 1-31 * 1", "Daily(1) @09:00");                                     // OR with an all-covering side
        Shape("0 9 * 2 0-6", "Monthly([" + string.Join(",", Enumerable.Range(1, 31)) + "],last=False,February) @09:00");
        Shape("0 9 L 6 1", "Monthly([],last=True,June) @09:00 | MonthlyDow([First,Second,Third,Fourth,Last],Monday,June) @09:00");
    }

    public static void DiagnosticsOrAndShortMonths()
    {
        var plain = Compile("0 8 * * 1-5");
        T.Eq("plain schedule has no diagnostics", 0, plain.Diagnostics.Count);

        var or = Compile("0 9 13 * 5");
        T.True("OR Info", HasInfo(or, "runs when either the day of month or the weekday matches"));
        T.True("OR Info names the line", Of(or, CronSeverity.Info).Any(d => d.Line == 1 && d.Message.StartsWith("line 1, day-of-month:", StringComparison.Ordinal)));
        T.True("day 13 gives no short-month Info", !HasInfo(or, "skipped"));

        T.True("day 31 Info", HasInfo(Compile("0 9 31 * *"), "line 1, day-of-month: day 31 is skipped in months without that day"));
        T.True("day 29 Info", HasInfo(Compile("0 0 29 2 *"), "day 29 is skipped in months without that day"));
        T.True("day 30 Info", HasInfo(Compile("0 9 30 * *"), "day 30 is skipped"));
        T.True("days 29-31 Info", HasInfo(Compile("0 9 29-31 * *"), "days 29, 30 and 31 are skipped in months without those days"));
        T.True("days 28,30 Info", HasInfo(Compile("0 9 28,30 * *"), "day 30 is skipped"));
        T.True("day 28 gives no Info", !Of(Compile("0 9 28 * *"), CronSeverity.Info).Any());
        T.True("dom star has no short-month Info", !HasInfo(Compile("0 9 * * *"), "skipped"));
        T.True("L has no short-month Info", !Of(Compile("0 9 L * *"), CronSeverity.Info).Any());
        T.True("every day (1-31) has no short-month Info", !Of(Compile("0 9 1-31 * *"), CronSeverity.Info).Any());

        var line2 = Compile("0 8 * * *", "0 9 31 * *");
        T.True("Info line number is the line's", Of(line2, CronSeverity.Info).Any(d => d.Line == 2 && d.Message.StartsWith("line 2,", StringComparison.Ordinal)));

        var six = Compile("0 0 0 * * *");
        T.True("6-field seconds Info passes through", Of(six, CronSeverity.Info).Any(d => d.Field == "seconds"));
    }

    // [CP-A] A day of month that no selected month has used to compile to a trigger that never fires ("On the 30th of February").
    public static void NeverRunsIsAnError()
    {
        foreach (string cron in new[] { "0 9 30 2 *", "0 9 30,31 2 *", "0 9 31 4,6,9,11 *", "0 9 31 FEB *" })
        {
            var c = Compile(cron);
            T.True(cron + " fails", !c.Success);
            T.True(cron + " explains why", HasError(c, "this never runs, because none of the selected months has"));
        }
        T.True("message names the day", HasError(Compile("0 9 30 2 *"), "line 1, day-of-month: this never runs, because none of the selected months has day 30"));
        foreach (string cron in new[] { "0 9 29 2 *", "0 9 30,1 2 *", "0 9 L 2 *", "0 9 30 2 1", "0 9 31 4,5 *", "0 9 31 * *" })
            T.True(cron + " still compiles (some date exists, or the weekday adds runs)", Compile(cron).Success);
    }

    // ----- limits --------------------------------------------------------------------------------

    public static void FortyEightTriggerLimit()
    {
        var lines = Enumerable.Repeat("*/7 * * * *", 5).Append("5,20,40 */2 * * *").ToArray();   // 45 + 3
        var ok = Compile(lines);
        T.Eq("exactly 48 triggers succeeds", true, ok.Success);
        T.Eq("48 triggers", 48, ok.Triggers.Count);

        var over = Compile(lines.Append("0 0 * * *").ToArray());   // 49
        T.Eq("49 triggers fails", false, over.Success);
        T.True("message says needs 49, allows 48", HasError(over, "needs 49 triggers") && HasError(over, "allows 48"));
        T.Eq("failure has no triggers", 0, over.Triggers.Count);

        // A single line can need too many: 24 hours x 59 minutes of non-progressions listed as times.
        var one = Compile("0,1,3,7,12,20,33,50 1,2,4,5,9,10,13 * * *");   // 56 times, no progression
        T.Eq("one line with 56 times fails", false, one.Success);
        T.True("names the count", HasError(one, "needs 56 triggers"));

        var dayRules = Compile("0 9 13 * 5", "*/7 * * * *", "*/7 * * * *", "*/7 * * * *", "*/7 * * * *", "*/7 * * * *");
        T.Eq("OR doubles a line's triggers (2 + 45 = 47)", 47, dayRules.Triggers.Count);
        var orBlowUp = Compile("*/7 * 13 * 5", "0 0 * * *", "0 1 * * *");
        T.Eq("OR with strategy C (18 + 2)", 20, orBlowUp.Triggers.Count);
        var orOver = Compile("*/7 * 13 * 5", "*/7 * 13 * 5", "*/7 * 13 * 5", "0 1 * * *");   // 18 x 3 + 1
        T.Eq("OR groups multiply and break the limit", false, orOver.Success);
        T.True("... with the real count", HasError(orOver, "needs 55 triggers; Task Scheduler allows 48"));
    }

    public static void LineLimit()
    {
        var ten = Enumerable.Range(0, 10).Select(h => $"0 {h} * * *").ToArray();
        T.Eq("10 lines succeed", true, Compile(ten).Success);
        T.Eq("10 lines give 10 triggers", 10, Compile(ten).Triggers.Count);
        var eleven = Enumerable.Range(0, 11).Select(h => $"0 {h} * * *").ToArray();
        var c = Compile(eleven);
        T.Eq("11 lines fail", false, c.Success);
        T.True("11 lines message", HasError(c, "between 1 and 10 cron lines, but this one has 11"));
        var none = Compile();
        T.Eq("0 lines fail", false, none.Success);
        T.True("0 lines message", HasError(none, "but this one has 0"));
    }

    // ----- @logon, @reboot -----------------------------------------------------------------------

    public static void Logon()
    {
        var c = Compile("@logon");
        T.Eq("AtLogon", true, c.AtLogon);
        T.Eq("no groups", 0, c.Groups.Count);
        var t = c.Triggers[0] as LogonTrigger;
        T.True("is a LogonTrigger", t is not null);
        T.Eq("user id", @"TESTPC\tester", t?.UserId);
        T.Eq("no delay", (TimeSpan?)null, t?.Delay);
        T.Eq("case-insensitive", true, Compile("@LOGON").AtLogon);

        var twice = Compile("@logon", "@logon");
        T.Eq("two @logon lines give one trigger", 1, twice.Triggers.Count);

        var mixed = Compile("0 8 * * *", "@logon", "30 19 * * *");
        T.Eq("logon + times", 3, mixed.Triggers.Count);
        T.Eq("AtLogon with times", true, mixed.AtLogon);
        T.True("trigger order follows the lines", mixed.Triggers[0] is DailyTrigger && mixed.Triggers[1] is LogonTrigger && mixed.Triggers[2] is DailyTrigger);
        T.Eq("groups exclude logon", 2, mixed.Groups.Count);

        T.Eq("no logon without @logon", false, Compile("0 8 * * *").AtLogon);
    }

    public static void RebootAlternative()
    {
        var c = Compile("@reboot");
        T.Eq("@reboot fails", false, c.Success);
        T.True("@reboot Error mentions administrator rights", HasError(c, "administrator rights"));
        T.True("alternative is @logon", c.ClosestAlternative is { Count: 1 } a && a[0] == "@logon");
        T.True("explanation present", !string.IsNullOrEmpty(c.AlternativeExplanation) && c.AlternativeExplanation.Contains("@logon", StringComparison.Ordinal));
        T.Eq("@reboot not applied silently", 0, c.Triggers.Count);

        var withOther = Compile("@reboot", "0 8 * * *");
        T.True("other lines are kept in the alternative", withOther.ClosestAlternative is { Count: 2 } b && b[0] == "@logon" && b[1] == "0 8 * * *");

        var dup = Compile("@reboot", "@logon");
        T.True("duplicate @logon collapses", dup.ClosestAlternative is { Count: 1 });

        var bothBroken = Compile("@reboot", "*/90 * * * *");
        T.Eq("no alternative when it would still be invalid", (ValueList<string>?)null, bothBroken.ClosestAlternative);

        foreach (string bad in new[] { "*/90 * * * *", "0 9 15W * *", "0 9 * * 1#5", "hello", "0 22-2 * * *" })
        {
            var x = Compile(bad);
            T.Eq(bad + ": no ClosestAlternative", (ValueList<string>?)null, x.ClosestAlternative);
            T.Eq(bad + ": no AlternativeExplanation", (string?)null, x.AlternativeExplanation);
        }
        T.Eq("success has no alternative", (ValueList<string>?)null, Compile("0 8 * * *").ClosestAlternative);
    }

    // ----- plain English errors ------------------------------------------------------------------

    public static void ErrorsAreExplained()
    {
        T.True("seconds", HasError(Compile("*/30 * * * * *"), "seconds"));
        T.True("year", HasError(Compile("0 9 * * * 2027"), "year"));
        T.True("nearest weekday", HasError(Compile("0 9 15W * *"), "nearest weekday"));
        T.True("LW", HasError(Compile("0 9 LW * *"), "LW"));
        T.True("L-n", HasError(Compile("0 9 L-3 * *"), "month end"));
        T.True("5th weekday", HasError(Compile("0 9 * * 1#5"), "5th weekday"));
        T.True("step 90", HasError(Compile("*/90 * * * *"), "every 90 minutes"));
        T.True("range", HasError(Compile("61 * * * *"), "61 is out of range 0-59"));
        T.True("descending", HasError(Compile("0 22-2 * * *"), "descending range"));
        T.True("field count", HasError(Compile("hello"), "expected 5 fields"));
        T.True("a line with an error does not hide a later line's Info",
            HasInfo(Compile("hello", "0 9 31 * *"), "day 31"));
        T.Eq("a good line plus a bad line fails", false, Compile("0 8 * * *", "hello").Success);
    }

    // ----- boundaries, multi-line, determinism ---------------------------------------------------

    public static void StartBoundaryUsesAnchor()
    {
        var spec = new ScheduleSpec(ValueList.Of("30 17 * * 1-5", "*/15 * * * *"), HolidayCalendar.None, new DateOnly(2026, 3, 5));
        var c = CronCompiler.Compile(spec, Ctx);
        T.Eq("weekly start", new DateTime(2026, 3, 5, 17, 30, 0, DateTimeKind.Unspecified), c.Triggers[0].StartBoundary);
        T.Eq("repeat start", new DateTime(2026, 3, 5, 0, 0, 0, DateTimeKind.Unspecified), c.Triggers[1].StartBoundary);
        T.Eq("Kind unspecified", DateTimeKind.Unspecified, c.Triggers[0].StartBoundary.Kind);
        T.Eq("no offset", (DateTimeOffset?)null, c.Triggers[0].StartBoundaryOffset);
        T.Eq("no end boundary", (DateTime?)null, c.Triggers[0].EndBoundary);
        T.True("triggers are enabled", c.Triggers.All(t => t.Enabled));
        T.True("repetition never stops the running task at its end", c.Triggers[1].Repetition is { StopAtDurationEnd: false });
    }

    public static void MultiLineUnion()
    {
        var c = Compile("0 7 * * *", "30 19 * * *");
        T.Eq("union", "Daily(1) @07:00 | Daily(1) @19:30", Shapes(c));
        T.Eq("two groups", 2, c.Groups.Count);
        T.True("groups carry EveryDay + AtTimes", c.Groups.All(g => g.Days is EveryDay && g.Times is AtTimes { Times.Count: 1 }));
    }

    public static void GroupsAreTheIr()
    {
        var c = Compile("0 9 13 * 5");
        T.True("OR: MonthlyOnDays then WeeklyOn",
            c.Groups.Count == 2 && c.Groups[0].Days is MonthlyOnDays { LastDay: false } m && m.Days.Count == 1 && m.Days[0] == 13
            && c.Groups[1].Days is WeeklyOn { Days: WeekDays.Friday });
        var w = Compile("0 17 * * 5L");
        T.True("5L: MonthlyOnWeekdays Last", w.Groups[0].Days is MonthlyOnWeekdays { Days: WeekDays.Friday } mw && mw.Weeks.Count == 1 && mw.Weeks[0] == WeekOfMonth.Last);
        T.True("every day: EveryDay", Compile("0 9 * * *").Groups[0].Days is EveryDay);
    }

    public static void Deterministic()
    {
        var a = Compile("*/7 * 13 * 5", "0 9 * 1,4 1#1");
        var b = Compile("*/7 * 13 * 5", "0 9 * 1,4 1#1");
        T.True("same input, equal result (structural)", a == b);
        T.Eq("same hash", a.Triggers.GetHashCode(), b.Triggers.GetHashCode());
    }

    public static void WhitespaceAndCase()
    {
        T.Eq("extra spaces and lowercase names", "Weekly(1,Weekdays) @08:00", Shapes(Compile("  0   8  *  *  mon-fri  ")));
        T.Eq("macro @hourly", Shapes(Compile("0 * * * *")), Shapes(Compile("@hourly")));
        T.Eq("macro @daily", "Daily(1) @00:00", Shapes(Compile("@daily")));
        T.Eq("macro @weekly", "Weekly(1,Sunday) @00:00", Shapes(Compile("@weekly")));
        T.Eq("macro @monthly", "Monthly([1],last=False,All) @00:00", Shapes(Compile("@monthly")));
        T.Eq("macro @yearly", "Monthly([1],last=False,January) @00:00", Shapes(Compile("@yearly")));
    }
}
