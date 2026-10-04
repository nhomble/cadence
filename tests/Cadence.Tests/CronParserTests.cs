using Cadence.Core.Cron;
using Cadence.Core.Model;
using Cadence.TestHarness;

namespace Cadence.Tests;

public static class CronParserTests
{
    // ----- helpers -----

    static (CronExpr? Expr, List<CronDiagnostic> Diags) Run(string line, int lineNo = 1)
    {
        var diags = new List<CronDiagnostic>();
        var expr = CronSyntax.Parse(line, lineNo, diags);
        return (expr, diags);
    }

    static string Set(IEnumerable<int> values)
    {
        var v = values.ToList();
        var parts = new List<string>();
        for (int i = 0; i < v.Count;)
        {
            int j = i;
            while (j + 1 < v.Count && v[j + 1] == v[j] + 1) j++;
            if (j - i >= 2) parts.Add($"{v[i]}-{v[j]}");
            else for (int k = i; k <= j; k++) parts.Add(v[k].ToString(System.Globalization.CultureInfo.InvariantCulture));
            i = j + 1;
        }
        return string.Join(",", parts);
    }

    // Compact, order-stable rendering of a CronExpr for table-driven comparison.
    static string Fmt(CronExpr e)
    {
        if (e.AtLogon) return "@logon";
        string dom = e.Dom.Star ? "*" : Set(e.Dom.Days) + (e.Dom.Last ? (e.Dom.Days.Count > 0 ? ",L" : "L") : "");
        var months = new List<int>();
        for (int m = 1; m <= 12; m++) if (((int)e.Months & (1 << (m - 1))) != 0) months.Add(m);
        string mon = e.Months == MonthSet.All ? "*" : Set(months);
        string dow;
        if (e.Dow.Star) dow = "*";
        else
        {
            var days = new List<int>();
            for (int d = 0; d < 7; d++) if (((int)e.Dow.Days & (1 << d)) != 0) days.Add(d);
            var parts = new List<string>();
            if (days.Count > 0) parts.Add(Set(days));
            foreach (var n in e.Dow.Nth)
                parts.Add(n.Week == WeekOfMonth.Last ? $"{(int)n.Day}L" : $"{(int)n.Day}#{(int)n.Week}");
            dow = string.Join(",", parts);
        }
        return $"m={Set(e.Minutes)} h={Set(e.Hours)} dom={dom} mon={mon} dow={dow}";
    }

    static string Join(IEnumerable<int> v) => string.Join(",", v);

    static void ExpectOk(string line, string expected)
    {
        var (expr, diags) = Run(line);
        T.True($"[{line}] succeeds", expr is not null && diags.All(d => d.Severity != CronSeverity.Error),
            string.Join(" | ", diags.Select(d => d.Message)));
        if (expr is not null) T.Eq($"[{line}] value", expected, Fmt(expr));
    }

    static void ExpectError(string line, string field, params string[] mentions)
    {
        var (expr, diags) = Run(line);
        T.True($"[{line}] fails", expr is null);
        var err = diags.FirstOrDefault(d => d.Severity == CronSeverity.Error && d.Field == field);
        T.True($"[{line}] Error on field '{field}'", err is not null, string.Join(" | ", diags.Select(d => d.Field + ": " + d.Message)));
        if (err is null) return;
        T.True($"[{line}] message starts with line number", err.Message.StartsWith("line 1, " + field + ": ", StringComparison.Ordinal), err.Message);
        foreach (string m in mentions)
            T.True($"[{line}] message mentions '{m}'", err.Message.Contains(m, StringComparison.Ordinal), err.Message);
    }

    // ----- section 3.6 rows: successes -----

    public static void Table36_Successes()
    {
        string odd = Join(Enumerable.Range(0, 30).Select(i => 1 + 2 * i));
        var rows = new (string Line, string Expected)[]
        {
            ("0 8 * * 1-5", "m=0 h=8 dom=* mon=* dow=1-5"),
            ("30 17 * * MON-FRI", "m=30 h=17 dom=* mon=* dow=1-5"),
            ("0 * * * *", "m=0 h=0-23 dom=* mon=* dow=*"),
            ("*/15 * * * *", "m=0,15,30,45 h=0-23 dom=* mon=* dow=*"),
            ("* * * * *", "m=0-59 h=0-23 dom=* mon=* dow=*"),
            ("0 9-17 * * 1-5", "m=0 h=9-17 dom=* mon=* dow=1-5"),
            ("1-59/2 * * * *", $"m={odd} h=0-23 dom=* mon=* dow=*"),
            ("*/7 * * * *", "m=0,7,14,21,28,35,42,49,56 h=0-23 dom=* mon=* dow=*"),
            ("5,20,40 */2 * * *", "m=5,20,40 h=0,2,4,6,8,10,12,14,16,18,20,22 dom=* mon=* dow=*"),
            ("0-58 * * * 1", "m=0-58 h=0-23 dom=* mon=* dow=1"),
            ("0 0 L * *", "m=0 h=0 dom=L mon=* dow=*"),
            ("0 17 * * 5L", "m=0 h=17 dom=* mon=* dow=5L"),
            ("0 9 13 * 5", "m=0 h=9 dom=13 mon=* dow=5"),
            ("0 0 * * 0,7", "m=0 h=0 dom=* mon=* dow=0"),
            ("0 0 29 2 *", "m=0 h=0 dom=29 mon=2 dow=*"),
            ("0 9 * 1,7 *", "m=0 h=9 dom=* mon=1,7 dow=*"),
            ("0 7 * * *", "m=0 h=7 dom=* mon=* dow=*"),
            ("30 19 * * *", "m=30 h=19 dom=* mon=* dow=*"),
            ("@logon", "@logon"),
            ("0 0 0 * * *", "m=0 h=0 dom=* mon=* dow=*"),
        };
        foreach (var (line, expected) in rows) ExpectOk(line, expected);
    }

    public static void Table36_SixFieldIsNormalizedWithInfo()
    {
        var (expr, diags) = Run("0 0 0 * * *");
        T.True("parsed", expr is not null);
        var info = diags.SingleOrDefault();
        T.True("exactly one diagnostic", info is not null);
        T.Eq("severity Info", CronSeverity.Info, info!.Severity);
        T.Eq("field seconds", "seconds", info.Field);
        T.True("mentions the normalized line", info.Message.Contains("0 0 * * *", StringComparison.Ordinal), info.Message);
    }

    public static void Table36_LogonFlags()
    {
        var (expr, diags) = Run("@logon");
        T.True("@logon parses", expr is not null && diags.Count == 0);
        T.True("AtLogon true", expr!.AtLogon);
        var (plain, _) = Run("0 8 * * *");
        T.True("plain line is not AtLogon", !plain!.AtLogon);
    }

    // ----- section 3.6 rows: errors -----

    public static void Table36_Errors()
    {
        ExpectError("@reboot", "macro", "@reboot", "administrator", "@logon");
        ExpectError("*/30 * * * * *", "seconds", "*/30", "second");
        ExpectError("0 9 15W * *", "day-of-month", "15W", "nearest weekday");
        ExpectError("0 9 LW * *", "day-of-month", "LW", "nearest weekday");
        ExpectError("0 9 * * 1#5", "day-of-week", "1#5", "5th weekday", "1L");
        ExpectError("*/90 * * * *", "minute", "*/90", "90", "every 90 minutes");
        ExpectError("61 * * * *", "minute", "61", "0-59");
        ExpectError("0 22-2 * * *", "hour", "22-2", "descending");
        ExpectError("0 9 * * * 2027", "year", "2027");
        ExpectError("hello", "line", "5 fields", "hello");
    }

    public static void Table36_ErrorsAreSeverityErrorWithNoExpr()
    {
        foreach (string line in new[] { "@reboot", "*/30 * * * * *", "0 9 15W * *", "0 9 LW * *", "0 9 * * 1#5", "*/90 * * * *",
                     "61 * * * *", "0 22-2 * * *", "0 9 * * * 2027", "hello" })
        {
            var (expr, diags) = Run(line);
            T.True($"[{line}] null expr", expr is null);
            T.True($"[{line}] has an Error diagnostic", diags.Any(d => d.Severity == CronSeverity.Error));
        }
    }

    public static void Table36_ExactMessageFromPlan()
    {
        var (_, diags) = Run("0 9 15W * *");
        T.Eq("exact text", "line 1, day-of-month: \"15W\" means \"nearest weekday\", which Task Scheduler can't do", diags.Single().Message);
        T.Eq("Line property", 1, diags.Single().Line);
    }

    public static void Table36_FortyNineLinesAllParse()
    {
        // The 48-trigger limit belongs to the compiler; the parser must accept each of the 49 lines.
        int ok = 0;
        var seen = new HashSet<string>();
        for (int i = 0; i < 49; i++)
        {
            var (expr, diags) = Run($"{i} 0 1 1 *", i + 1);
            if (expr is not null && diags.Count == 0) ok++;
            if (expr is not null) seen.Add(Fmt(expr));
        }
        T.Eq("all 49 lines parse", 49, ok);
        T.Eq("49 distinct expressions", 49, seen.Count);
    }

    // ----- extra edge cases -----

    public static void Edge_NamesInRangesAndLists()
    {
        ExpectOk("0 0 * JAN-MAR *", "m=0 h=0 dom=* mon=1-3 dow=*");
        ExpectOk("0 0 * JAN,JUN,DEC *", "m=0 h=0 dom=* mon=1,6,12 dow=*");
        ExpectOk("0 0 * 3-MAY *", "m=0 h=0 dom=* mon=3-5 dow=*");
        ExpectOk("0 0 * * MON,WED,FRI", "m=0 h=0 dom=* mon=* dow=1,3,5");
        ExpectOk("0 0 * * TUE-THU", "m=0 h=0 dom=* mon=* dow=2-4");
        ExpectOk("0 0 * * FRI-SUN", "m=0 h=0 dom=* mon=* dow=0,5,6");
        ExpectOk("0 0 * * SAT-SUN", "m=0 h=0 dom=* mon=* dow=0,6");
    }

    public static void Edge_LowercaseAndMixedCase()
    {
        ExpectOk("0 0 * jan-mar mon-fri", "m=0 h=0 dom=* mon=1-3 dow=1-5");
        ExpectOk("0 0 * Feb Sun", "m=0 h=0 dom=* mon=2 dow=0");
        ExpectOk("0 17 * * fril", "m=0 h=17 dom=* mon=* dow=5L");
        ExpectOk("0 0 l * *", "m=0 h=0 dom=L mon=* dow=*");
        ExpectOk("@DAILY", "m=0 h=0 dom=* mon=* dow=*");
        ExpectOk("@LogOn", "@logon");
    }

    public static void Edge_SundayIsZeroOrSeven()
    {
        ExpectOk("0 0 * * 7", "m=0 h=0 dom=* mon=* dow=0");
        ExpectOk("0 0 * * 0", "m=0 h=0 dom=* mon=* dow=0");
        ExpectOk("0 0 * * 0,7", "m=0 h=0 dom=* mon=* dow=0");
        ExpectOk("0 0 * * 5-7", "m=0 h=0 dom=* mon=* dow=0,5,6");
        ExpectOk("0 0 * * 0-7", "m=0 h=0 dom=* mon=* dow=0-6");
        ExpectOk("0 0 * * 7L", "m=0 h=0 dom=* mon=* dow=0L");
        ExpectOk("0 0 * * SUN#1", "m=0 h=0 dom=* mon=* dow=0#1");
        T.True("7 and 0 produce equal expressions", Run("0 0 * * 7").Expr == Run("0 0 * * 0").Expr);
    }

    public static void Edge_StepsStartAtTheRightPlace()
    {
        ExpectOk("0 0 */10 * *", "m=0 h=0 dom=1,11,21,31 mon=* dow=*");     // day of month starts at 1
        ExpectOk("0 0 * */3 *", "m=0 h=0 dom=* mon=1,4,7,10 dow=*");          // month starts at 1
        ExpectOk("0 */6 * * *", "m=0 h=0,6,12,18 dom=* mon=* dow=*");         // hour starts at 0
        ExpectOk("*/20 0 * * *", "m=0,20,40 h=0 dom=* mon=* dow=*");          // minute starts at 0
        ExpectOk("0 0 * * */2", "m=0 h=0 dom=* mon=* dow=0,2,4,6");           // day of week starts at 0
        ExpectOk("0 0 * * */3", "m=0 h=0 dom=* mon=* dow=0,3,6");
    }

    public static void Edge_StartWithStep()
    {
        ExpectOk("5/20 * * * *", "m=5,25,45 h=0-23 dom=* mon=* dow=*");       // a/s runs to the field maximum
        ExpectOk("0 2/6 * * *", "m=0 h=2,8,14,20 dom=* mon=* dow=*");
        ExpectOk("0 0 5/10 * *", "m=0 h=0 dom=5,15,25 mon=* dow=*");
        ExpectOk("0 0 * 2/5 *", "m=0 h=0 dom=* mon=2,7,12 dow=*");
        ExpectOk("10-30/10 * * * *", "m=10,20,30 h=0-23 dom=* mon=* dow=*");
        ExpectOk("0 0 * * MON-FRI/2", "m=0 h=0 dom=* mon=* dow=1,3,5");
    }

    public static void Edge_Whitespace()
    {
        ExpectOk("  0   8 \t * *   1-5  ", "m=0 h=8 dom=* mon=* dow=1-5");
        ExpectOk("\t30\t17\t*\t*\tMON-FRI", "m=30 h=17 dom=* mon=* dow=1-5");
        ExpectOk("  @daily  ", "m=0 h=0 dom=* mon=* dow=*");
        ExpectOk(" 0   0   0 *  * *", "m=0 h=0 dom=* mon=* dow=*");
    }

    public static void Edge_QuestionMarkMeansStar()
    {
        var (expr, _) = Run("0 0 ? * ?");
        T.True("parses", expr is not null);
        T.True("dom is star", expr!.Dom.Star);
        T.True("dow is star", expr.Dow.Star);
        T.True("same as stars", expr == Run("0 0 * * *").Expr);
        ExpectOk("0 0 ? * 1", "m=0 h=0 dom=* mon=* dow=1");
    }

    public static void Edge_ListsAreSortedAndDistinct()
    {
        ExpectOk("40,5,20,5 8 * * *", "m=5,20,40 h=8 dom=* mon=* dow=*");
        ExpectOk("0 0 15,1,L,15 * *", "m=0 h=0 dom=1,15,L mon=* dow=*");
        ExpectOk("0 0 * * 5,1,MON", "m=0 h=0 dom=* mon=* dow=1,5");
        ExpectOk("0 0 * 12,1,JAN *", "m=0 h=0 dom=* mon=1,12 dow=*");
    }

    public static void Edge_DomAndDowExtensions()
    {
        ExpectOk("0 9 1,15,L * *", "m=0 h=9 dom=1,15,L mon=* dow=*");
        ExpectOk("0 9 * * 1#1", "m=0 h=9 dom=* mon=* dow=1#1");
        ExpectOk("0 9 * * FRI#4", "m=0 h=9 dom=* mon=* dow=5#4");
        ExpectOk("0 9 * * 1-5,5L", "m=0 h=9 dom=* mon=* dow=1-5,5L");
        ExpectOk("0 9 * * 1#2,1#4", "m=0 h=9 dom=* mon=* dow=1#2,1#4");
        var (expr, _) = Run("0 17 * * 5L");
        T.True("5L has no plain days", expr!.Dow.Days == WeekDays.None);
        T.Eq("5L is the last Friday", new NthWeekday(WeekOfMonth.Last, DayOfWeek.Friday), expr.Dow.Nth.Single());
        T.True("5L dow is not star", !expr.Dow.Star);
    }

    public static void Edge_StarFlagsAndFullSets()
    {
        var (expr, _) = Run("0 0 * * *");
        T.True("dom star", expr!.Dom.Star);
        T.Eq("star dom holds 1-31", 31, expr.Dom.Days.Count);
        T.True("dow star", expr.Dow.Star);
        T.True("star dow holds all days", expr.Dow.Days == WeekDays.All);
        T.True("months all", expr.Months == MonthSet.All);
        var (explicitAll, _) = Run("0 0 1-31 * *");
        T.True("explicit 1-31 is not star", !explicitAll!.Dom.Star);
    }

    public static void Edge_MacrosExpandToTheirLines()
    {
        var table = new (string Macro, string Equivalent)[]
        {
            ("@hourly", "0 * * * *"), ("@daily", "0 0 * * *"), ("@midnight", "0 0 * * *"), ("@weekly", "0 0 * * 0"),
            ("@monthly", "0 0 1 * *"), ("@yearly", "0 0 1 1 *"), ("@annually", "0 0 1 1 *"),
        };
        foreach (var (macro, equivalent) in table)
        {
            var (m, md) = Run(macro);
            T.True($"{macro} parses cleanly", m is not null && md.Count == 0);
            T.True($"{macro} equals '{equivalent}'", m == Run(equivalent).Expr);
        }
    }

    public static void Edge_MacroErrors()
    {
        ExpectError("@sometimes", "macro", "@sometimes");
        ExpectError("@daily extra", "macro", "@daily", "extra");
        ExpectError("@REBOOT", "macro", "administrator");
    }

    public static void Edge_SixFieldWithSeconds()
    {
        ExpectOk("0 */5 * * * *", "m=0,5,10,15,20,25,30,35,40,45,50,55 h=0-23 dom=* mon=* dow=*");
        ExpectOk("00 30 8 * * 1-5", "m=30 h=8 dom=* mon=* dow=1-5");
        ExpectError("15 0 0 * * *", "seconds", "15");
        ExpectError("* 0 0 * * *", "seconds", "\"*\"");
        ExpectError("0/10 * * * * *", "seconds", "0/10");
    }

    public static void Edge_FieldCountErrors()
    {
        ExpectError("0 0 0 0 0 0 0", "seconds/year", "7 fields");
        ExpectError("0 0 * *", "line", "5 fields", "found 4");
        ExpectError("", "line", "empty");
        ExpectError("   ", "line", "empty");
        ExpectError("0 0 0 0 0 0 0 0", "line", "found 8");
        ExpectError("* * * * * 2027-2030", "year", "2027-2030");
    }

    public static void Edge_RangeAndStepErrors()
    {
        ExpectError("0 24 * * *", "hour", "24", "0-23");
        ExpectError("60 * * * *", "minute", "60", "0-59");
        ExpectError("0 0 0 * *", "day-of-month", "0", "1-31");
        ExpectError("0 0 32 * *", "day-of-month", "32");
        ExpectError("0 0 * 13 *", "month", "13", "1-12");
        ExpectError("0 0 * 0 *", "month", "0");
        ExpectError("0 0 * * 8", "day-of-week", "8", "0-7");
        ExpectError("*/0 * * * *", "minute", "*/0", "0");
        ExpectError("*/60 * * * *", "minute", "*/60");
        ExpectError("0 */24 * * *", "hour", "*/24", "every 24 hours");
        ExpectError("0 0 * * */7", "day-of-week", "*/7");
        ExpectError("*/x * * * *", "minute", "*/x");
        ExpectError("*/ * * * *", "minute", "*/");
        ExpectError("5-1 * * * *", "minute", "5-1", "descending", "5-59,0-1");
        ExpectError("0 0 * * FRI-MON", "day-of-week", "FRI-MON", "descending");
        ExpectError("0 0 * 12-JAN *", "month", "12-JAN", "descending");
        ExpectError("1- * * * *", "minute", "1-");
        ExpectError("-5 * * * *", "minute", "-5");
        ExpectError("1-2-3 * * * *", "minute", "1-2-3");
    }

    public static void Edge_TokenErrors()
    {
        ExpectError("abc * * * *", "minute", "ABC");
        ExpectError("0 0 * FOO *", "month", "FOO", "JAN-DEC");
        ExpectError("0 0 * * MONDAY", "day-of-week", "MONDAY", "SUN-SAT");
        ExpectError("1,,2 * * * *", "minute", "empty entry");
        ExpectError("1, * * * *", "minute", "empty entry");
        ExpectError("MON * * * *", "minute", "MON");
        ExpectError("0 0 * * -", "day-of-week", "\"-\"");
        ExpectError("99999999999 * * * *", "minute", "out of range");
    }

    public static void Edge_ExtensionsInTheWrongField()
    {
        ExpectError("L * * * *", "minute", "L");
        ExpectError("0 L * * *", "hour", "L");
        ExpectError("0 0 * L *", "month", "L");
        ExpectError("0 0 * * L", "day-of-week", "only works in day-of-month", "5L");
        ExpectError("0 0 5L * *", "day-of-month", "5L", "day-of-week");
        ExpectError("0 0 1#2 * *", "day-of-month", "1#2", "day-of-week");
        ExpectError("0 0 * * 1-5L", "day-of-week", "1-5L");
        ExpectError("0 0 * * */2L", "day-of-week", "*/2L");
        ExpectError("0 0 * * 1#0", "day-of-week", "1#0", "1-4");
        ExpectError("0 0 * * 1#6", "day-of-week", "1#6", "1-4");
        ExpectError("0 0 * * 1#x", "day-of-week", "1#x");
        ExpectError("0 0 * * 9L", "day-of-week", "9", "0-7");
        ExpectError("0 0 * * FRI#5", "day-of-week", "FRI#5", "FRIL");
    }

    public static void Edge_DayOfMonthUnsupportedForms()
    {
        ExpectError("0 9 L-3 * *", "day-of-month", "L-3", "end of the month", "can't do");
        ExpectError("0 9 1W * *", "day-of-month", "1W", "nearest weekday");
        ExpectError("0 9 lw * *", "day-of-month", "lw", "nearest weekday");
        ExpectError("0 9 1,15W * *", "day-of-month", "15W");
    }

    public static void Edge_AllFieldErrorsAreReported()
    {
        var (expr, diags) = Run("61 25 * * *");
        T.True("fails", expr is null);
        T.True("minute error present", diags.Any(d => d.Field == "minute" && d.Severity == CronSeverity.Error));
        T.True("hour error present", diags.Any(d => d.Field == "hour" && d.Severity == CronSeverity.Error));
        T.Eq("two diagnostics", 2, diags.Count);
    }

    public static void Edge_LineNumberIsReported()
    {
        var (expr, diags) = Run("0 9 15W * *", 7);
        T.True("fails", expr is null);
        T.Eq("Line property", 7, diags.Single().Line);
        T.True("message names line 7", diags.Single().Message.StartsWith("line 7, day-of-month: ", StringComparison.Ordinal), diags.Single().Message);
        var (_, infoDiags) = Run("0 0 0 * * *", 3);
        T.True("info message names line 3", infoDiags.Single().Message.StartsWith("line 3, ", StringComparison.Ordinal));
    }

    public static void Edge_ParserAppendsToExistingDiagnostics()
    {
        var diags = new List<CronDiagnostic>();
        CronSyntax.Parse("0 9 15W * *", 1, diags);
        CronSyntax.Parse("61 * * * *", 2, diags);
        var ok = CronSyntax.Parse("0 8 * * *", 3, diags);
        T.Eq("two errors accumulated", 2, diags.Count);
        T.Eq("second error is for line 2", 2, diags[1].Line);
        T.True("a good line still parses", ok is not null);
    }

    public static void Edge_ExpressionsHaveStructuralEquality()
    {
        T.True("names equal numbers", Run("30 17 * * MON-FRI").Expr == Run("30 17 * * 1,2,3,4,5").Expr);
        T.True("different lines differ", Run("0 8 * * *").Expr != Run("0 9 * * *").Expr);
        T.True("whitespace is irrelevant", Run("0   8 * *  *").Expr == Run("0 8 * * *").Expr);
    }
}
