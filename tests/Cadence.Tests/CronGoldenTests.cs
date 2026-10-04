using System.Globalization;
using System.Text;
using Cadence.Core.Cron;
using Cadence.Core.Model;
using Cadence.Core.Scheduling;
using Cadence.Core.Text;
using Cadence.TestHarness;

namespace Cadence.Tests;

/// <summary>
/// The cron behaviour of Cadence as a golden (docs\REFACTOR-modules.md, Phase 4): for a broad corpus (every template, the renderer table, the edge cases L, #, nL, steps
/// of 0, minute 60, @reboot, TZ= prefixes, the 48-trigger cap, malformed lines and a seeded random set) it records what the parser says (the parsed line and every
/// diagnostic, word for word), what the compiler makes of it (groups, triggers, diagnostics, closest alternative), the English text, the Simple-view mapping, the next
/// runs and what the matcher says for 113 dates and 21 minutes. The golden in Golden\cron-behaviour.txt was captured from the code BEFORE the parser and the matcher moved
/// into Cadence.Cron; it must stay byte-identical.
/// </summary>
public static class CronGoldenTests
{
    static readonly TimeZoneInfo Est = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
    static readonly MapContext Ctx = new(@"TESTPC\tester");
    static readonly DateOnly Anchor = new(2026, 9, 15);

    // ---- the corpus ----

    static readonly string[][] Fixed =
    [
        ["0 8 * * 1-5"], ["30 17 * * MON-FRI"], ["0 * * * *"], ["*/15 * * * *"], ["* * * * *"],
        ["0 9-17 * * 1-5"], ["1-59/2 * * * *"], ["*/7 * * * *"], ["5,20,40 */2 * * *"], ["0-58 * * * 1"],
        ["0 0 L * *"], ["0 17 * * 5L"], ["0 9 13 * 5"], ["0 0 * * 0,7"], ["0 0 29 2 *"], ["0 9 * 1,7 *"],
        ["0 7 * * *", "30 19 * * *"], ["@logon"], ["0 0 0 * * *"],
        ["@hourly"], ["@daily"], ["@midnight"], ["@weekly"], ["@monthly"], ["@yearly"], ["@annually"], ["@LOGON"], ["@reboot"], ["@REBOOT"], ["@nope"], ["@daily extra"], ["@logon @logon"],
        ["0 */2 * * *"], ["0 6 * * 0-6"], ["0 7,19 * * 1,3,5"], ["0 10 * * 0,6"], ["*/30 8-17 * * *"], ["0,30 9-17 * * *"], ["15 8-22/3 * * *"],
        ["0 9 1,15 * *"], ["0 9 31 * *"], ["0 9 * * 1#1"], ["0 9 * 1,4,7,10 1#1"], ["0 9 1 */3 *"],
        ["0 23 L * *"], ["0 9 30 * *"], ["0 9 29-31 * *"], ["0 9 * * 1,5L"], ["0 9 * * 1#1,2#2"], ["0 9 * * 1#1,1#3"],
        ["0 9 * 11,12 1-5"], ["0 9 L 11 1"], ["0 9 1-31 * 1"], ["0 9 * * 1,1L"], ["0 0,8,16 * * *"], ["0 1,2,4 * * *"],
        ["0,30 7,19 * * *"], ["0-10/5 1,2,4 * * *"], ["*/7 * 13 * 5"], ["0 0 * * 6L"], ["0 12 * 12 *"],
        ["0 12 * * 5#4"], ["59 23 L * *"], ["0 0 * 10-12 *"], ["*/10 22-23 * * *"],
        // edge cases and refusals
        ["L * * * *"], ["0 9 L * L"], ["0 9 * * L"], ["0 9 * * 5L"], ["0 9 5L * *"], ["0 9 * * 5#2"], ["0 9 * * 5#5"], ["0 9 * * 5#0"], ["0 9 * * 5#9"], ["0 9 * * 5#x"], ["0 9 * * 8#1"],
        ["0 9 * * 7"], ["0 9 * * 7#1"], ["0 9 * * 7L"], ["0 9 * * MON#2"], ["0 9 * * FRI-SUN"], ["0 9 * * SUN-FRI"], ["0 9 * * 6-1"], ["0 9 * * 1-5L"],
        ["*/0 * * * *"], ["0 */0 * * *"], ["60 * * * *"], ["0 24 * * *"], ["0 0 0 * *"], ["0 0 32 * *"], ["0 0 * 13 *"], ["0 0 * 0 *"], ["0 0 * * 9"], ["*/60 * * * *"], ["*/59 * * * *"],
        ["*/30 */12 * * *"], ["*/31 * * * *"], ["0 */13 * * *"], ["0 0 */16 * *"], ["0 0 */31 * *"], ["0 0 * */12 *"], ["0 0 * */13 *"], ["0 0 * * */7"], ["0 0 * * */6"],
        ["5/10 * * * *"], ["5-30/10 * * * *"], ["0 9-5 * * *"], ["10-5 * * * *"], ["0 0 20-10 * *"], ["0 0 * 12-3 *"], ["0 0 -5 * *"], ["0 0 5- * *"], ["0 0 1-2-3 * *"], ["0 0 ,5 * *"], ["0 0 5, * *"], ["0 0 5,,6 * *"],
        ["0 9 15W * *"], ["0 9 LW * *"], ["0 9 L-3 * *"], ["0 9 1#2 * *"], ["0 9 W * *"], ["0 9 * jan *"], ["0 9 * JAN-MAR *"], ["0 9 * DEC-FEB *"], ["0 9 * foo *"], ["0 9 * * foo"], ["0 9 foo * *"],
        ["0 9 ? * *"], ["0 9 * * ?"], ["0 9 ? * ?"], ["? ? ? ? ?"], ["0 9 1 * ?"], ["0 9 ? * 1"], ["0 9 1 * 1"], ["0 9 13 * 1-5"], ["0 9 L * 1"],
        ["TZ=UTC 0 8 * * *"], ["CRON_TZ=America/New_York 0 8 * * *"], ["TZ=America/New_York 0 8 * * * *"], ["TZ=UTC"], ["0 8 * * * TZ=UTC"], ["0 8 * * * UTC"],
        ["0 0 8 * * *"], ["30 0 8 * * *"], ["abc 0 8 * * *"], ["0 0 8 * * 2026"], ["0 8 * * * 2026"], ["0 0 8 * * * 2026"], ["0 0 0 0 0 0 0 0"], ["1 2 3 4"], ["1 2 3"], ["1"], [""], ["   "], ["\t0 9 * * *\t"], ["0  9   *  *  *"],
        ["0 9 * * 1,2,3,4,5,6,7"], ["0 9 * * 0-7"], ["0-59 * * * *"], ["0 0-23 * * *"], ["0 0 1-31 1-12 0-6"], ["* * * * 1-5"], ["* * 1 * *"], ["* * L * *"], ["*/2 * * * 1"],
        ["999999 * * * *"], ["9999999 * * * *"], ["0 0 1 1 9999999"], ["*/9999999 * * * *"], ["*/ * * * *"], ["/5 * * * *"], ["*/-1 * * * *"], ["* /5 * * *"], ["*/1/2 * * * *"],
        ["1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23,24,25,26,27,28,29,30,31,32,33,34,35,36,37,38,39,40,41,42,43,44,45,46,47,48,49,50 * * * *"],
        // the 48-trigger cap and the strategies around it
        ["0,30 0-23 * * *"], ["0,20,40 0-16 * * *"], ["0,20,40 0-15 * * *"], ["0,20,40 0-17 * * *"], ["5,10 0-23 * * *"], ["1,2 */1 * * *"], ["0,15,30,45 8-19 * * *"], ["0,15,30,45 0-11 * * *"], ["0,15,30,45 0-12 * * *"],
        ["*/5 8-17 * * 1-5"], ["*/10 * * * *"], ["*/20 6-18 * * 1-5"], ["7,37 6-21 * * *"], ["7,37 6-29 * * *"], ["*/1 * * * *"], ["* 8-17 * * *"], ["* 0-23 * * *"], ["*/2 9-10 * * *"],
        ["0 8 * * 1", "0 8 * * 2", "0 8 * * 3", "0 8 * * 4", "0 8 * * 5", "0 9 * * 1", "0 9 * * 2", "0 9 * * 3", "0 9 * * 4", "0 9 * * 5"],
        ["0 8 * * 1", "0 8 * * 2", "0 8 * * 3", "0 8 * * 4", "0 8 * * 5", "0 9 * * 1", "0 9 * * 2", "0 9 * * 3", "0 9 * * 4", "0 9 * * 5", "0 10 * * 1"],
        ["0,30 * * * *", "15,45 * * * *"], ["0,30 * * * *", "15,45 * * * *", "@logon"], ["@logon", "@logon"], ["@logon", "0 9 * * *"], ["0 9 * * *", "bad"], ["bad", "worse"], ["0 8 * * 1-5", "0 10 * * 0,6"],
        ["0 9 * * 1#1", "0 9 * * 1#3"], ["0 9 L * *", "0 9 1 * *"], ["0 9 1,15 * *", "0 9 L * *"], ["0 7 * * *", "30 19 * * *", "0 12 * * 1"],
        // the nine templates and variations of them come from Templates.All below
    ];

    static List<string[]> Corpus()
    {
        var list = new List<string[]>(Fixed);
        foreach (Template t in Templates.All) list.Add([t.Cron]);
        var r = new Random(20261003);
        string[] mins = ["0", "5", "15", "30", "59", "60", "*", "*/5", "*/15", "*/0", "0,30", "1-59/2", "0-10", "10-5", "x", "*/61", "45-50/2", "?", "5,,6"];
        string[] hours = ["0", "8", "9", "17", "23", "24", "*", "*/2", "*/6", "8-17", "9-17/2", "0,12", "7,19", "22-3", "x", "8-17/3", "?"];
        string[] doms = ["*", "1", "15", "31", "32", "0", "L", "1,15", "1-7", "*/2", "29-31", "15W", "L-2", "?", "2,L", "10-20/5", "5L"];
        string[] months = ["*", "1", "2", "12", "13", "JAN", "dec", "1,7", "*/3", "JAN-MAR", "10-12", "x", "?", "*/5", "2-2"];
        string[] dows = ["*", "0", "1", "5", "7", "8", "1-5", "MON-FRI", "SAT,SUN", "0,6", "1#1", "5L", "2#4", "5#5", "1#1,3#3", "6L,1", "*/2", "FRI-SUN", "?", "L", "x#1", "1,1"];
        for (int i = 0; i < 360; i++)
        {
            string P(string[] a) => a[r.Next(a.Length)];
            list.Add([P(mins) + " " + P(hours) + " " + P(doms) + " " + P(months) + " " + P(dows)]);
        }
        return list;
    }

    // ---- the shims: the only lines that name the parser and the matcher ----

    // The parse goes through Core's adapter (Cadence.Core.Cron.CronSyntax); the matcher is the cron module's own (Cadence.Cron), parsing the same text again, so the golden also
    // proves that the module's parser and the adapter agree. Before the move both were Core's CronParser and CronMatcher.
    static CronExpr? Parse(string line, int no, List<CronDiagnostic> diags) => CronSyntax.Parse(line, no, diags);

    static bool MatchesDate(Cadence.Cron.CronExpression e, DateOnly d) => Cadence.Cron.CronMatcher.MatchesDate(e, d);

    static bool Matches(Cadence.Cron.CronExpression e, DateTime t) => Cadence.Cron.CronMatcher.Matches(e, t);

    // ---- the record ----

    static string Diag(CronDiagnostic d) => d.Severity + "|" + d.Line.ToString(CultureInfo.InvariantCulture) + "|" + d.Field + "|" + d.Message;

    static string Safe(Func<string> f)
    {
        try { return f(); }
        catch (Exception ex) { return "THROWS " + ex.GetType().Name + ": " + ex.Message; }
    }

    static string F(Occurrence o) => o.At.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture) + (o.DstAdjusted ? " dst" : "") + (o.TriggerIndex != 0 ? " #" + o.TriggerIndex.ToString(CultureInfo.InvariantCulture) : "");

    static readonly DateOnly[] Dates = Enumerable.Range(0, 92).Select(i => new DateOnly(2026, 10, 1).AddDays(i))
        .Concat(Enumerable.Range(0, 15).Select(i => new DateOnly(2028, 2, 20).AddDays(i)))
        .Concat(Enumerable.Range(0, 6).Select(i => new DateOnly(2027, 2, 25).AddDays(i))).ToArray();

    static readonly DateTime[] Minutes = new[] { new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 30), new DateOnly(2028, 2, 29) }
        .SelectMany(d => new[] { "00:00", "00:37", "08:00", "08:30", "12:15", "17:30", "23:59" }.Select(t => d.ToDateTime(TimeOnly.Parse(t, CultureInfo.InvariantCulture)))).ToArray();

    static string Record(string[] lines)
    {
        var sb = new StringBuilder();
        sb.Append("### ").Append(string.Join(" + ", lines.Select(l => "<" + l + ">"))).Append('\n');

        var exprs = new List<CronExpr>();
        for (int i = 0; i < lines.Length; i++)
        {
            var diags = new List<CronDiagnostic>();
            string shown = Safe(() =>
            {
                CronExpr? e = Parse(lines[i], i + 1, diags);
                if (e is not null) exprs.Add(e);
                return e is null ? "null" : e.ToString();
            });
            sb.Append("parse ").Append(i + 1).Append(": ").Append(shown).Append(" :: ").Append(string.Join(" ;; ", diags.Select(Diag))).Append('\n');
        }

        var mods = new List<Cadence.Cron.CronExpression>();
        for (int i = 0; i < lines.Length; i++)
        {
            Cadence.Cron.CronExpression? m = Cadence.Cron.CronParser.Parse(lines[i], i + 1, []);
            if (m is not null) mods.Add(m);
        }

        var spec = new ScheduleSpec(new ValueList<string>(lines), HolidayCalendar.None, Anchor);
        CompiledSchedule? compiled = null;
        sb.Append("compile: ").Append(Safe(() =>
        {
            compiled = CronCompiler.Compile(spec, Ctx);
            return compiled.Success + " logon=" + compiled.AtLogon + " groups=" + compiled.Groups + " triggers=" + compiled.Triggers
                + " diags=" + string.Join(" ;; ", compiled.Diagnostics.Select(Diag))
                + " alt=" + (compiled.ClosestAlternative?.ToString() ?? "null") + " why=" + (compiled.AlternativeExplanation ?? "null");
        })).Append('\n');

        sb.Append("english: ").Append(Safe(() => EnglishRenderer.Describe(spec))).Append('\n');
        var nyse = new ScheduleSpec(new ValueList<string>(lines), HolidayCalendar.Nyse, Anchor);
        sb.Append("english nyse: ").Append(Safe(() => EnglishRenderer.Describe(nyse))).Append('\n');

        sb.Append("human: ").Append(Safe(() =>
        {
            bool ok = HumanScheduleMapper.TryFromCron(new ValueList<string>(lines), out HumanSchedule h);
            return ok ? "true " + h + " -> " + HumanScheduleMapper.ToCron(h) : "false";
        })).Append('\n');

        if (compiled is { Success: true } c && c.Triggers.Count > 0)
        {
            sb.Append("next est: ").Append(Safe(() => string.Join(", ", NextRuns.Next(c.Triggers, new DateTimeOffset(2026, 9, 30, 22, 0, 0, TimeSpan.FromHours(-4)), 6, Est).Select(F)))).Append('\n');
            sb.Append("next utc: ").Append(Safe(() => string.Join(", ", NextRuns.Next(c.Triggers, new DateTimeOffset(2028, 2, 28, 23, 50, 0, TimeSpan.Zero), 6, TimeZoneInfo.Utc).Select(F)))).Append('\n');
            sb.Append("next nyse: ").Append(Safe(() => string.Join(", ", NextRuns.Next(c.Triggers, HolidayCalendar.Nyse, new DateTimeOffset(2026, 12, 20, 0, 0, 0, TimeSpan.FromHours(-5)), 4, Est).Select(F)))).Append('\n');
        }

        if (exprs.Count == lines.Length && mods.Count == lines.Length)
        {
            sb.Append("dates: ").Append(Safe(() => new string(Dates.Select(d => mods.Any(e => MatchesDate(e, d)) ? '1' : '0').ToArray()))).Append('\n');
            sb.Append("minutes: ").Append(Safe(() => new string(Minutes.Select(t => mods.Any(e => Matches(e, t)) ? '1' : '0').ToArray()))).Append('\n');
        }
        else if (exprs.Count != mods.Count) sb.Append("DISAGREEMENT between the adapter and the module parser\n");
        return sb.ToString();
    }

    static string GoldenPath()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "tests", "Cadence.Tests", "Golden"))) dir = Path.GetDirectoryName(dir);
        return dir is null ? "" : Path.Combine(dir, "tests", "Cadence.Tests", "Golden", "cron-behaviour.txt");
    }

    public static void TheCronBehaviourIsUnchanged()
    {
        string path = GoldenPath();
        if (path.Length == 0) { T.True("the golden folder was found", false); return; }
        List<string[]> corpus = Corpus();
        var sb = new StringBuilder();
        CultureInfo saved = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;   // the record prints records and dates with ToString: the golden must not depend on the machine's culture
        try { foreach (string[] lines in corpus) sb.Append(Record(lines)); }
        finally { CultureInfo.CurrentCulture = saved; }
        string actual = sb.ToString();
        // Only the checkout may differ (git can turn LF into CRLF in a text file); the record itself only has LF.
        string golden = File.Exists(path) ? File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal) : "";
        T.True("the corpus is broad (" + corpus.Count + " cases, " + actual.Length + " characters)", corpus.Count >= 500);
        if (actual != golden)
        {
            int at = 0;
            while (at < actual.Length && at < golden.Length && actual[at] == golden[at]) at++;
            int from = Math.Max(0, at - 120);
            T.True("byte-identical to Golden\\cron-behaviour.txt; first difference at " + at + ": actual [" + actual[from..Math.Min(actual.Length, at + 120)] + "] golden [" + golden[from..Math.Min(golden.Length, at + 120)] + "]", false);
        }
        else T.True("byte-identical to Golden\\cron-behaviour.txt (" + corpus.Count + " cases, " + actual.Length + " characters)", true);
    }
}
