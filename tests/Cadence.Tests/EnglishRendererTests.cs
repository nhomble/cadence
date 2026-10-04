using Cadence.Core.Cron;
using Cadence.Core.Model;
using Cadence.Core.Text;
using Cadence.TestHarness;

namespace Cadence.Tests;

public static class EnglishRendererTests
{
    static readonly DateOnly Anchor = new(2026, 9, 30);

    static string Describe(HolidayCalendar skip, params string[] lines) =>
        EnglishRenderer.Describe(new ScheduleSpec(new ValueList<string>(lines), skip, Anchor));

    // ----- every row of the PLAN.md 3.5 table (exact strings) -------------------------------------

    public static void Table35()
    {
        var N = HolidayCalendar.None;
        var rows = new (string[] Lines, HolidayCalendar Skip, string Text)[]
        {
            (["0 8 * * 1-5"], N, "Every weekday at 8:00"),
            (["30 17 * * MON-FRI"], N, "Every weekday at 17:30"),
            (["0 18 * * 1-5"], HolidayCalendar.Nyse, "Every weekday at 18:00, except NYSE market holidays"),
            (["0 18 * * 1-5"], HolidayCalendar.UsFederal, "Every weekday at 18:00, except US federal holidays"),
            (["0 * * * *"], N, "Every hour, on the hour"),
            (["@hourly"], N, "Every hour, on the hour"),
            (["*/15 * * * *"], N, "Every 15 minutes"),
            (["* * * * *"], N, "Every minute"),
            (["0 */2 * * *"], N, "Every 2 hours"),
            (["0 2 * * *"], N, "Every day at 2:00"),
            (["@daily"], N, "Every day at 0:00"),
            (["0 6 * * 0-6"], N, "Every day at 6:00"),
            (["0 9 * * 1"], N, "Every Monday at 9:00"),
            (["0 7,19 * * 1,3,5"], N, "Every Monday, Wednesday and Friday at 7:00 and 19:00"),
            (["0 10 * * 0,6"], N, "Every Saturday and Sunday at 10:00"),
            (["0 9-17 * * 1-5"], N, "Every hour from 9:00 to 17:00, on weekdays"),
            (["*/30 8-17 * * *"], N, "Every 30 minutes from 8:00 to 17:30"),
            (["0,30 9-17 * * *"], N, "Every 30 minutes from 9:00 to 17:30"),
            (["15 8-22/3 * * *"], N, "Every 3 hours from 8:15 to 20:15"),
            (["1-59/2 * * * *"], N, "Every 2 minutes from 0:01 to 23:59"),
            (["*/7 * * * *"], N, "Every hour at :00, :07, :14, :21, :28, :35, :42, :49 and :56"),
            (["5,20,40 */2 * * *"], N, "Every 2 hours from 0:05 to 22:05; and every 2 hours from 0:20 to 22:20; and every 2 hours from 0:40 to 22:40"),
            (["0 23 L * *"], N, "On the last day of every month at 23:00"),
            (["0 9 1,15 * *"], N, "On the 1st and 15th of every month at 9:00"),
            (["0 9 31 * *"], N, "On the 31st of every month at 9:00 (skipped in months with fewer days)"),
            (["0 0 29 2 *"], N, "On 29 February at 0:00 (only in leap years)"),
            (["0 9 * 1,7 *"], N, "Every day in January and July at 9:00"),
            (["0 17 * * 5L"], N, "On the last Friday of every month at 17:00"),
            (["0 9 * * 1#1"], N, "On the first Monday of every month at 9:00"),
            (["0 9 * 1,4,7,10 1#1"], N, "On the first Monday of January, April, July and October at 9:00"),
            (["0 9 1 */3 *"], N, "On the 1st of January, April, July and October at 9:00"),
            (["0 9 13 * 5"], N, "On the 13th of every month at 9:00; and every Friday at 9:00"),
            (["0 7 * * *", "30 19 * * *"], N, "Every day at 7:00; and every day at 19:30"),
            (["@logon"], N, "When you sign in"),
        };
        foreach (var (lines, skip, text) in rows)
        {
            T.Eq(string.Join(" + ", lines) + " [" + skip + "]", text, Describe(skip, lines));
        }
        T.True("table has at least 34 rows", rows.Length >= 34);
    }

    // ----- cases the plan table does not list (the plainest wording; recorded in PLAN.md section 11) ---

    public static void ExtraWording()
    {
        var N = HolidayCalendar.None;
        var rows = new (string[] Lines, HolidayCalendar Skip, string Text)[]
        {
            (["0 0 * * *"], N, "Every day at 0:00"),
            (["5 9 * * *"], N, "Every day at 9:05"),
            (["0 7,12,19 * * *"], N, "Every day at 7:00, 12:00 and 19:00"),
            (["0 7 * * 1,3,5"], N, "Every Monday, Wednesday and Friday at 7:00"),
            (["0 9 * * 6,0"], N, "Every Saturday and Sunday at 9:00"),
            (["0 9 * * 1-6"], N, "Every Monday, Tuesday, Wednesday, Thursday, Friday and Saturday at 9:00"),
            (["15 * * * *"], N, "Every hour at :15"),
            (["*/15 * * * 1-5"], N, "Every 15 minutes, on weekdays"),
            (["*/15 9-17 * * 1-5"], N, "Every 15 minutes from 9:00 to 17:45, on weekdays"),
            (["*/10 * * * 0,6"], N, "Every 10 minutes, on Saturday and Sunday"),
            (["*/5 * * * 1"], N, "Every 5 minutes, on Monday"),
            (["0 */3 * * 1,3,5"], N, "Every 3 hours, on Monday, Wednesday and Friday"),
            (["15 */3 * * *"], N, "Every 3 hours from 0:15 to 21:15"),
            (["*/90 * * * *"], N, "Invalid schedule"),
            (["*/7 * * * 1-5"], N, "Every hour at :00, :07, :14, :21, :28, :35, :42, :49 and :56, on weekdays"),
            (["0,7 9-17 * * *"], N, "Every hour from 9:00 to 17:59 at :00 and :07"),
            (["0 9 15 * *"], N, "On the 15th of every month at 9:00"),
            (["0 9 2,3,11,12,13,21,22,23 * *"], N, "On the 2nd, 3rd, 11th, 12th, 13th, 21st, 22nd and 23rd of every month at 9:00"),
            (["0 9 1,15,L * *"], N, "On the 1st, 15th and last day of every month at 9:00"),
            (["0 9 29 * *"], N, "On the 29th of every month at 9:00 (skipped in February, except in leap years)"),
            (["0 9 30 * *"], N, "On the 30th of every month at 9:00 (skipped in months with fewer days)"),
            (["0 9 31 1,3 *"], N, "On the 31st of January and March at 9:00"),
            (["0 9 * 3 *"], N, "Every day in March at 9:00"),
            (["0 9 * 1-3 1-5"], N, "Every weekday in January, February and March at 9:00"),
            (["0 9 * 1,7 1"], N, "Every Monday in January and July at 9:00"),
            (["*/30 * * 1,7 *"], N, "Every 30 minutes, in January and July"),
            (["0 9 * * 1#1,1#3"], N, "On the first and third Monday of every month at 9:00"),
            (["0 9 * * 1#2,5#2"], N, "On the second Monday and Friday of every month at 9:00"),
            (["0 17 * * 0L"], N, "On the last Sunday of every month at 17:00"),
            (["0 9 * * 1#1,1L"], N, "On the first and last Monday of every month at 9:00"),
            (["0 9 13 * 1#1"], N, "On the 13th of every month at 9:00; and on the first Monday of every month at 9:00"),
            (["0 9,17 13 * 5"], N, "On the 13th of every month at 9:00 and 17:00; and every Friday at 9:00 and 17:00"),
            (["@logon", "0 7 * * *"], HolidayCalendar.UsFederal, "When you sign in; and every day at 7:00, except US federal holidays"),
            (["@logon"], HolidayCalendar.Nyse, "When you sign in, except NYSE market holidays"),
            (["@weekly"], N, "Every Sunday at 0:00"),
            (["@monthly"], N, "On the 1st of every month at 0:00"),
            (["@yearly"], N, "On the 1st of January at 0:00"),
            (["hello"], N, "Invalid schedule"),
            (["@reboot"], N, "Invalid schedule"),
        };
        foreach (var (lines, skip, text) in rows)
        {
            T.Eq(string.Join(" + ", lines) + " [" + skip + "]", text, Describe(skip, lines));
        }
    }

    public static void NeverShowsCronSyntax()
    {
        var crons = new[]
        {
            "0 8 * * 1-5", "*/15 * * * *", "0 9 13 * 5", "0 17 * * 5L", "5,20,40 */2 * * *", "0 9 * 1,7 *", "0-58 * * * 1", "@logon",
            "15 8-22/3 * * *", "0 9 * * 1#1", "1-59/2 * * * *", "0 23 L * *",
        };
        foreach (string cron in crons)
        {
            string text = Describe(HolidayCalendar.None, cron);
            bool clean = !text.Contains('*', StringComparison.Ordinal) && !text.Contains('/', StringComparison.Ordinal)
                && !text.Contains('#', StringComparison.Ordinal) && !text.Contains('@', StringComparison.Ordinal)
                && text.Length > 0;
            T.True("no cron syntax: " + cron, clean, text);
        }
    }

    public static void RendersFromCompiledSchedule()
    {
        var spec = new ScheduleSpec(ValueList.Of("0 18 * * 1-5"), HolidayCalendar.Nyse, Anchor);
        var compiled = CronCompiler.Compile(spec, new MapContext(@"TESTPC\tester"));
        T.Eq("compiled overload", "Every weekday at 18:00, except NYSE market holidays", EnglishRenderer.Describe(compiled, HolidayCalendar.Nyse));
        T.Eq("compiled overload, no skip", "Every weekday at 18:00", EnglishRenderer.Describe(compiled, HolidayCalendar.None));
        var bad = CronCompiler.Compile(new ScheduleSpec(ValueList.Of("61 * * * *"), HolidayCalendar.None, Anchor), new MapContext("u"));
        T.Eq("failed compile", "Invalid schedule", EnglishRenderer.Describe(bad, HolidayCalendar.Nyse));
    }

    public static void OrdinalsTable()
    {
        var rows = new (int N, string Text)[]
        {
            (1, "1st"), (2, "2nd"), (3, "3rd"), (4, "4th"), (10, "10th"), (11, "11th"), (12, "12th"), (13, "13th"),
            (14, "14th"), (20, "20th"), (21, "21st"), (22, "22nd"), (23, "23rd"), (24, "24th"), (30, "30th"), (31, "31st"),
        };
        foreach (var (n, text) in rows) T.Eq("ordinal " + n, text, Ordinals.Of(n));
        T.Eq("join 0", "", Ordinals.JoinAnd([]));
        T.Eq("join 1", "a", Ordinals.JoinAnd(["a"]));
        T.Eq("join 2", "a and b", Ordinals.JoinAnd(["a", "b"]));
        T.Eq("join 3", "a, b and c", Ordinals.JoinAnd(["a", "b", "c"]));
        T.Eq("week word last", "last", Ordinals.WeekWord(WeekOfMonth.Last));
        T.Eq("week word second", "second", Ordinals.WeekWord(WeekOfMonth.Second));
    }
}
