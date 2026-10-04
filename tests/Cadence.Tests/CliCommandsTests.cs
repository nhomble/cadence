using System.Globalization;
using System.Text;
using System.Text.Json;
using Cadence.App.Cli;
using Cadence.Core.History;
using Cadence.Core.TaskXml;
using Cadence.History;
using Cadence.Scheduler;
using Cadence.TestHarness;
using static Cadence.Tests.PlanSamples;

namespace Cadence.Tests;

/// <summary>
/// The headless commands against the in-memory gateway and a temp runs root (never Task Scheduler, never %LOCALAPPDATA%): --probe output and
/// where its status comes from, --apply/--delete/--reapply/--untrack/--run/--history, --selftest, and the text/JSON formatting helpers.
/// </summary>
public static class CliCommandsTests
{
    const string Id = "d4dst0ck";

    sealed class Rig : IDisposable
    {
        public FakeGateway Gw { get; } = new();
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "CadenceCliTests-" + Guid.NewGuid().ToString("N"));
        public RunRecordStore Store { get; }
        public StringBuilder Out { get; } = new();
        public CliEnv Env { get; }

        /// <summary>The clock the commands see (fixed; tests move it).</summary>
        public DateTimeOffset Now { get; set; }

        public Rig(DateTimeOffset? now = null)
        {
            Store = new RunRecordStore(Path.Combine(Root, "runs"));
            Now = now ?? ComposeSamples.Now;
            Env = new CliEnv(Gw, Store, () => Now, TimeZoneInfo.Utc, t => Out.Append(t));
            Directory.CreateDirectory(Root);
        }

        public string Text => Out.ToString();

        public int Run(params string[] args)
        {
            CliParseResult p = CliParser.Parse(args);
            if (p.Command is not { } cmd) throw new InvalidOperationException("bad test command line: " + p.Error);
            Out.Clear();
            return cmd.Action == CliAction.Probe
                ? Probe(cmd.All)
                : JobCommands.Execute(cmd, Env);
        }

        int Probe(bool all)
        {
            Env.Raw(Cadence.App.Cli.Probe.Build(Env, all, "test") + "\r\n");
            return 0;
        }

        public string WriteFile(string name, string content)
        {
            string path = Path.Combine(Root, name);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return path;
        }

        public void WriteRuns(string jobId, params string[] lines)
        {
            string dir = Path.Combine(Store.Root, jobId);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, RunRecordStore.JournalFile), string.Join("\n", lines) + "\n");
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }

    static string RunLine(DateTimeOffset at, string status, int exit = 0, double seconds = 1, string? msg = null, string task = "\\\\Cadence\\\\Stock ingest (Dad)", int outBytes = 5)
    {
        string F(DateTimeOffset t) => t.ToString("o", CultureInfo.InvariantCulture);
        return "{\"v\":1,\"wv\":1,\"task\":\"" + task + "\",\"runId\":\"" + at.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)
            + "\",\"wrapperStart\":\"" + F(at) + "\",\"start\":\"" + F(at.AddMilliseconds(500)) + "\",\"end\":\"" + F(at.AddMilliseconds(500 + seconds * 1000))
            + "\",\"exit\":" + exit.ToString(CultureInfo.InvariantCulture) + ",\"status\":\"" + status + "\",\"pid\":1,\"outBytes\":" + outBytes.ToString(CultureInfo.InvariantCulture)
            + ",\"errBytes\":0,\"msg\":" + (msg is null ? "null" : "\"" + msg + "\"") + "}";
    }

    static JsonDocument Json(string text) => JsonDocument.Parse(text);

    static JsonElement FirstJob(JsonDocument doc) => doc.RootElement.GetProperty("jobs")[0];

    // ---- --probe ----

    public static void ProbeShapeAndStatusFromRunRecords()
    {
        using var rig = new Rig();
        rig.Gw.Seed(StockName, Stock());
        DateTimeOffset t0 = ComposeSamples.Now.AddDays(-2);
        DateTimeOffset newest = t0.AddHours(2);
        rig.WriteRuns(Id, RunLine(t0, "ok"), RunLine(t0.AddHours(1), "fail", exit: 3, seconds: 3), RunLine(newest, "ok", seconds: 2));
        // Task Scheduler reports a non-zero code for the newest run. Nothing may be derived from it: the records say the run was fine.
        rig.Gw.SetRuntime(StockName, TaskState.Ready, newest.LocalDateTime, 1);

        T.Eq("exit code", 0, rig.Run("--probe"));
        using JsonDocument doc = Json(rig.Text);
        JsonElement root = doc.RootElement;
        T.Eq("version", "test", root.GetProperty("version").GetString());
        T.Eq("user", FakeGateway.User, root.GetProperty("user").GetString());
        T.Eq("folder", "Cadence", root.GetProperty("folder").GetString());
        T.Eq("one job", 1, root.GetProperty("jobs").GetArrayLength());
        T.True("no foreign property without --all", !root.TryGetProperty("foreign", out _));

        JsonElement j = FirstJob(doc);
        T.Eq("id", Id, j.GetProperty("id").GetString());
        T.Eq("name", StockName, j.GetProperty("name").GetString());
        T.Eq("taskPath", "\\Cadence\\" + StockName, j.GetProperty("taskPath").GetString());
        T.Eq("ownership", "managed", j.GetProperty("ownership").GetString());
        T.Eq("sync", "inSync", j.GetProperty("sync").GetString());
        T.Eq("enabled", true, j.GetProperty("enabled").GetBoolean());
        T.Eq("cron", "0 18 * * 1-5", j.GetProperty("cron")[0].GetString());
        T.Eq("skipHolidays", "nyse", j.GetProperty("skipHolidays").GetString());
        T.True("schedule text", j.GetProperty("schedule").GetString()!.Contains("except NYSE market holidays", StringComparison.Ordinal), j.GetProperty("schedule").GetString());
        T.Eq("drift is empty", 0, j.GetProperty("drift").GetArrayLength());
        T.Eq("lastResult comes from the records (not from LastTaskResult = 1)", "ok", j.GetProperty("lastResult").GetString());
        T.Eq("badge", "ok", j.GetProperty("badge").GetString());
        T.Eq("sampleSize", 3, j.GetProperty("sampleSize").GetInt32());
        T.True("successRate 2/3", Math.Abs(j.GetProperty("successRate").GetDouble() - 0.6667) < 0.0001);
        T.Eq("averageRuntimeMs = mean of ok and failed runs (1 s, 3 s, 2 s)", 2000L, j.GetProperty("averageRuntimeMs").GetInt64());
        T.Eq("lastRunAt", newest.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture), j.GetProperty("lastRunAt").GetString());
        JsonElement ts = j.GetProperty("taskScheduler");
        T.Eq("the raw code is shown as a raw field", "0x00000001", ts.GetProperty("lastTaskResult").GetString());
        T.Eq("raw state", "Ready", ts.GetProperty("state").GetString());
    }

    public static void ABlockedLaunchIsReadFromTheRecordsNotTheResultCode()
    {
        using var rig = new Rig();
        rig.Gw.Seed(StockName, Stock());
        // Task Scheduler ran the task a minute ago and reports 0x80070005; no wrapper ran, so there is no record at all.
        rig.Gw.SetRuntime(StockName, TaskState.Ready, ComposeSamples.Now.AddMinutes(-1).LocalDateTime, unchecked((int)0x80070005));

        T.Eq("history exit code", 0, rig.Run("--history", Id, "--json"));
        using JsonDocument h = Json(rig.Text);
        JsonElement row = h.RootElement.GetProperty("recent")[0];
        T.Eq("status", "notReported", row.GetProperty("status").GetString());
        T.Eq("the message is the blocked-launch text", TaskResultCodes.AccessDeniedLaunch, row.GetProperty("message").GetString());
        T.Eq("exit code field carries the raw code", unchecked((int)0x80070005), row.GetProperty("exitCode").GetInt32());
        T.Eq("summary lastResult", "notReported", h.RootElement.GetProperty("summary").GetProperty("lastResult").GetString());
        T.Eq("summary successRate counts it as a failure", 0.0, h.RootElement.GetProperty("summary").GetProperty("successRate").GetDouble());

        T.Eq("probe exit code", 0, rig.Run("--probe"));
        using JsonDocument doc = Json(rig.Text);
        T.Eq("probe badge", "notReported", FirstJob(doc).GetProperty("badge").GetString());
        T.Eq("probe lastResult", "notReported", FirstJob(doc).GetProperty("lastResult").GetString());

        T.Eq("history text exit code", 0, rig.Run("--history", Id));
        T.True("the text table shows the message", rig.Text.Contains("Windows refused to start this job: access denied (0x80070005)", StringComparison.Ordinal), rig.Text);
    }

    public static void HiddenJobsReportZeroButTheRecordsDecide()
    {
        // A hidden job always reports LastTaskResult 0 (conhost swallows the exit code); the failed run in the record must still show as failed.
        using var rig = new Rig();
        rig.Gw.Seed(StockName, Stock());
        DateTimeOffset at = ComposeSamples.Now.AddHours(-3);
        rig.WriteRuns(Id, RunLine(at, "fail", exit: 2, msg: "boom"));
        rig.Gw.SetRuntime(StockName, TaskState.Ready, at.LocalDateTime, 0);
        T.Eq("exit", 0, rig.Run("--probe"));
        using JsonDocument doc = Json(rig.Text);
        T.Eq("lastResult", "failed", FirstJob(doc).GetProperty("lastResult").GetString());
        T.Eq("badge", "failed", FirstJob(doc).GetProperty("badge").GetString());
        T.Eq("successRate", 0.0, FirstJob(doc).GetProperty("successRate").GetDouble());
        T.Eq("raw code still shown", "0x00000000", FirstJob(doc).GetProperty("taskScheduler").GetProperty("lastTaskResult").GetString());
    }

    public static void NextRunsSkipHolidaysAndDisabledJobsHaveNone()
    {
        // 2026-11-23 is a Monday; Thursday the 26th is Thanksgiving (NYSE closed). Evaluated in UTC for a fixed answer.
        var now = new DateTimeOffset(2026, 11, 23, 0, 0, 0, TimeSpan.Zero);
        using var rig = new Rig(now);
        rig.Gw.Seed(StockName, Stock());
        T.Eq("exit", 0, rig.Run("--probe"));
        using (JsonDocument doc = Json(rig.Text))
        {
            string next = string.Join(",", FirstJob(doc).GetProperty("next").EnumerateArray().Select(e => e.GetString()));
            T.Eq("the holiday is left out",
                "2026-11-23T18:00:00+00:00,2026-11-24T18:00:00+00:00,2026-11-25T18:00:00+00:00,2026-11-27T18:00:00+00:00,2026-11-30T18:00:00+00:00", next);
        }

        // A disabled job has no next runs.
        string disabled = Compose(ComposeSamples.StockIngest() with { Enabled = false, Id = "off00001", Name = "Off job" }, "Off job");
        rig.Gw.Seed("Off job", disabled);
        T.Eq("exit 2", 0, rig.Run("--probe"));
        using JsonDocument doc2 = Json(rig.Text);
        JsonElement off = doc2.RootElement.GetProperty("jobs").EnumerateArray().First(e => e.GetProperty("id").GetString() == "off00001");
        T.Eq("disabled: enabled false", false, off.GetProperty("enabled").GetBoolean());
        T.Eq("disabled: no next runs", 0, off.GetProperty("next").GetArrayLength());
    }

    public static void ForeignTasksAreListedOnlyWithAll()
    {
        using var rig = new Rig();
        rig.Gw.Seed(StockName, Stock());
        rig.Gw.Seed("Somebody else", Plain());
        rig.Gw.SeedOther(@"\Other\Thing", Plain());

        rig.Run("--probe");
        using (JsonDocument doc = Json(rig.Text))
        {
            T.Eq("jobs: only the managed one", 1, doc.RootElement.GetProperty("jobs").GetArrayLength());
            T.True("no foreign key", !doc.RootElement.TryGetProperty("foreign", out _));
        }

        rig.Run("--probe", "--all");
        using JsonDocument all = Json(rig.Text);
        T.Eq("jobs: still only the managed one", 1, all.RootElement.GetProperty("jobs").GetArrayLength());
        string foreign = string.Join("|", all.RootElement.GetProperty("foreign").EnumerateArray().Select(e => e.GetProperty("taskPath").GetString()).OrderBy(s => s, StringComparer.Ordinal));
        T.Eq("foreign: the managed folder's stranger and the other folder's task", @"\Cadence\Somebody else|\Other\Thing", foreign);
        T.True("every foreign entry says so", all.RootElement.GetProperty("foreign").EnumerateArray().All(e => e.GetProperty("ownership").GetString() == "foreign"));
        T.Eq("nothing was written by a probe", 0, rig.Gw.Writes.Count);
    }

    public static void AnEmptyFolderProbesToAnEmptyList()
    {
        using var rig = new Rig();
        T.Eq("exit", 0, rig.Run("--probe", "--all"));
        using JsonDocument doc = Json(rig.Text);
        T.Eq("jobs", 0, doc.RootElement.GetProperty("jobs").GetArrayLength());
        T.Eq("foreign", 0, doc.RootElement.GetProperty("foreign").GetArrayLength());
        T.Eq("no writes", 0, rig.Gw.Writes.Count);
        T.True("the output ends with a line break", rig.Text.EndsWith("\r\n", StringComparison.Ordinal));
    }

    public static void DriftAndOutdatedAreReported()
    {
        using var rig = new Rig();
        rig.Gw.Seed(StockName, Edit(Stock(), m => m with { Triggers = new Cadence.Core.Model.ValueList<Cadence.Core.Model.TaskTrigger>(m.Triggers.Select(t => t with { StartBoundary = t.StartBoundary.AddHours(1) })) }));
        rig.Run("--probe");
        using JsonDocument doc = Json(rig.Text);
        JsonElement j = FirstJob(doc);
        T.Eq("sync", "drifted", j.GetProperty("sync").GetString());
        T.True("at least one drift entry", j.GetProperty("drift").GetArrayLength() >= 1);
        JsonElement d = j.GetProperty("drift")[0];
        T.True("entry has field, before and after", d.TryGetProperty("field", out _) && d.TryGetProperty("before", out _) && d.TryGetProperty("after", out _));
        T.True("problem is a sentence", j.GetProperty("problem").GetString()!.Length > 10);
    }

    // ---- --apply ----

    const string EchoJson = """{"id":"tst00001","name":"Echo job","schedule":{"cron":["0 9 * * 1-5"]},"run":{"exe":"cmd.exe","args":"/c echo hi"}}""";

    public static void ApplyCreatesThenUpdatesTheSameJob()
    {
        using var rig = new Rig();
        string file = rig.WriteFile("echo.json", EchoJson);

        T.Eq("create: exit", 0, rig.Run("--apply", file, "--yes"));
        T.True("create: says what happened (" + rig.Text.Trim() + ")", rig.Text.StartsWith("OK: Created \\Cadence\\Echo job", StringComparison.Ordinal));
        T.True("create: names the job", rig.Text.Contains("job tst00001", StringComparison.Ordinal));
        T.Eq("create: one write, mode Create", "register Echo job", string.Join("|", rig.Gw.Writes));
        T.Eq("create: mode", "Create", string.Join("|", rig.Gw.Modes));

        rig.Gw.Writes.Clear();
        T.Eq("again: exit", 0, rig.Run("--apply", file, "--yes"));
        T.True("again: nothing to change (" + rig.Text.Trim() + ")", rig.Text.Contains("No changes", StringComparison.Ordinal));
        T.Eq("again: no write", 0, rig.Gw.Writes.Count);
        T.Eq("again: still one task", "Echo job", string.Join("|", rig.Gw.Names));

        string changed = EchoJson.Replace("0 9 * * 1-5", "30 9 * * 1-5", StringComparison.Ordinal);
        T.Eq("update: exit", 0, rig.Run("--apply", rig.WriteFile("echo2.json", changed), "--yes"));
        T.True("update: saved", rig.Text.StartsWith("OK: Saved \\Cadence\\Echo job", StringComparison.Ordinal), rig.Text);
        T.Eq("update: one write", "register Echo job", string.Join("|", rig.Gw.Writes));
        rig.Run("--probe");
        using JsonDocument doc = Json(rig.Text);
        T.Eq("the new cron is live", "30 9 * * 1-5", FirstJob(doc).GetProperty("cron")[0].GetString());
        T.Eq("still in sync", "inSync", FirstJob(doc).GetProperty("sync").GetString());

        // Applying a file without an anchor date on a later day is still "no change": the job keeps the anchor of its first save.
        rig.Gw.Writes.Clear();
        rig.Now = rig.Now.AddDays(40);
        T.Eq("later day: exit", 0, rig.Run("--apply", rig.WriteFile("echo2b.json", changed), "--yes"));
        T.True("later day: nothing to change (" + rig.Text.Trim() + ")", rig.Text.Contains("No changes", StringComparison.Ordinal));
        T.Eq("later day: no write", 0, rig.Gw.Writes.Count);
        rig.Now = ComposeSamples.Now;

        rig.Gw.Writes.Clear();
        string renamed = changed.Replace("Echo job", "Echo job renamed", StringComparison.Ordinal);
        T.Eq("rename: exit", 0, rig.Run("--apply", rig.WriteFile("echo3.json", renamed), "--yes"));
        T.Eq("rename: new task first, then the old one goes", "register Echo job renamed|delete Echo job", string.Join("|", rig.Gw.Writes));
        T.Eq("rename: one task left", "Echo job renamed", string.Join("|", rig.Gw.Names));
    }

    public static void ApplyNeverTouchesAForeignTaskOfTheSameName()
    {
        using var rig = new Rig();
        string foreign = Plain();
        rig.Gw.Seed("Echo job", foreign);
        T.Eq("exit", 0, rig.Run("--apply", rig.WriteFile("echo.json", EchoJson), "--yes"));
        T.True("a free name was chosen (" + rig.Text.Trim() + ")", rig.Text.Contains("Echo job (2)", StringComparison.Ordinal));
        T.Eq("the foreign task is unchanged", foreign, rig.Gw.XmlOf("Echo job"));
    }

    public static void ApplyProblemsWriteNothing()
    {
        using var rig = new Rig();
        T.Eq("missing file: exit", 1, rig.Run("--apply", Path.Combine(rig.Root, "nope.json"), "--yes"));
        T.True("missing file: says so", rig.Text.StartsWith("ERROR: Cannot read the job file", StringComparison.Ordinal), rig.Text);

        T.Eq("bad JSON: exit", 1, rig.Run("--apply", rig.WriteFile("bad.json", "{oops"), "--yes"));
        T.True("bad JSON: says so", rig.Text.Contains("not valid JSON", StringComparison.Ordinal), rig.Text);

        T.Eq("unknown field: exit", 1, rig.Run("--apply", rig.WriteFile("typo.json", EchoJson.Replace("\"name\"", "\"nam\"", StringComparison.Ordinal)), "--yes"));

        T.Eq("bad cron: exit", 1, rig.Run("--apply", rig.WriteFile("cron.json", EchoJson.Replace("0 9 * * 1-5", "61 9 * * *", StringComparison.Ordinal)), "--yes"));
        T.True("bad cron: the schedule problem is shown (" + rig.Text.Trim() + ")", rig.Text.StartsWith("ERROR: The schedule can't be used", StringComparison.Ordinal));

        T.Eq("timeout over the limit: exit", 1, rig.Run("--apply", rig.WriteFile("to.json", EchoJson.Replace("\"run\":{", "\"run\":{\"timeoutSec\":99999999,", StringComparison.Ordinal)), "--yes"));
        T.Eq("nothing was written by any of them", 0, rig.Gw.Writes.Count);
        T.Eq("no tasks", 0, rig.Gw.Names.Count);
    }

    // ---- --run / --history / --delete / --untrack / --reapply ----

    public static void RunStartsOnlyManagedJobsAndRejectsBadIds()
    {
        using var rig = new Rig();
        rig.Gw.Seed(StockName, Stock());
        rig.Gw.Seed("Somebody else", Plain());

        T.Eq("run: exit", 0, rig.Run("--run", Id));
        T.True("run: says it started (" + rig.Text.Trim() + ")", rig.Text.StartsWith("Started \\Cadence\\" + StockName, StringComparison.Ordinal));
        T.Eq("run: asked Task Scheduler to run exactly that task", "run " + StockName, string.Join("|", rig.Gw.Writes));

        rig.Gw.Writes.Clear();
        T.Eq("unknown id: exit", 1, rig.Run("--run", "zzzzzzzz"));
        T.True("unknown id: says so", rig.Text.Contains("No job with id zzzzzzzz in \\Cadence", StringComparison.Ordinal), rig.Text);
        T.Eq("not an id: exit", 1, rig.Run("--run", "Somebody else"));
        T.True("not an id: says so", rig.Text.Contains("is not a job id", StringComparison.Ordinal), rig.Text);
        T.Eq("short id: exit", 1, rig.Run("--run", "abc"));
        T.Eq("nothing ran", 0, rig.Gw.Writes.Count);
    }

    public static void HistoryJsonAndText()
    {
        using var rig = new Rig();
        rig.Gw.Seed(StockName, Stock());
        DateTimeOffset t0 = ComposeSamples.Now.AddDays(-1);
        rig.WriteRuns(Id, RunLine(t0, "ok", seconds: 1.5), RunLine(t0.AddHours(1), "fail", exit: 3, seconds: 12, msg: "oops"),
            RunLine(t0.AddHours(2), "skipped", seconds: 0, msg: "holiday: Test Day", outBytes: 0));
        rig.Gw.SetRuntime(StockName, TaskState.Ready, t0.AddHours(2).LocalDateTime, 0);

        T.Eq("json: exit", 0, rig.Run("--history", Id, "--json"));
        using JsonDocument doc = Json(rig.Text);
        JsonElement recent = doc.RootElement.GetProperty("recent");
        T.Eq("json: three rows", 3, recent.GetArrayLength());
        T.Eq("json: newest first", "skipped", recent[0].GetProperty("status").GetString());
        T.Eq("json: message of the skip", "holiday: Test Day", recent[0].GetProperty("message").GetString());
        T.Eq("json: failed row exit code", 3, recent[1].GetProperty("exitCode").GetInt32());
        T.Eq("json: failed row duration", 12000L, recent[1].GetProperty("durationMs").GetInt64());
        T.Eq("json: ok row has output", true, recent[2].GetProperty("hasOutput").GetBoolean());
        T.Eq("json: skipped row has none", false, recent[0].GetProperty("hasOutput").GetBoolean());
        T.True("json: skipped row has no duration", recent[0].GetProperty("durationMs").ValueKind == JsonValueKind.Null);
        JsonElement s = doc.RootElement.GetProperty("summary");
        T.Eq("json: summary lastResult skips the skipped run", "failed", s.GetProperty("lastResult").GetString());
        T.Eq("json: sample size excludes the skip", 2, s.GetProperty("sampleSize").GetInt32());
        T.Eq("json: success rate", 0.5, s.GetProperty("successRate").GetDouble());

        T.Eq("--max: exit", 0, rig.Run("--history", Id, "--json", "--max", "1"));
        using JsonDocument one = Json(rig.Text);
        T.Eq("--max limits the rows", 1, one.RootElement.GetProperty("recent").GetArrayLength());

        T.Eq("text: exit", 0, rig.Run("--history", Id));
        string[] lines = rig.Text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        T.Eq("text: title", "Stock ingest (Dad)  [d4dst0ck]", lines[0]);
        T.True("text: summary line (" + lines[1] + ")", lines[1].StartsWith("last result: skipped; success 50% of 2 run(s); average ", StringComparison.Ordinal));
        T.True("text: header", lines[2].StartsWith("When", StringComparison.Ordinal) && lines[2].Contains("Message", StringComparison.Ordinal));
        T.Eq("text: header + 3 rows", 6, lines.Length);
        T.True("text: failed row", lines[4].Contains("failed", StringComparison.Ordinal) && lines[4].Contains("12 s", StringComparison.Ordinal) && lines[4].EndsWith("oops", StringComparison.Ordinal), lines[4]);

        rig.Gw.Writes.Clear();
        T.Eq("no runs yet: exit", 0, rig.Run("--history", Id, "--max", "5"));
        T.Eq("history is read-only", 0, rig.Gw.Writes.Count);
    }

    public static void HistoryOfAJobWithoutRunsSaysSo()
    {
        using var rig = new Rig();
        rig.Gw.Seed(StockName, Stock());
        T.Eq("text exit", 0, rig.Run("--history", Id));
        T.True("says there are no runs", rig.Text.Contains("no runs recorded yet", StringComparison.Ordinal), rig.Text);
        T.Eq("json exit", 0, rig.Run("--history", Id, "--json"));
        using JsonDocument doc = Json(rig.Text);
        T.Eq("no rows", 0, doc.RootElement.GetProperty("recent").GetArrayLength());
        T.True("summary lastResult null", doc.RootElement.GetProperty("summary").GetProperty("lastResult").ValueKind == JsonValueKind.Null);
        T.Eq("summary sampleSize", 0, doc.RootElement.GetProperty("summary").GetProperty("sampleSize").GetInt32());
        T.Eq("unknown job: exit", 1, rig.Run("--history", "zzzzzzzz"));
    }

    public static void DeleteRemovesTheTaskAndItsRunHistory()
    {
        using var rig = new Rig();
        rig.Gw.Seed(StockName, Stock());
        rig.Gw.Seed("Somebody else", Plain());
        rig.WriteRuns(Id, RunLine(ComposeSamples.Now.AddHours(-1), "ok"));
        rig.WriteRuns("keepkeep", RunLine(ComposeSamples.Now.AddHours(-1), "ok"));

        T.Eq("exit", 0, rig.Run("--delete", Id, "--yes"));
        T.True("says deleted (" + rig.Text.Trim() + ")", rig.Text.StartsWith("OK: Deleted \\Cadence\\" + StockName, StringComparison.Ordinal));
        T.True("says the history went too", rig.Text.Contains("Run history of d4dst0ck removed.", StringComparison.Ordinal), rig.Text);
        T.Eq("the task is gone, the stranger stays", "Somebody else", string.Join("|", rig.Gw.Names));
        T.True("the job's run folder is gone", !Directory.Exists(Path.Combine(rig.Store.Root, Id)));
        T.True("another job's run folder stays", Directory.Exists(Path.Combine(rig.Store.Root, "keepkeep")));

        T.Eq("again: exit (the job is not there any more)", 1, rig.Run("--delete", Id, "--yes"));
        T.Eq("a foreign task cannot be deleted by id", 1, rig.Run("--delete", "somebody", "--yes"));
        T.Eq("the stranger is untouched", "Somebody else", string.Join("|", rig.Gw.Names));
    }

    public static void FailedDeleteKeepsTheHistory()
    {
        using var rig = new Rig();
        rig.Gw.Seed(StockName, Stock());
        rig.WriteRuns(Id, RunLine(ComposeSamples.Now.AddHours(-1), "ok"));
        rig.Gw.Seed("Somebody else", Plain());
        // Task Scheduler refuses to delete (simulated): the history must stay.
        var failing = new FailingDeleteGateway(rig.Gw);
        var env = new CliEnv(failing, rig.Store, () => ComposeSamples.Now, TimeZoneInfo.Utc, t => rig.Out.Append(t));
        int rc = JobCommands.Execute(new CliCommand(CliAction.Delete, Id, "Cadence", false, true, 20, false), env);
        T.Eq("exit", 1, rc);
        T.True("history kept", Directory.Exists(Path.Combine(rig.Store.Root, Id)));
    }

    sealed class FailingDeleteGateway(FakeGateway inner) : ITaskSchedulerGateway
    {
        public SchedulerOptions Options => inner.Options;
        public string ConnectedUserId => inner.ConnectedUserId;
        public string ConnectedUserSid => inner.ConnectedUserSid;
        public bool IsConnectedUser(string? p) => inner.IsConnectedUser(p);
        public IReadOnlyList<RegisteredTaskInfo> ListManagedFolder() => inner.ListManagedFolder();
        public IReadOnlyList<RegisteredTaskInfo> ListOtherTasks(int max = 5000) => inner.ListOtherTasks(max);
        public RegisteredTaskInfo? Get(string taskName) => inner.Get(taskName);
        public RegisteredTaskInfo Register(string taskName, string xml, RegisterMode mode = RegisterMode.CreateOrUpdate) => inner.Register(taskName, xml, mode);
        public void Delete(string taskName) { /* the task stays */ }
        public void Run(string taskName) => inner.Run(taskName);
        public void Stop(string taskName) => inner.Stop(taskName);
        public void DeleteFolderIfEmpty() => inner.DeleteFolderIfEmpty();
    }

    public static void UntrackAndReapply()
    {
        using var rig = new Rig();
        string drifted = Edit(Stock(), m => m with { Triggers = new Cadence.Core.Model.ValueList<Cadence.Core.Model.TaskTrigger>(m.Triggers.Select(t => t with { StartBoundary = t.StartBoundary.AddHours(1) })) });
        rig.Gw.Seed(StockName, drifted);

        T.Eq("reapply: exit", 0, rig.Run("--reapply", Id, "--yes"));
        T.True("reapply: says so (" + rig.Text.Trim() + ")", rig.Text.StartsWith("OK: Re-applied", StringComparison.Ordinal));
        // Phase 19: the command line records itself as the writer (updatedBy "cli"); everything else is exactly what Cadence composes.
        T.Eq("reapply: the task is Cadence's version again", PlanSamples.EditMeta(Stock(), m => m with { UpdatedBy = "cli" }), rig.Gw.XmlOf(StockName));

        T.Eq("untrack: exit", 0, rig.Run("--untrack", Id, "--yes"));
        T.True("untrack: says so", rig.Text.Contains("no longer manages", StringComparison.Ordinal), rig.Text);
        T.Eq("untrack: the task stays", StockName, string.Join("|", rig.Gw.Names));
        TaskDefinitionModel after = TaskXmlReader.Read(rig.Gw.XmlOf(StockName)!);
        T.True("untrack: the metadata is gone", string.IsNullOrEmpty(after.RegistrationInfo.Documentation));

        T.Eq("the job is no longer addressable by id", 1, rig.Run("--run", Id));
        T.Eq("nor can it be reapplied", 1, rig.Run("--reapply", Id, "--yes"));
        rig.Run("--probe", "--all");
        using JsonDocument doc = Json(rig.Text);
        T.Eq("probe: no managed job", 0, doc.RootElement.GetProperty("jobs").GetArrayLength());
        T.Eq("probe --all: it is foreign now", @"\Cadence\" + StockName, doc.RootElement.GetProperty("foreign")[0].GetProperty("taskPath").GetString());
    }

    // ---- --selftest ----

    // ---- Phase 19: attribution, revision and the agent lock through the command line ----

    static string Str(JsonElement e, string name) => e.GetProperty(name).GetString() ?? "";

    public static void CliWritesAreAttributedAndProbeShowsIt()
    {
        using var rig = new Rig();
        // The app creates the job (the same pipeline as the window's Save), then the command line changes it.
        Cadence.Core.Model.JobDefinition job = (JobJson.TryParse(EchoJson, new DateOnly(2026, 10, 1), out JobJson.Parsed? parsed, out _) ? parsed! : throw new InvalidOperationException("sample")).Job;
        var app = new Cadence.Scheduler.Apply.Applier(new ManagedJobs(rig.Gw, () => rig.Now, Cadence.Core.Metadata.Actor.App));
        T.True("the app creates the job", app.Save(job, null).Success);

        rig.Now = ComposeSamples.Now.AddMinutes(30);
        string changed = EchoJson.Replace("0 9 * * 1-5", "30 9 * * 1-5", StringComparison.Ordinal);
        T.Eq("cli --apply: exit", 0, rig.Run("--apply", rig.WriteFile("echo2.json", changed), "--yes"));

        rig.Run("--probe");
        using JsonDocument doc = Json(rig.Text);
        JsonElement j = FirstJob(doc);
        T.Eq("createdBy is still the app", "app", Str(j, "createdBy"));
        T.Eq("updatedBy is the command line", "cli", Str(j, "updatedBy"));
        T.Eq("created", "2026-10-01T09:00:00-04:00", Str(j, "created"));
        T.Eq("updated", CliFormat.Iso(ComposeSamples.Now.AddMinutes(30)), Str(j, "updated"));
        T.Eq("agentLock", false, j.GetProperty("agentLock").GetBoolean());
        string revision = Str(j, "revision");
        T.True("revision is 12 lowercase hex", revision.Length == 12 && revision.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')), revision);
        T.Eq("revision is the live task's JobRevision", Cadence.Core.Planning.JobRevision.Of(new ManagedJobs(rig.Gw, () => rig.Now).Reclassify("Echo job")!), revision);

        rig.Now = ComposeSamples.Now.AddMinutes(45);
        T.Eq("cli --reapply: exit", 0, rig.Run("--reapply", "tst00001", "--yes"));
        rig.Run("--probe");
        using JsonDocument after = Json(rig.Text);
        T.Eq("a re-apply is a write by the command line", CliFormat.Iso(ComposeSamples.Now.AddMinutes(45)), Str(FirstJob(after), "updated"));
        T.True("and it moved the revision", Str(FirstJob(after), "revision") != revision);
        T.Eq("the creator survives it", "app", Str(FirstJob(after), "createdBy"));

        // A no-op apply writes nothing: same revision, same stamp.
        string rev2 = Str(FirstJob(after), "revision");
        rig.Gw.Writes.Clear();
        rig.Now = ComposeSamples.Now.AddMinutes(60);
        T.Eq("no-op apply: exit", 0, rig.Run("--apply", rig.WriteFile("echo2b.json", changed), "--yes"));
        T.Eq("no-op apply: no write", 0, rig.Gw.Writes.Count);
        rig.Run("--probe");
        using JsonDocument noop = Json(rig.Text);
        T.Eq("no-op apply: same revision", rev2, Str(FirstJob(noop), "revision"));
    }

    public static void AJobWrittenByV010ReadsAsUnknownAndStaysInSync()
    {
        using var rig = new Rig();
        rig.Gw.Seed(StockName, Stock());   // composed without an actor: exactly the text v0.1.0 wrote (no createdBy, updatedBy or agentLock)
        T.True("the fixture has none of the new fields", !rig.Gw.XmlOf(StockName)!.Contains("updatedBy", StringComparison.Ordinal) && !rig.Gw.XmlOf(StockName)!.Contains("agentLock", StringComparison.Ordinal));
        rig.Run("--probe");
        using JsonDocument doc = Json(rig.Text);
        JsonElement j = FirstJob(doc);
        T.Eq("sync", "inSync", Str(j, "sync"));
        T.Eq("ownership", "managed", Str(j, "ownership"));
        T.Eq("createdBy", "unknown", Str(j, "createdBy"));
        T.Eq("updatedBy", "unknown", Str(j, "updatedBy"));
        T.Eq("created is still shown", "2026-10-01T09:00:00-04:00", Str(j, "created"));
        T.Eq("updated is still shown", "2026-10-01T09:00:00-04:00", Str(j, "updated"));
        T.Eq("agentLock false", false, j.GetProperty("agentLock").GetBoolean());
        T.Eq("revision present", 12, Str(j, "revision").Length);
        T.Eq("the keys come in a stable order after the problem field", "problem|created|createdBy|updated|updatedBy|revision|agentLock|schedule",
            string.Join("|", j.EnumerateObject().Select(p => p.Name).SkipWhile(n => n != "problem").Take(8)));

        // Garbage in updatedBy (a hand edit) reads as unknown too.
        rig.Gw.Seed(StockName, EditMeta(Stock(), m => m with { UpdatedBy = "hacker??", CreatedBy = "agent:" }));
        rig.Run("--probe");
        using JsonDocument odd = Json(rig.Text);
        T.Eq("garbage updatedBy: unknown", "unknown", Str(FirstJob(odd), "updatedBy"));
        T.Eq("an agent token without a name: unknown", "unknown", Str(FirstJob(odd), "createdBy"));
        T.Eq("and it is still in sync", "inSync", Str(FirstJob(odd), "sync"));
    }

    public static void AgentLockInTheJobFile()
    {
        using var rig = new Rig();
        string locked = EchoJson.Replace("\"name\"", "\"agentLock\":true,\"name\"", StringComparison.Ordinal);
        T.Eq("apply with agentLock true: exit", 0, rig.Run("--apply", rig.WriteFile("locked.json", locked), "--yes"));
        rig.Run("--probe");
        using (JsonDocument doc = Json(rig.Text))
        {
            T.Eq("probe shows the lock", true, FirstJob(doc).GetProperty("agentLock").GetBoolean());
            T.Eq("the lock does not make the job drift", "inSync", Str(FirstJob(doc), "sync"));
        }

        // A file that says nothing about agentLock leaves the owner's lock alone.
        string changed = EchoJson.Replace("0 9 * * 1-5", "15 9 * * 1-5", StringComparison.Ordinal);
        T.Eq("apply without agentLock: exit", 0, rig.Run("--apply", rig.WriteFile("plain.json", changed), "--yes"));
        rig.Run("--probe");
        using (JsonDocument doc = Json(rig.Text))
        {
            T.Eq("the cron changed", "15 9 * * 1-5", FirstJob(doc).GetProperty("cron")[0].GetString());
            T.Eq("the lock stayed", true, FirstJob(doc).GetProperty("agentLock").GetBoolean());
        }

        string unlocked = changed.Replace("\"name\"", "\"agentLock\":false,\"name\"", StringComparison.Ordinal);
        T.Eq("apply with agentLock false: exit", 0, rig.Run("--apply", rig.WriteFile("unlocked.json", unlocked), "--yes"));
        T.True("it says what changed", rig.Text.StartsWith("OK: Saved", StringComparison.Ordinal), rig.Text);
        rig.Run("--probe");
        using (JsonDocument doc = Json(rig.Text))
            T.Eq("the lock is cleared", false, FirstJob(doc).GetProperty("agentLock").GetBoolean());

        // Attribution fields copied out of a task's metadata are accepted and ignored; typos are still rejected.
        string copied = changed.Replace("\"name\"", "\"createdBy\":\"agent:Someone\",\"updatedBy\":\"app\",\"name\"", StringComparison.Ordinal);
        T.Eq("createdBy / updatedBy in the file are ignored: exit", 0, rig.Run("--apply", rig.WriteFile("copied.json", copied), "--yes"));
        T.True("a no-op apply of a copied job file writes nothing", !rig.Text.Contains("Saved", StringComparison.Ordinal), rig.Text);
        string typo = EchoJson.Replace("\"name\"", "\"agentLok\":true,\"name\"", StringComparison.Ordinal);
        T.Eq("a typo is still an error", 1, rig.Run("--apply", rig.WriteFile("typo.json", typo), "--yes"));
        T.True("typo: named", rig.Text.Contains("agentLok", StringComparison.Ordinal), rig.Text);
        string wrongType = EchoJson.Replace("\"name\"", "\"agentLock\":\"yes\",\"name\"", StringComparison.Ordinal);
        T.Eq("agentLock must be true or false", 1, rig.Run("--apply", rig.WriteFile("wrong.json", wrongType), "--yes"));
        T.True("wrong type: says so", rig.Text.Contains("job.agentLock must be true or false", StringComparison.Ordinal), rig.Text);
    }

    public static void SelfTestPassesAndReportsEachCheck()
    {
        string local = Path.Combine(Path.GetTempPath(), "CadenceSelfTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(local);
        try
        {
            var sb = new StringBuilder();
            string root = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
            int rc = SelfTest.Run(new FakeGateway(), local, root, t => sb.Append(t));
            string[] lines = sb.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            int pass = lines.Count(l => l.StartsWith("PASS ", StringComparison.Ordinal));
            T.Eq("exit code", 0, rc);
            T.True("at least 9 PASS lines (" + pass + ")", pass >= 9);
            T.Eq("no FAIL line", 0, lines.Count(l => l.StartsWith("FAIL", StringComparison.Ordinal)));
            T.Eq("last line is the count", "selftest: " + pass + " passed, 0 failed", lines[^1]);
            T.Eq("the writable check left no file behind", 0, Directory.GetFileSystemEntries(local).Length);
            T.True("the output uses CRLF", sb.ToString().EndsWith("\r\n", StringComparison.Ordinal) && !sb.ToString().Replace("\r\n", "", StringComparison.Ordinal).Contains('\n', StringComparison.Ordinal));
        }
        finally { Directory.Delete(local, recursive: true); }
    }

    public static void SelfTestFailuresAreReportedAndDoNotStopTheOthers()
    {
        var sb = new StringBuilder();
        string missing = Path.Combine(Path.GetTempPath(), "CadenceNoSuchDir-" + Guid.NewGuid().ToString("N"));
        int rc = SelfTest.Run(new FakeGateway(), missing, missing, t => sb.Append(t));
        string[] lines = sb.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        T.Eq("exit code", 1, rc);
        T.True("the writable check failed", lines.Any(l => l.StartsWith("FAIL %LOCALAPPDATA% is writable", StringComparison.Ordinal)));
        T.True("the tools check failed", lines.Any(l => l.StartsWith("FAIL conhost.exe and powershell.exe exist", StringComparison.Ordinal)));
        T.True("the engine checks still ran and passed", lines.Count(l => l.StartsWith("PASS ", StringComparison.Ordinal)) >= 8);
        T.True("the summary counts the failures", lines[^1].EndsWith("2 failed", StringComparison.Ordinal), lines[^1]);

        sb.Clear();
        rc = SelfTest.Run(new BrokenGateway(), Path.GetTempPath(), Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows", t => sb.Append(t));
        T.Eq("a gateway that cannot connect: exit code", 1, rc);
        T.True("the connect check says why", sb.ToString().Contains("FAIL Task Scheduler connects (read-only): threw SchedulerException", StringComparison.Ordinal), sb.ToString());
    }

    sealed class BrokenGateway : ITaskSchedulerGateway
    {
        static SchedulerException Down() => new(SchedulerErrorKind.Unavailable, "The Task Scheduler service is not running.");
        public SchedulerOptions Options => new();
        public string ConnectedUserId => throw Down();
        public string ConnectedUserSid => throw Down();
        public bool IsConnectedUser(string? p) => false;
        public IReadOnlyList<RegisteredTaskInfo> ListManagedFolder() => throw Down();
        public IReadOnlyList<RegisteredTaskInfo> ListOtherTasks(int max = 5000) => throw Down();
        public RegisteredTaskInfo? Get(string taskName) => throw Down();
        public RegisteredTaskInfo Register(string taskName, string xml, RegisterMode mode = RegisterMode.CreateOrUpdate) => throw Down();
        public void Delete(string taskName) => throw Down();
        public void Run(string taskName) => throw Down();
        public void Stop(string taskName) => throw Down();
        public void DeleteFolderIfEmpty() => throw Down();
    }

    // ---- formatting helpers ----

    public static void DurationText()
    {
        T.Eq("0", "0 ms", CliFormat.Duration(TimeSpan.Zero));
        T.Eq("negative", "0 ms", CliFormat.Duration(TimeSpan.FromSeconds(-5)));
        T.Eq("38 ms", "38 ms", CliFormat.Duration(TimeSpan.FromMilliseconds(38)));
        T.Eq("999 ms", "999 ms", CliFormat.Duration(TimeSpan.FromMilliseconds(999)));
        T.Eq("1 s", "1.0 s", CliFormat.Duration(TimeSpan.FromSeconds(1)));
        T.Eq("1.5 s", "1.5 s", CliFormat.Duration(TimeSpan.FromSeconds(1.5)));
        T.Eq("9.94 s", "9.9 s", CliFormat.Duration(TimeSpan.FromSeconds(9.94)));
        T.Eq("10 s", "10 s", CliFormat.Duration(TimeSpan.FromSeconds(10)));
        T.Eq("59.9 s", "59 s", CliFormat.Duration(TimeSpan.FromSeconds(59.9)));
        T.Eq("60 s", "1 min 0 s", CliFormat.Duration(TimeSpan.FromSeconds(60)));
        T.Eq("125 s", "2 min 5 s", CliFormat.Duration(TimeSpan.FromSeconds(125)));
        T.Eq("1 h", "1 h 0 min", CliFormat.Duration(TimeSpan.FromHours(1)));
        T.Eq("3 h 3 min", "3 h 3 min", CliFormat.Duration(TimeSpan.FromMinutes(183)));
    }

    public static void NamesAndBadge()
    {
        T.Eq("ok", "ok", CliFormat.StatusName(RunStatus.Ok));
        T.Eq("failed", "failed", CliFormat.StatusName(RunStatus.Failed));
        T.Eq("timedOut", "timedOut", CliFormat.StatusName(RunStatus.TimedOut));
        T.Eq("launchError", "launchError", CliFormat.StatusName(RunStatus.LaunchError));
        T.Eq("skipped", "skipped", CliFormat.StatusName(RunStatus.Skipped));
        T.Eq("running", "running", CliFormat.StatusName(RunStatus.Running));
        T.Eq("notReported", "notReported", CliFormat.StatusName(RunStatus.NotReported));
        T.Eq("calendar", "usFederal", CliFormat.CalendarName(Cadence.Core.Model.HolidayCalendar.UsFederal));
        T.Eq("calendar none", "none", CliFormat.CalendarName(Cadence.Core.Model.HolidayCalendar.None));
        T.Eq("hresult", "0x80070005", CliFormat.HResult(unchecked((int)0x80070005)));
        T.Eq("hresult none", "", CliFormat.HResult(null));

        var when = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.FromHours(-4));
        RunView Row(RunStatus s) => new("r", when, null, s, null, null, false);
        JobSummary Sum(RunStatus? last) => new(last, when, null, null, 1);
        T.Eq("empty history", "none", CliFormat.Badge(JobHistory.Empty));
        T.Eq("last ok", "ok", CliFormat.Badge(new JobHistory([Row(RunStatus.Ok)], Sum(RunStatus.Ok))));
        T.Eq("a newest skip is neutral, not a failure", "skipped", CliFormat.Badge(new JobHistory([Row(RunStatus.Skipped), Row(RunStatus.Failed)], Sum(RunStatus.Failed))));
        T.Eq("an older skip does not hide the last result", "failed", CliFormat.Badge(new JobHistory([Row(RunStatus.Failed), Row(RunStatus.Skipped)], Sum(RunStatus.Failed))));
        T.Eq("not reported", "notReported", CliFormat.Badge(new JobHistory([Row(RunStatus.NotReported)], Sum(RunStatus.NotReported))));

        T.Eq("Iso offset", "2026-10-01T09:00:00-04:00", CliFormat.Iso(when));
        T.True("Iso of 'never' is null", CliFormat.Iso((DateTime?)new DateTime(1999, 11, 30)) is null && CliFormat.Iso((DateTime?)null) is null);
        T.True("Iso of a date has an offset", System.Text.RegularExpressions.Regex.IsMatch(CliFormat.Iso((DateTime?)new DateTime(2026, 10, 1, 9, 0, 0)) ?? "", @"^2026-10-01T09:00:00[+-]\d\d:\d\d$"));
        T.Eq("plain diff", "Time 18:00 -> 19:00", CliFormat.Plain(new Cadence.Core.Planning.FieldDiff("Time", "18:00", "19:00")));
    }

    public static void HistoryDocumentsAreStable()
    {
        var when = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.FromHours(-4));
        var rows = new[]
        {
            new RunView("20261001-090000-000", when, TimeSpan.FromMilliseconds(1500), RunStatus.Ok, 0, null, true),
            new RunView("20260930-090000-000", when.AddDays(-1), null, RunStatus.LaunchError, null, "line one\r\nline \"two\"", false),
        };
        var h = new JobHistory(rows, new JobSummary(RunStatus.Ok, when, TimeSpan.FromMilliseconds(1500), 0.5, 2));

        using JsonDocument doc = Json(CliFormat.HistoryJson(h));
        T.Eq("two top-level keys", "summary|recent", string.Join("|", doc.RootElement.EnumerateObject().Select(p => p.Name)));
        T.Eq("summary keys", "lastResult|lastRunAt|averageRuntimeMs|successRate|sampleSize", string.Join("|", doc.RootElement.GetProperty("summary").EnumerateObject().Select(p => p.Name)));
        T.Eq("row keys", "runId|when|durationMs|status|exitCode|message|hasOutput", string.Join("|", doc.RootElement.GetProperty("recent")[0].EnumerateObject().Select(p => p.Name)));
        T.Eq("message text survives escaping", "line one\r\nline \"two\"", doc.RootElement.GetProperty("recent")[1].GetProperty("message").GetString());
        T.Eq("when", "2026-10-01T09:00:00-04:00", doc.RootElement.GetProperty("recent")[0].GetProperty("when").GetString());

        string text = CliFormat.HistoryText("Job", "abcd1234", h);
        string[] lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        T.Eq("title", "Job  [abcd1234]", lines[0]);
        T.Eq("summary", "last result: ok; success 50% of 2 run(s); average 1.5 s", lines[1]);
        T.Eq("ok row", "2026-10-01 09:00:00 -04:00 " + "ok".PadRight(12) + " " + "0".PadRight(5) + " 1.5 s", lines[3]);
        T.True("a multi-line message stays on one line", lines[4].EndsWith("line one line \"two\"", StringComparison.Ordinal), lines[4]);
        T.Eq("4 lines in all", 5, lines.Length);
    }
}
