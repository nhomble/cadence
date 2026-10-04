using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cadence.Mcp;
using Cadence.Agent.Tools;
using Cadence.Core.Cron;
using Cadence.Core.Execution;
using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.Core.TaskXml;
using Cadence.Core.Text;
using Cadence.Scheduler;
using Cadence.TestHarness;

namespace Cadence.Tests.Mcp;

/// <summary>
/// Phase 18: the six read-only tools over a fake gateway and a TEMP runs root. Foreign tasks must be invisible and unfetchable, environment
/// values never returned, run statuses taken from run records, the preview must never touch the gateway, and long output must be cut with a marker.
/// </summary>
public static class ReadToolsTests
{
    const string StockId = "d4dst0ck";
    const string StockName = "Stock ingest (Dad)";
    const string BackupId = "a1b2c3d4";
    const string BackupName = "Backup notes";

    static string? Str(JsonNode? n) => n?.GetValue<string>();

    static JsonArray Arr(JsonNode? n) => n!.AsArray();

    static JsonObject Seeded(out ReadRig rig)
    {
        rig = new ReadRig();
        rig.Seed(ReadRig.Stock());
        rig.Seed(PlanSamples.Job(BackupName, BackupId, "30 2 * * *"));
        return rig.Ok(ToolCatalog.ListJobs);
    }

    static void ErrorCase(ReadRig rig, string label, string tool, string args, string mentions)
    {
        ToolResult r = rig.Call(tool, args);
        T.True(label + ": isError", r.IsError);
        T.Eq(label + ": code", "invalid_argument", ReadRig.Code(r));
        T.True(label + ": names '" + mentions + "' (" + r.Text + ")", r.Text.Contains(mentions, StringComparison.Ordinal));
        T.Eq(label + ": structured message equals the text", r.Text, Str(r.Structured?["error"]?["message"]));
    }

    // ---- status ----

    public static void StatusCountsOnlyManagedJobs()
    {
        using var rig = new ReadRig();
        rig.Seed(ReadRig.Stock());
        rig.Seed(PlanSamples.Job(BackupName, BackupId, "30 2 * * *"));
        rig.SeedForeign();
        rig.Fake.SeedOther("\\Microsoft\\Windows\\Foo\\Bar", PlanSamples.Plain());
        ToolResult r = rig.Call(ToolCatalog.GetStatus);
        T.True("not an error", !r.IsError);
        JsonObject s = r.Structured!;
        T.Eq("jobs: the two managed ones", 2, s["jobs"]!.GetValue<int>());
        T.Eq("folder", "\\Cadence", Str(s["folder"]));
        T.Eq("cadenceVersion", "v0.0.0-test", Str(s["cadenceVersion"]));
        T.Eq("protocolVersion", "2025-11-25", Str(s["protocolVersion"]));
        T.Eq("timeZone", "UTC", Str(s["timeZone"]));
        T.Eq("now", "2026-10-01T13:00:00+00:00", Str(s["now"]));
        T.Eq("you", "Test Agent", Str(s["you"]));
        T.Eq("minimum interval default", 5, s["agents"]!["minIntervalMinutes"]!.GetValue<int>());
        T.Eq("text mirrors the structured content", s.ToJsonString(), JsonNode.Parse(r.Text)!.ToJsonString());
        T.True("only the managed folder was listed", rig.Gw.Calls.All(c => c == "ListManagedFolder"));
    }

    public static void StatusReportsAFailingTaskScheduler()
    {
        // A gateway whose listing throws: the tool reports task_scheduler_error instead of crashing.
        var failing = new CadenceToolHost(new AgentEnvironment(new ThrowingGateway(), new Cadence.History.RunRecordStore(Path.Combine(Path.GetTempPath(), "cadence-nonexistent-runs")),
            () => ReadRig.FixedNow, TimeZoneInfo.Utc, new NoProbe(), "v0", new Cadence.Agent.Policy.MemoryAgentPolicyStore(), Cadence.Agent.Audit.NullAuditLog.Instance));
        using JsonDocument d = JsonDocument.Parse("{}");
        foreach (string tool in new[] { ToolCatalog.GetStatus, ToolCatalog.ListJobs })
        {
            ToolResult r = failing.Call(tool, d.RootElement, ReadRig.Context);
            T.True(tool + ": isError", r.IsError);
            T.Eq(tool + ": code", "task_scheduler_error", ReadRig.Code(r));
            T.True(tool + ": says nothing was changed", r.Text.Contains("Nothing was changed", StringComparison.Ordinal));
        }
    }

    internal sealed class ThrowingGateway : ITaskSchedulerGateway
    {
        public SchedulerOptions Options { get; } = new("Cadence", "Cadence");
        public string ConnectedUserId => FakeGateway.User;
        public string ConnectedUserSid => FakeGateway.Sid;
        public bool IsConnectedUser(string? principalUserId) => false;
        public IReadOnlyList<RegisteredTaskInfo> ListManagedFolder() => throw new SchedulerException(SchedulerErrorKind.Unavailable, "unavailable");
        public IReadOnlyList<RegisteredTaskInfo> ListOtherTasks(int max = 5000) => throw new NotSupportedException();
        public RegisteredTaskInfo? Get(string taskName) => throw new NotSupportedException();
        public RegisteredTaskInfo Register(string taskName, string xml, RegisterMode mode = RegisterMode.CreateOrUpdate) => throw new NotSupportedException();
        public void Delete(string taskName) => throw new NotSupportedException();
        public void Run(string taskName) => throw new NotSupportedException();
        public void Stop(string taskName) => throw new NotSupportedException();
        public void DeleteFolderIfEmpty() => throw new NotSupportedException();
    }

    // ---- list ----

    public static void ListJobsShape()
    {
        JsonObject list = Seeded(out ReadRig rig);
        using (rig)
        {
            T.Eq("folder", "\\Cadence", Str(list["folder"]));
            T.Eq("count", 2, list["count"]!.GetValue<int>());
            JsonArray jobs = Arr(list["jobs"]);
            T.Eq("sorted by name: first", BackupName, Str(jobs[0]!["name"]));
            T.Eq("sorted by name: second", StockName, Str(jobs[1]!["name"]));

            JsonNode b = jobs[0]!;
            T.Eq("backup id", BackupId, Str(b["jobId"]));
            T.Eq("backup enabled", true, b["enabled"]!.GetValue<bool>());
            T.Eq("backup state", "ok", Str(b["state"]));
            T.Eq("backup sentence", EnglishRenderer.Describe(PlanSamples.Job(BackupName, BackupId, "30 2 * * *").Schedule), Str(b["schedule"]));
            T.Eq("backup next run (UTC, tomorrow)", "2026-10-02T02:30:00+00:00", Str(b["nextRun"]!["at"]));
            T.Eq("backup next run text", "Tomorrow 2:30", Str(b["nextRun"]!["text"]));
            T.Eq("backup last result", "none", Str(b["lastResult"]));
            T.Eq("backup updated by", "unknown", Str(b["updated"]!["by"]));
            T.Eq("backup updated byKind", "unknown", Str(b["updated"]!["byKind"]));
            T.Eq("backup updated at", "2026-10-01T13:00:00+00:00", Str(b["updated"]!["at"]));
            T.Eq("backup locked", false, b["lockedForAgents"]!.GetValue<bool>());
            T.True("backup problem null", b["problem"] is null);

            JsonNode s = jobs[1]!;
            T.Eq("stock sentence is the plan's", "Every weekday at 18:00, except NYSE market holidays", Str(s["schedule"]));
            T.Eq("stock next run text", "Today 18:00", Str(s["nextRun"]!["text"]));
            T.Eq("stock next run", "2026-10-01T18:00:00+00:00", Str(s["nextRun"]!["at"]));
        }
    }

    public static void ListJobsNameFilter()
    {
        JsonObject all = Seeded(out ReadRig rig);
        using (rig)
        {
            T.Eq("no filter: 2", 2, all["count"]!.GetValue<int>());
            JsonObject one = rig.Ok(ToolCatalog.ListJobs, "{\"nameContains\":\"STOCK\"}");
            T.Eq("case-insensitive: 1", 1, one["count"]!.GetValue<int>());
            T.Eq("it is the stock job", StockId, Str(Arr(one["jobs"])[0]!["jobId"]));
            JsonObject none = rig.Ok(ToolCatalog.ListJobs, "{\"nameContains\":\"zzz\"}");
            T.Eq("no match: 0", 0, none["count"]!.GetValue<int>());
            T.Eq("no match: empty array", 0, Arr(none["jobs"]).Count);
            T.Eq("an empty filter lists all", 2, rig.Ok(ToolCatalog.ListJobs, "{\"nameContains\":\"\"}")["count"]!.GetValue<int>());
            T.Eq("a name needing JSON escaping survives", 1, rig.Ok(ToolCatalog.ListJobs, "{\"nameContains\":\"(Dad)\"}")["count"]!.GetValue<int>());
        }
    }

    public static void JobNamesWithSpecialCharactersAreEscapedByTheJsonWriter()
    {
        using var rig = new ReadRig();
        const string tricky = "Say \"hi\" \\ done <b>&</b>";
        rig.Seed(PlanSamples.Job(tricky, "q1w2e3r4", "0 8 * * *"), "Say hi task");
        ToolResult r = rig.Call(ToolCatalog.ListJobs);
        T.Eq("the name round-trips through the text", tricky, Str(Arr(JsonNode.Parse(r.Text)!["jobs"])[0]!["name"]));
        T.Eq("and through the structure", tricky, Str(Arr(r.Structured!["jobs"])[0]!["name"]));
    }

    // ---- foreign tasks ----

    public static void ForeignTasksAreInvisibleAndUnfetchable()
    {
        using var rig = new ReadRig();
        rig.Seed(ReadRig.Stock());
        rig.SeedForeign();                            // a name that is itself shaped like a job id
        rig.SeedForeign("Dad's backup");
        rig.Fake.SeedOther("\\Microsoft\\Windows\\Foo\\bar12345", PlanSamples.Plain());
        var outputs = new List<string>();

        JsonObject list = rig.Ok(ToolCatalog.ListJobs);
        outputs.Add(list.ToJsonString());
        T.Eq("list: only the managed job", 1, list["count"]!.GetValue<int>());
        T.Eq("status: only the managed job", 1, rig.Ok(ToolCatalog.GetStatus)["jobs"]!.GetValue<int>());

        // by id: the foreign task's name has the shape of an id, and ids are guessed
        string[] guesses = [ReadRig.ForeignName, "bar12345", "aaaaaaaa", "00000000", "zzzzzzzz", "d4dst0cl", "12345678", "abcdefgh"];
        foreach (string guess in guesses)
        {
            ToolResult g = rig.Call(ToolCatalog.GetJob, "{\"jobId\":\"" + guess + "\"}");
            T.True("get_job '" + guess + "' is an error", g.IsError);
            T.Eq("get_job '" + guess + "' is not_found", "not_found", ReadRig.Code(g));
            outputs.Add(g.Text);
            ToolResult o = rig.Call(ToolCatalog.GetRunOutput, "{\"jobId\":\"" + guess + "\",\"runId\":\"20261001-180002-117\"}");
            T.Eq("get_run_output '" + guess + "' is not_found", "not_found", ReadRig.Code(o));
            outputs.Add(o.Text);
        }

        // by name: not an id, refused by the schema pattern
        foreach (string name in new[] { "Dad's backup", "Stock ingest (Dad)", "\\Cadence\\foreign1", "foreign1\n" })
        {
            ToolResult g = rig.Call(ToolCatalog.GetJob, "{\"jobId\":" + JsonSerializer.Serialize(name) + "}");
            T.True("get_job by the name '" + name.Trim() + "' is refused", g.IsError && ReadRig.Code(g) == "invalid_argument");
            outputs.Add(g.Text);
        }

        // The listings never mention a foreign task; no output ever carries foreign content (an error may echo the id or name the caller typed).
        string listing = list.ToJsonString() + rig.Call(ToolCatalog.GetStatus).Text;
        foreach (string secret in new[] { ReadRig.ForeignName, "Dad's backup", "bar12345", "not ours", "Someone" })
            T.True("the listing never mentions '" + secret + "'", !listing.Contains(secret, StringComparison.Ordinal));
        foreach (string text in outputs)
            T.True("no foreign content in an output", !text.Contains("not ours", StringComparison.Ordinal) && !text.Contains("Someone", StringComparison.Ordinal));
        T.True("the foreign tasks were never read one by one", rig.Gw.Calls.All(c => c == "ListManagedFolder"));
        T.Eq("nothing was written", 0, rig.Fake.Writes.Count);
    }

    public static void DamagedAndNewerVersionTasksAreListedWithTheirProblemButOnlyAddressableWhenTheyHaveAnId()
    {
        using var rig = new ReadRig();
        string stock = rig.Seed(ReadRig.Stock());
        string xml = rig.Fake.XmlOf(stock)!;

        // damaged: the metadata is broken, so there is no job id
        JobDefinition other = PlanSamples.Job("Broken one", "br0k3n01", "0 8 * * *");
        string brokenXml = PlanSamples.Edit(PlanSamples.Compose(other, "Broken one", rig.Now), m => m with { RegistrationInfo = m.RegistrationInfo with { Documentation = "cadence/1\n{broken" } });
        rig.Fake.Seed("Broken one", brokenXml);

        // newer version: written by a later Cadence; the metadata still decodes
        JobDefinition newer = PlanSamples.Job("From the future", "n3w3rver", "0 9 * * *");
        rig.Fake.Seed("From the future", PlanSamples.EditMeta(PlanSamples.Compose(newer, "From the future", rig.Now), m => m with { Gen = m.Gen + 1 }));

        // drifted: someone edited the action in Task Scheduler
        JobDefinition drift = PlanSamples.Job("Edited by hand", "dr1ft001", "0 7 * * *");
        rig.Fake.Seed("Edited by hand", PlanSamples.Edit(PlanSamples.Compose(drift, "Edited by hand", rig.Now),
            m => m with { ExecActions = new ValueList<ExecActionModel>(m.ExecActions.Select(a => a with { Arguments = "--changed" })) }));

        // a copy made outside Cadence: same job id, another task name
        rig.Fake.Seed("Stock ingest copy", xml);

        JsonObject list = rig.Ok(ToolCatalog.ListJobs);
        JsonNode Row(string name) => Arr(list["jobs"]).First(j => Str(j!["name"]) == name && Str(j["state"]) != "copy")!;
        T.Eq("five rows", 5, list["count"]!.GetValue<int>());

        JsonNode broken = Row("Broken one");
        T.Eq("damaged: state", "damaged", Str(broken["state"]));
        T.True("damaged: no jobId (it cannot be addressed)", broken["jobId"] is null);
        T.True("damaged: says what is wrong", !string.IsNullOrEmpty(Str(broken["problem"])));
        T.True("damaged: no schedule or next run", broken["schedule"] is null && broken["nextRun"] is null);

        JsonNode fut = Row("From the future");
        T.Eq("newer: state", "newerVersion", Str(fut["state"]));
        T.Eq("newer: has its id", "n3w3rver", Str(fut["jobId"]));
        T.True("newer: problem", !string.IsNullOrEmpty(Str(fut["problem"])));

        T.Eq("drifted: state", "changedOutsideCadence", Str(Row("Edited by hand")["state"]));
        T.True("drifted: problem", !string.IsNullOrEmpty(Str(Row("Edited by hand")["problem"])));

        JsonNode copy = Arr(list["jobs"]).Single(j => Str(j!["state"]) == "copy")!;
        T.Eq("copy: shows the job's own name and id (a copy has the original's metadata)", StockId, Str(copy["jobId"]));
        T.True("copy: problem says it is a copy", Str(copy["problem"])!.Contains("copy", StringComparison.Ordinal));
        T.Eq("copy: the original is ok", "ok", Str(Row(StockName)["state"]));

        JsonObject getNewer = rig.Ok(ToolCatalog.GetJob, "{\"jobId\":\"n3w3rver\"}");
        T.Eq("get_job of a newer-version job: state", "newerVersion", Str(getNewer["state"]));
        T.True("get_job of a newer-version job: problem", !string.IsNullOrEmpty(Str(getNewer["problem"])));

        JsonObject getDrift = rig.Ok(ToolCatalog.GetJob, "{\"jobId\":\"dr1ft001\"}");
        T.Eq("get_job of a drifted job: state", "changedOutsideCadence", Str(getDrift["state"]));
        T.True("get_job of a drifted job: problem", !string.IsNullOrEmpty(Str(getDrift["problem"])));

        JsonObject getStock = rig.Ok(ToolCatalog.GetJob, "{\"jobId\":\"" + StockId + "\"}");
        T.Eq("get_job of a duplicated id returns the original", "ok", Str(getStock["state"]));
        T.Eq("and its own task path", "\\Cadence\\" + StockName, Str(getStock["taskPath"]));
    }

    // ---- get_job ----

    public static void GetJobShowsTheFullView()
    {
        using var rig = new ReadRig();
        rig.Seed(ReadRig.Stock());
        ToolResult r = rig.Call(ToolCatalog.GetJob, "{\"jobId\":\"" + StockId + "\"}");
        T.True("not an error", !r.IsError);
        JsonObject j = r.Structured!;
        T.Eq("jobId", StockId, Str(j["jobId"]));
        T.Eq("name", StockName, Str(j["name"]));
        T.Eq("taskPath", "\\Cadence\\" + StockName, Str(j["taskPath"]));
        T.Eq("enabled", true, j["enabled"]!.GetValue<bool>());
        T.Eq("state", "ok", Str(j["state"]));
        T.True("problem null", j["problem"] is null);
        T.Eq("cron verbatim", "0 18 * * 1-5", Str(Arr(j["schedule"]!["cron"])[0]));
        T.Eq("skipHolidays", "nyse", Str(j["schedule"]!["skipHolidays"]));
        T.Eq("plain English", "Every weekday at 18:00, except NYSE market holidays", Str(j["schedule"]!["description"]));
        JsonArray next = Arr(j["nextRuns"]);
        T.Eq("five next runs", 5, next.Count);
        T.Eq("first next run", "2026-10-01T18:00:00+00:00", Str(next[0]!["at"]));
        T.Eq("first next run text", "Thu 1 Oct 18:00", Str(next[0]!["text"]));
        T.True("not skipped", next[0]!["skipped"] is null);
        T.Eq("no clock change", false, next[0]!["clockChange"]!.GetValue<bool>());
        JsonNode p = j["program"]!;
        T.Eq("program path", @"C:\Windows\py.exe", Str(p["path"]));
        T.True("arguments", Str(p["arguments"])!.Contains("ingest.py", StringComparison.Ordinal));
        T.Eq("working folder", @"C:\Users\Dad\stocks", Str(p["workingFolder"]));
        T.Eq("hidden", true, p["hidden"]!.GetValue<bool>());
        T.Eq("timeout minutes (1800 s)", 30, p["timeoutMinutes"]!.GetValue<int>());
        T.Eq("output encoding", "utf8", Str(p["outputEncoding"]));
        T.Eq("environment NAMES only", "PYTHONIOENCODING,API_KEY", string.Join(",", Arr(p["environmentNames"]).Select(n => Str(n))));
        T.Eq("created by", "unknown", Str(j["created"]!["by"]));
        T.Eq("updated byKind", "unknown", Str(j["updated"]!["byKind"]));
        T.Eq("locked", false, j["lockedForAgents"]!.GetValue<bool>());
        T.True("revision is 12 hex characters", System.Text.RegularExpressions.Regex.IsMatch(Str(j["revision"])!, "\\A[0-9a-f]{12}\\z"));
        T.Eq("lastResult none", "none", Str(j["lastResult"]!["status"]));
        T.Eq("stats: no runs yet", 0, j["stats"]!["sampleSize"]!.GetValue<int>());
        T.True("stats: success rate unknown", j["stats"]!["successRate"] is null);
        T.Eq("recentRuns empty", 0, Arr(j["recentRuns"]).Count);
        T.Eq("text mirrors the structured content", j.ToJsonString(), JsonNode.Parse(r.Text)!.ToJsonString());
    }

    public static void ADisabledJobHasNoNextRuns()
    {
        using var rig = new ReadRig();
        rig.Seed(ReadRig.Stock() with { Enabled = false });
        JsonObject j = rig.Ok(ToolCatalog.GetJob, "{\"jobId\":\"" + StockId + "\"}");
        T.Eq("enabled false", false, j["enabled"]!.GetValue<bool>());
        T.Eq("no next runs", 0, Arr(j["nextRuns"]).Count);
        JsonNode row = Arr(rig.Ok(ToolCatalog.ListJobs)["jobs"])[0]!;
        T.True("no nextRun in the list", row["nextRun"] is null);
    }

    // Phase 19: who changed a job and the owner's lock come from the metadata (the Phase 18 placeholders).
    public static void AttributionAndTheLockAreShownFromTheMetadata()
    {
        using var rig = new ReadRig();
        rig.Fake.Seed(StockName, PlanSamples.EditMeta(PlanSamples.Compose(ReadRig.Stock(), StockName, rig.Now), m => m with { CreatedBy = "app", UpdatedBy = "agent:Claude Code", Job = m.Job with { AgentLock = true } }));
        rig.Fake.Seed(BackupName, PlanSamples.EditMeta(PlanSamples.Compose(PlanSamples.Job(BackupName, BackupId, "30 2 * * *"), BackupName, rig.Now), m => m with { CreatedBy = "cli", UpdatedBy = "hacker??" }));

        JsonObject list = rig.Ok(ToolCatalog.ListJobs);
        JsonNode backup = Arr(list["jobs"])[0]!;
        JsonNode stock = Arr(list["jobs"])[1]!;
        T.Eq("list: agent-written job: by", "Claude Code", Str(stock["updated"]!["by"]));
        T.Eq("list: agent-written job: byKind", "agent", Str(stock["updated"]!["byKind"]));
        T.Eq("list: the lock is shown", true, stock["lockedForAgents"]!.GetValue<bool>());
        T.Eq("list: garbage updatedBy reads as unknown", "unknown", Str(backup["updated"]!["by"]));
        T.Eq("list: and as byKind unknown", "unknown", Str(backup["updated"]!["byKind"]));
        T.Eq("list: an unlocked job", false, backup["lockedForAgents"]!.GetValue<bool>());

        JsonObject s = rig.Ok(ToolCatalog.GetJob, "{\"jobId\":\"" + StockId + "\"}");
        T.Eq("get: created by", "Cadence (the app)", Str(s["created"]!["by"]));
        T.Eq("get: created byKind", "app", Str(s["created"]!["byKind"]));
        T.Eq("get: updated by", "Claude Code", Str(s["updated"]!["by"]));
        T.Eq("get: updated byKind", "agent", Str(s["updated"]!["byKind"]));
        T.Eq("get: locked", true, s["lockedForAgents"]!.GetValue<bool>());
        T.Eq("get: the revision is JobRevision.Of the live task", JobRevision.Of(new Cadence.Scheduler.ManagedJobs(rig.Fake, () => rig.Now).Reclassify(StockName)!), Str(s["revision"]));

        JsonObject b = rig.Ok(ToolCatalog.GetJob, "{\"jobId\":\"" + BackupId + "\"}");
        T.Eq("get: created by the command line", "the command line", Str(b["created"]!["by"]));
        T.Eq("get: created byKind cli", "cli", Str(b["created"]!["byKind"]));
        T.Eq("get: garbage is unknown", "unknown", Str(b["updated"]!["by"]));
        T.Eq("get: not locked", false, b["lockedForAgents"]!.GetValue<bool>());
        T.True("the agent code has no private copy of the revision any more", !File.ReadAllText(Path.Combine(RepoRootDir(), "src", "Cadence.Agent", "Tools", "JobViews.cs")).Contains("SHA256", StringComparison.Ordinal));
    }

    static string RepoRootDir()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Cadence.slnx"))) return d.FullName;
        throw new InvalidOperationException("Cadence.slnx not found above " + AppContext.BaseDirectory);
    }

    public static void TheRevisionChangesWithEveryChange()
    {
        using var rig = new ReadRig();
        string name = rig.Seed(ReadRig.Stock());
        string args = "{\"jobId\":\"" + StockId + "\"}";
        string r1 = Str(rig.Ok(ToolCatalog.GetJob, args)["revision"])!;
        T.Eq("stable while nothing changes", r1, Str(rig.Ok(ToolCatalog.GetJob, args)["revision"]));

        rig.Seed(ReadRig.Stock(), at: rig.Now.AddMinutes(5));   // a Cadence write: the updated stamp changes
        string r2 = Str(rig.Ok(ToolCatalog.GetJob, args)["revision"])!;
        T.True("a Cadence write changes it", r2 != r1);

        string xml = rig.Fake.XmlOf(name)!;
        rig.Fake.Seed(name, PlanSamples.Edit(xml, m => m with { ExecActions = new ValueList<ExecActionModel>(m.ExecActions.Select(a => a with { Arguments = "--hand" })) }));
        string r3 = Str(rig.Ok(ToolCatalog.GetJob, args)["revision"])!;
        T.True("a Task Scheduler side edit changes it", r3 != r2 && r3 != r1);

        // The formula of PLAN 12.1.3 (documentation text + newline + fingerprint), computed here independently.
        TaskDefinitionModel m = TaskXmlReader.Read(rig.Fake.XmlOf(name)!);
        string expected = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes((m.RegistrationInfo.Documentation ?? "") + "\n" + Fingerprint.Of(m))))[..12];
        T.Eq("equals the 12.1.3 formula", expected, r3);
    }

    public static void EnvironmentValuesNeverAppearInAnyOutput()
    {
        using var rig = new ReadRig();
        rig.Seed(ReadRig.Stock());
        rig.AddRun(StockId, StockName, "20260930-180002-117", "ok", 0, "fine\n");
        var texts = new List<string>
        {
            rig.Call(ToolCatalog.GetStatus).Text,
            rig.Call(ToolCatalog.ListJobs).Text,
            rig.Call(ToolCatalog.GetJob, "{\"jobId\":\"" + StockId + "\"}").Text,
            rig.Call(ToolCatalog.GetRunOutput, "{\"jobId\":\"" + StockId + "\",\"runId\":\"20260930-180002-117\"}").Text,
            rig.Call(ToolCatalog.PreviewSchedule, "{\"templateId\":\"hourly\"}").Text,
            rig.Call(ToolCatalog.ListTemplates).Text,
            rig.Call(ToolCatalog.GetJob, "{\"jobId\":\"zzzzzzzz\"}").Text,
        };
        T.True("the seed secret is not in the job's task XML as a name", rig.Fake.XmlOf(StockName)!.Contains("API_KEY", StringComparison.Ordinal));
        foreach (string t in texts)
            T.True("no secret value in an output", !t.Contains(ReadRig.Secret, StringComparison.Ordinal) && !t.Contains("utf-8", StringComparison.Ordinal));
        T.True("the names are shown", texts[2].Contains("\"API_KEY\"", StringComparison.Ordinal));
    }

    // ---- history ----

    public static void RecentRunsAndStatsComeFromTheRunRecords()
    {
        using var rig = new ReadRig();
        string name = rig.Seed(ReadRig.Stock());
        rig.AddRun(StockId, name, "20260928-180002-009", "ok", 0, "old\n");
        rig.AddRun(StockId, name, "20260929-180002-005", "skipped", null, msg: "holiday: Test Holiday");
        rig.AddRun(StockId, name, "20260930-180002-117", "ok", 0, "hello\nworld\n");
        rig.AddRun(StockId, name, "20260930-180100-001", "fail", 1, "x", "boom");
        rig.AddRun(StockId, name, "20261001-060000-001", "skipped", null, msg: "holiday: Test Holiday");
        // Task Scheduler's own number must never decide the status: it says "access denied" but the records are what count.
        rig.Fake.SetRuntime(name, TaskState.Ready, null, unchecked((int)0x80070005));

        JsonObject j = rig.Ok(ToolCatalog.GetJob, "{\"jobId\":\"" + StockId + "\"}");
        JsonArray runs = Arr(j["recentRuns"]);
        T.Eq("five rows, newest first", 5, runs.Count);
        T.Eq("row 0 is the newest (skipped)", "20261001-060000-001", Str(runs[0]!["runId"]));
        T.Eq("row 0 status", "skipped", Str(runs[0]!["status"]));
        T.Eq("row 0 message", "holiday: Test Holiday", Str(runs[0]!["message"]));
        T.True("row 0 has no duration", runs[0]!["durationSeconds"] is null);
        T.Eq("row 1 is the failure", "failed", Str(runs[1]!["status"]));
        T.Eq("row 1 exit code", 1, runs[1]!["exitCode"]!.GetValue<int>());
        T.Eq("row 1 has output", true, runs[1]!["hasOutput"]!.GetValue<bool>());
        T.Eq("row 1 duration (the program's, not the wrapper's)", 20.6, runs[1]!["durationSeconds"]!.GetValue<double>());
        T.Eq("row 1 at", "2026-09-30T18:01:00+00:00", Str(runs[1]!["at"]));
        T.Eq("row 2 ok", "ok", Str(runs[2]!["status"]));
        T.Eq("row 2 runId", "20260930-180002-117", Str(runs[2]!["runId"]));
        T.Eq("row 4 ok", "ok", Str(runs[4]!["status"]));

        T.Eq("lastResult skips the skipped row", "failed", Str(j["lastResult"]!["status"]));
        T.Eq("lastResult exit code", 1, j["lastResult"]!["exitCode"]!.GetValue<int>());
        T.Eq("stats: three counted runs", 3, j["stats"]!["sampleSize"]!.GetValue<int>());
        T.Eq("stats: success rate 2/3", 0.6667, j["stats"]!["successRate"]!.GetValue<double>());
        T.Eq("stats: average runtime", 20.6, j["stats"]!["averageRuntimeSeconds"]!.GetValue<double>());
        T.Eq("the list says failed too", "failed", Str(Arr(rig.Ok(ToolCatalog.ListJobs)["jobs"])[0]!["lastResult"]));

        T.Eq("recentRuns 2", 2, Arr(rig.Ok(ToolCatalog.GetJob, "{\"jobId\":\"" + StockId + "\",\"recentRuns\":2}")["recentRuns"]).Count);
        T.Eq("recentRuns 0", 0, Arr(rig.Ok(ToolCatalog.GetJob, "{\"jobId\":\"" + StockId + "\",\"recentRuns\":0}")["recentRuns"]).Count);
        T.Eq("recentRuns 0 keeps lastResult", "failed", Str(rig.Ok(ToolCatalog.GetJob, "{\"jobId\":\"" + StockId + "\",\"recentRuns\":0}")["lastResult"]!["status"]));
        T.Eq("text shown relative to now", "Yesterday 18:01", Str(runs[1]!["text"]));
    }

    public static void ABlockedLaunchShowsAsDidNotReport()
    {
        using var rig = new ReadRig();
        string name = rig.Seed(ReadRig.Stock());
        rig.AddRun(StockId, name, "20260930-180002-117", "ok", 0, "hello\n");
        // Task Scheduler says it started the task a day after the newest record and the launch was refused (0x80070005), and no record exists.
        rig.Fake.SetRuntime(name, TaskState.Ready, new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc), unchecked((int)0x80070005));
        JsonObject j = rig.Ok(ToolCatalog.GetJob, "{\"jobId\":\"" + StockId + "\"}");
        JsonNode first = Arr(j["recentRuns"])[0]!;
        T.Eq("synthesized row", "didNotReport", Str(first["status"]));
        T.Eq("its message is the plain sentence", TaskResultCodes.AccessDeniedLaunch, Str(first["message"]));
        T.Eq("lastResult", "didNotReport", Str(j["lastResult"]!["status"]));
        T.Eq("no output to fetch", false, first["hasOutput"]!.GetValue<bool>());
    }

    // ---- run output ----

    public static void RunOutputIsReadFromTheRunsRoot()
    {
        using var rig = new ReadRig();
        string name = rig.Seed(ReadRig.Stock());
        rig.AddRun(StockId, name, "20260930-180002-117", "ok", 0, "hello\nworld\n");
        rig.AddRun(StockId, name, "20260930-180100-001", "fail", 1, "x", "boom");
        rig.AddRun(StockId, name, "20260929-180002-005", "skipped", null, msg: "holiday: Test Holiday");
        T.True("the runs root is a temp folder", rig.RunsRoot.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase));

        JsonObject o = rig.Ok(ToolCatalog.GetRunOutput, "{\"jobId\":\"" + StockId + "\",\"runId\":\"20260930-180002-117\"}");
        T.Eq("stdout text", "hello\nworld\n", Str(o["text"]));
        T.Eq("stream", "stdout", Str(o["stream"]));
        T.Eq("not truncated", false, o["truncated"]!.GetValue<bool>());
        T.Eq("totalChars", 12, o["totalChars"]!.GetValue<int>());
        T.Eq("jobId echoed", StockId, Str(o["jobId"]));
        T.Eq("stderr", "boom", Str(rig.Ok(ToolCatalog.GetRunOutput, "{\"jobId\":\"" + StockId + "\",\"runId\":\"20260930-180100-001\",\"stream\":\"stderr\"}")["text"]));

        JsonObject none = rig.Ok(ToolCatalog.GetRunOutput, "{\"jobId\":\"" + StockId + "\",\"runId\":\"20260929-180002-005\"}");
        T.Eq("no output: empty text", "", Str(none["text"]));
        T.True("no output: the note says why", Str(none["note"])!.Contains("captured no output", StringComparison.Ordinal));
        JsonObject noErr = rig.Ok(ToolCatalog.GetRunOutput, "{\"jobId\":\"" + StockId + "\",\"runId\":\"20260930-180002-117\",\"stream\":\"stderr\"}");
        T.True("a stream the run did not print", Str(noErr["text"]) == "" && Str(noErr["note"])!.Contains("captured no output", StringComparison.Ordinal));

        File.Delete(Path.Combine(rig.RunsRoot, StockId, "20260930-180100-001.err.txt"));
        JsonObject pruned = rig.Ok(ToolCatalog.GetRunOutput, "{\"jobId\":\"" + StockId + "\",\"runId\":\"20260930-180100-001\",\"stream\":\"stderr\"}");
        T.True("a pruned file is said to be pruned", Str(pruned["note"])!.Contains("no longer stored", StringComparison.Ordinal));
    }

    public static void LongOutputIsCutWithAClearMarker()
    {
        using var rig = new ReadRig();
        string name = rig.Seed(ReadRig.Stock());
        string big = new string('a', 50_000) + "THE-END";
        rig.AddRun(StockId, name, "20260930-180002-117", "ok", 0, big);
        JsonObject o = rig.Ok(ToolCatalog.GetRunOutput, "{\"jobId\":\"" + StockId + "\",\"runId\":\"20260930-180002-117\"}");
        string text = Str(o["text"])!;
        T.Eq("truncated", true, o["truncated"]!.GetValue<bool>());
        T.Eq("totalChars is the real size", big.Length, o["totalChars"]!.GetValue<int>());
        T.True("starts with the marker", text.StartsWith("[truncated: showing the last 8000 of 50007 characters", StringComparison.Ordinal));
        T.True("ends with the END of the output", text.EndsWith("THE-END", StringComparison.Ordinal));
        T.True("small: about maxChars plus the marker (" + text.Length + ")", text.Length < 8000 + 200);

        JsonObject five = rig.Ok(ToolCatalog.GetRunOutput, "{\"jobId\":\"" + StockId + "\",\"runId\":\"20260930-180002-117\",\"maxChars\":5}");
        T.True("maxChars 5 gives the last 5 characters after the marker", Str(five["text"])!.EndsWith("\nE-END", StringComparison.Ordinal) && Str(five["text"])!.StartsWith("[truncated: showing the last 5 of 50007", StringComparison.Ordinal));
        JsonObject max = rig.Ok(ToolCatalog.GetRunOutput, "{\"jobId\":\"" + StockId + "\",\"runId\":\"20260930-180002-117\",\"maxChars\":65536}");
        T.Eq("65536 is allowed and returns it all", false, max["truncated"]!.GetValue<bool>());
        T.Eq("all of it", big, Str(max["text"]));
        ErrorCase(rig, "maxChars too big", ToolCatalog.GetRunOutput, "{\"jobId\":\"" + StockId + "\",\"runId\":\"20260930-180002-117\",\"maxChars\":65537}", "maxChars");

        // a cut that would land inside a surrogate pair starts after it
        rig.AddRun(StockId, name, "20260930-190000-001", "ok", 0, string.Concat(Enumerable.Repeat("\U0001F600", 3)));
        string e = Str(rig.Ok(ToolCatalog.GetRunOutput, "{\"jobId\":\"" + StockId + "\",\"runId\":\"20260930-190000-001\",\"maxChars\":5}")["text"])!;
        string after = e[(e.IndexOf('\n', StringComparison.Ordinal) + 1)..];
        T.True("the cut text starts with a whole character", after.Length > 0 && char.IsHighSurrogate(after[0]) && after.Length == 4);
    }

    public static void ARunOfAnotherJobOrAnInventedRunIsNotFound()
    {
        using var rig = new ReadRig();
        string name = rig.Seed(ReadRig.Stock());
        string other = rig.Seed(PlanSamples.Job(BackupName, BackupId, "30 2 * * *"));
        rig.AddRun(StockId, name, "20260930-180002-117", "ok", 0, "stock output\n");
        rig.AddRun(BackupId, other, "20260930-190000-001", "ok", 0, "backup output\n");

        ToolResult cross = rig.Call(ToolCatalog.GetRunOutput, "{\"jobId\":\"" + StockId + "\",\"runId\":\"20260930-190000-001\"}");
        T.True("another job's run id: error", cross.IsError);
        T.Eq("another job's run id: not_found", "not_found", ReadRig.Code(cross));
        T.True("and it does not leak the other job's text", !cross.Text.Contains("backup output", StringComparison.Ordinal));
        T.True("the message says where to find a run id", cross.Text.Contains("cadence_get_job", StringComparison.Ordinal));
        T.Eq("an invented run id: not_found", "not_found", ReadRig.Code(rig.Call(ToolCatalog.GetRunOutput, "{\"jobId\":\"" + StockId + "\",\"runId\":\"20200101-000000-000\"}")));
        T.Eq("a run file with no journal record: not_found", "not_found", ReadRig.Code(rig.Call(ToolCatalog.GetRunOutput, "{\"jobId\":\"" + StockId + "\",\"runId\":\"20260930-190000-001\"}")));

        // a file that exists on disk but is not in this job's journal is still not served
        File.WriteAllText(Path.Combine(rig.RunsRoot, StockId, "20250101-000000-000.out.txt"), "orphan");
        T.Eq("an orphan output file: not_found", "not_found", ReadRig.Code(rig.Call(ToolCatalog.GetRunOutput, "{\"jobId\":\"" + StockId + "\",\"runId\":\"20250101-000000-000\"}")));
    }

    public static void PathLikeIdsAreRefusedByThePattern()
    {
        using var rig = new ReadRig();
        rig.Seed(ReadRig.Stock());
        foreach (string bad in new[] { "..\\..\\x", "../../x", "20261001-180002-117\\..\\..\\y", "20261001-180002-117\n", "2026100-180002-117", "20261001-180002-1170", "" })
            ErrorCase(rig, "runId '" + bad.Replace("\n", "\\n", StringComparison.Ordinal).Replace("\\", "/", StringComparison.Ordinal) + "'", ToolCatalog.GetRunOutput,
                "{\"jobId\":\"" + StockId + "\",\"runId\":" + JsonSerializer.Serialize(bad) + "}", "runId");
        foreach (string bad in new[] { "_unknown", "..", "D4DST0CK", "d4dst0c", "d4dst0ckk", "d4dst0c\n", "d4/st0ck" })
            ErrorCase(rig, "jobId '" + bad.Replace("\n", "\\n", StringComparison.Ordinal) + "'", ToolCatalog.GetRunOutput,
                "{\"jobId\":" + JsonSerializer.Serialize(bad) + ",\"runId\":\"20261001-180002-117\"}", "jobId");
    }

    // ---- argument errors ----

    public static void EveryArgumentErrorIsAnIsErrorResultNamingTheField()
    {
        using var rig = new ReadRig();
        rig.Seed(ReadRig.Stock());
        const string id = "d4dst0ck";
        const string run = "20260930-180002-117";
        ErrorCase(rig, "status: unknown property", ToolCatalog.GetStatus, "{\"verbose\":true}", "verbose");
        ErrorCase(rig, "templates: unknown property", ToolCatalog.ListTemplates, "{\"x\":1}", "x");
        ErrorCase(rig, "list: unknown property", ToolCatalog.ListJobs, "{\"name\":\"a\"}", "name");
        ErrorCase(rig, "list: wrong type", ToolCatalog.ListJobs, "{\"nameContains\":5}", "nameContains");
        ErrorCase(rig, "list: too long", ToolCatalog.ListJobs, "{\"nameContains\":\"" + new string('a', 101) + "\"}", "nameContains");
        ErrorCase(rig, "get: missing id", ToolCatalog.GetJob, "{}", "jobId");
        ErrorCase(rig, "get: short id", ToolCatalog.GetJob, "{\"jobId\":\"abc\"}", "jobId");
        ErrorCase(rig, "get: upper-case id", ToolCatalog.GetJob, "{\"jobId\":\"D4DST0CK\"}", "jobId");
        ErrorCase(rig, "get: id of the wrong type", ToolCatalog.GetJob, "{\"jobId\":12345678}", "jobId");
        ErrorCase(rig, "get: recentRuns too big", ToolCatalog.GetJob, "{\"jobId\":\"" + id + "\",\"recentRuns\":51}", "recentRuns");
        ErrorCase(rig, "get: recentRuns negative", ToolCatalog.GetJob, "{\"jobId\":\"" + id + "\",\"recentRuns\":-1}", "recentRuns");
        ErrorCase(rig, "get: recentRuns fractional", ToolCatalog.GetJob, "{\"jobId\":\"" + id + "\",\"recentRuns\":2.5}", "recentRuns");
        ErrorCase(rig, "get: recentRuns a string", ToolCatalog.GetJob, "{\"jobId\":\"" + id + "\",\"recentRuns\":\"3\"}", "recentRuns");
        ErrorCase(rig, "get: unknown property", ToolCatalog.GetJob, "{\"jobId\":\"" + id + "\",\"revision\":\"x\"}", "revision");
        ErrorCase(rig, "output: missing runId", ToolCatalog.GetRunOutput, "{\"jobId\":\"" + id + "\"}", "runId");
        ErrorCase(rig, "output: missing jobId", ToolCatalog.GetRunOutput, "{\"runId\":\"" + run + "\"}", "jobId");
        ErrorCase(rig, "output: bad stream", ToolCatalog.GetRunOutput, "{\"jobId\":\"" + id + "\",\"runId\":\"" + run + "\",\"stream\":\"stdin\"}", "stream");
        ErrorCase(rig, "output: maxChars 0", ToolCatalog.GetRunOutput, "{\"jobId\":\"" + id + "\",\"runId\":\"" + run + "\",\"maxChars\":0}", "maxChars");
        ErrorCase(rig, "preview: both cron and templateId", ToolCatalog.PreviewSchedule, "{\"cron\":[\"0 9 * * *\"],\"templateId\":\"hourly\"}", "cron or templateId");
        ErrorCase(rig, "preview: neither", ToolCatalog.PreviewSchedule, "{}", "cron");
        ErrorCase(rig, "preview: only skipHolidays", ToolCatalog.PreviewSchedule, "{\"skipHolidays\":\"nyse\"}", "templateId");
        ErrorCase(rig, "preview: cron is a string", ToolCatalog.PreviewSchedule, "{\"cron\":\"0 9 * * *\"}", "cron");
        ErrorCase(rig, "preview: cron item empty", ToolCatalog.PreviewSchedule, "{\"cron\":[\"\"]}", "cron[0]");
        ErrorCase(rig, "preview: cron item not a string", ToolCatalog.PreviewSchedule, "{\"cron\":[\"0 9 * * *\",3]}", "cron[1]");
        ErrorCase(rig, "preview: cron empty array", ToolCatalog.PreviewSchedule, "{\"cron\":[]}", "cron");
        ErrorCase(rig, "preview: eleven lines", ToolCatalog.PreviewSchedule, "{\"cron\":[" + string.Join(",", Enumerable.Repeat("\"0 9 * * *\"", 11)) + "]}", "cron");
        ErrorCase(rig, "preview: unknown template", ToolCatalog.PreviewSchedule, "{\"templateId\":\"nope\"}", "templateId");
        ErrorCase(rig, "preview: bad skipHolidays", ToolCatalog.PreviewSchedule, "{\"cron\":[\"0 9 * * *\"],\"skipHolidays\":\"all\"}", "skipHolidays");
        ErrorCase(rig, "preview: count 0", ToolCatalog.PreviewSchedule, "{\"cron\":[\"0 9 * * *\"],\"count\":0}", "count");
        ErrorCase(rig, "preview: count 21", ToolCatalog.PreviewSchedule, "{\"cron\":[\"0 9 * * *\"],\"count\":21}", "count");
        ErrorCase(rig, "preview: unknown property", ToolCatalog.PreviewSchedule, "{\"cron\":[\"0 9 * * *\"],\"timezone\":\"UTC\"}", "timezone");

        // the message of an unknown template lists the valid ones; of a pattern failure says how to get a valid id
        T.True("unknown template lists the ids", rig.Call(ToolCatalog.PreviewSchedule, "{\"templateId\":\"nope\"}").Text.Contains("weekdays-6pm", StringComparison.Ordinal));
        T.True("a bad id says where ids come from", rig.Call(ToolCatalog.GetJob, "{\"jobId\":\"abc\"}").Text.Contains("cadence_list_jobs", StringComparison.Ordinal));
        T.Eq("no gateway call for any refused argument", 0, rig.Gw.Calls.Count);
    }

    public static void ArgumentsThatAreNotAnObjectAreAToolErrorHere()
    {
        // The server turns a non-object "arguments" into -32602 before the host sees it; the host itself still refuses it.
        using var rig = new ReadRig();
        using JsonDocument d = JsonDocument.Parse("[1]");
        ToolResult r = rig.Host.Call(ToolCatalog.ListJobs, d.RootElement, ReadRig.Context);
        T.True("array arguments: isError", r.IsError && ReadRig.Code(r) == "invalid_argument");
        ToolResult none = rig.Host.Call(ToolCatalog.GetStatus, default, ReadRig.Context);
        T.True("absent arguments are fine", !none.IsError);
    }

    // ---- preview ----

    static ReadRig LateRig() => new(new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero));

    public static void PreviewOfTheNyseWeekdaySchedule()
    {
        using ReadRig rig = LateRig();
        ToolResult r = rig.Call(ToolCatalog.PreviewSchedule, "{\"cron\":[\"0 18 * * 1-5\"],\"skipHolidays\":\"nyse\"}");
        T.True("not an error", !r.IsError);
        JsonObject p = r.Structured!;
        T.Eq("valid", true, p["valid"]!.GetValue<bool>());
        T.Eq("the sentence", "Every weekday at 18:00, except NYSE market holidays", Str(p["description"]));
        T.Eq("skipHolidays echoed", "nyse", Str(p["skipHolidays"]));
        T.Eq("cron echoed verbatim", "0 18 * * 1-5", Str(Arr(p["cron"])[0]));
        JsonArray runs = Arr(p["nextRuns"]);
        T.Eq("eight runs by default", 8, runs.Count);
        T.Eq("first: Fri 20 Nov", "2026-11-20T18:00:00+00:00", Str(runs[0]!["at"]));
        T.Eq("Thanksgiving is the fifth", "Thanksgiving Day", Str(runs[4]!["skipped"]));
        T.True("and its text says so (" + Str(runs[4]!["text"]) + ")", Str(runs[4]!["text"])!.StartsWith("Thu 26 Nov 18:00", StringComparison.Ordinal) && Str(runs[4]!["text"])!.Contains("skipped (Thanksgiving Day)", StringComparison.Ordinal));
        T.Eq("the others are not skipped", 7, runs.Count(x => x!["skipped"] is null));
        T.Eq("last run: Tue 1 Dec", "2026-12-01T18:00:00+00:00", Str(runs[7]!["at"]));
        T.Eq("minimum gap one day", 1440, p["minimumGapMinutes"]!.GetValue<int>());
        T.Eq("agents may save", true, p["agentsMaySave"]!.GetValue<bool>());
        T.True("no reason", p["agentsMaySaveReason"] is null);
        T.True("no alternative", p["closestAlternative"] is null);
        T.Eq("text mirrors the structured content", p.ToJsonString(), JsonNode.Parse(r.Text)!.ToJsonString());
    }

    public static void PreviewOfInvalidSchedulesIsANormalResultWithTheCompilersExplanation()
    {
        using ReadRig rig = LateRig();
        ToolResult r = rig.Call(ToolCatalog.PreviewSchedule, "{\"cron\":[\"*/90 * * * *\"]}");
        T.True("not isError: the input was well formed", !r.IsError);
        JsonObject p = r.Structured!;
        T.Eq("valid false", false, p["valid"]!.GetValue<bool>());
        T.True("no description", p["description"] is null);
        T.Eq("no runs", 0, Arr(p["nextRuns"]).Count);
        CompiledSchedule c = CronCompiler.Compile(new ScheduleSpec(ValueList.Of("*/90 * * * *"), HolidayCalendar.None, new DateOnly(2026, 11, 20)), new MapContext("YOU"));
        string expected = c.Diagnostics.First(d => d.Severity == CronSeverity.Error).Message;
        JsonNode first = Arr(p["diagnostics"])[0]!;
        T.Eq("the compiler's message, verbatim", expected, Str(first["message"]));
        T.Eq("severity", "error", Str(first["severity"]));
        T.Eq("line", 1, first["line"]!.GetValue<int>());
        T.True("field is named", !string.IsNullOrEmpty(Str(first["field"])));
        T.Eq("agents may not save it", false, p["agentsMaySave"]!.GetValue<bool>());
        T.True("minimumGapMinutes null", p["minimumGapMinutes"] is null);

        JsonObject reboot = rig.Ok(ToolCatalog.PreviewSchedule, "{\"cron\":[\"@reboot\"]}");
        T.Eq("@reboot: invalid", false, reboot["valid"]!.GetValue<bool>());
        T.Eq("@reboot: closest alternative is @logon", "@logon", string.Join(",", Arr(reboot["closestAlternative"]).Select(n => Str(n))));
        T.True("@reboot: with its explanation", !string.IsNullOrEmpty(Str(reboot["closestAlternativeExplanation"])));

        JsonObject two = rig.Ok(ToolCatalog.PreviewSchedule, "{\"cron\":[\"0 9 * * *\",\"nonsense\"]}");
        T.Eq("a bad second line: invalid", false, two["valid"]!.GetValue<bool>());
        T.Eq("and the diagnostic names line 2", 2, Arr(two["diagnostics"]).Max(d => d!["line"]!.GetValue<int>()));
    }

    public static void PreviewDensityAndAgentsMaySave()
    {
        using ReadRig rig = LateRig();
        JsonObject every = rig.Ok(ToolCatalog.PreviewSchedule, "{\"cron\":[\"* * * * *\"]}");
        T.Eq("every minute: valid", true, every["valid"]!.GetValue<bool>());
        T.Eq("minimumGapMinutes 1", 1, every["minimumGapMinutes"]!.GetValue<int>());
        T.Eq("agentsMaySave false", false, every["agentsMaySave"]!.GetValue<bool>());
        string reason = Str(every["agentsMaySaveReason"])!;
        T.True("the reason says what to do (" + reason + ")", reason.Contains("every 1 minute", StringComparison.Ordinal) && reason.Contains("at most every 5 minutes", StringComparison.Ordinal) && reason.Contains("--min-interval", StringComparison.Ordinal));

        JsonObject five = rig.Ok(ToolCatalog.PreviewSchedule, "{\"cron\":[\"*/5 * * * *\"]}");
        T.Eq("every 5 minutes: allowed", true, five["agentsMaySave"]!.GetValue<bool>());
        T.Eq("every 4 minutes: refused", false, rig.Ok(ToolCatalog.PreviewSchedule, "{\"cron\":[\"*/4 * * * *\"]}")["agentsMaySave"]!.GetValue<bool>());

        JsonObject logon = rig.Ok(ToolCatalog.PreviewSchedule, "{\"cron\":[\"@logon\"]}");
        T.Eq("@logon: valid", true, logon["valid"]!.GetValue<bool>());
        T.Eq("@logon: sentence", "When you sign in", Str(logon["description"]));
        T.Eq("@logon: no runs to list", 0, Arr(logon["nextRuns"]).Count);
        T.True("@logon: says why", !string.IsNullOrEmpty(Str(logon["note"])));
        T.True("@logon: no gap", logon["minimumGapMinutes"] is null);
        T.Eq("@logon: agents may save", true, logon["agentsMaySave"]!.GetValue<bool>());

        JsonObject two = rig.Ok(ToolCatalog.PreviewSchedule, "{\"cron\":[\"0 9 * * *\",\"0 17 * * *\"]}");
        T.Eq("two lines: gap 8 hours", 480, two["minimumGapMinutes"]!.GetValue<int>());
        T.Eq("two lines: both echoed", 2, Arr(two["cron"]).Count);
    }

    public static void PreviewTemplatesAndCount()
    {
        using ReadRig rig = LateRig();
        JsonObject t = rig.Ok(ToolCatalog.PreviewSchedule, "{\"templateId\":\"weekdays-6pm\"}");
        T.Eq("template cron", "0 18 * * 1-5", Str(Arr(t["cron"])[0]));
        T.Eq("skipHolidays defaults to none even for a template (the schema default)", "none", Str(t["skipHolidays"]));
        T.Eq("sentence", "Every weekday at 18:00", Str(t["description"]));
        T.Eq("with nyse chosen", "Every weekday at 18:00, except NYSE market holidays", Str(rig.Ok(ToolCatalog.PreviewSchedule, "{\"templateId\":\"weekdays-6pm\",\"skipHolidays\":\"nyse\"}")["description"]));
        T.Eq("blank is 0 9 * * *", "0 9 * * *", Str(Arr(rig.Ok(ToolCatalog.PreviewSchedule, "{\"templateId\":\"blank\"}")["cron"])[0]));
        T.Eq("at-login template", "When you sign in", Str(rig.Ok(ToolCatalog.PreviewSchedule, "{\"templateId\":\"at-login\"}")["description"]));
        T.Eq("count 3", 3, Arr(rig.Ok(ToolCatalog.PreviewSchedule, "{\"templateId\":\"hourly\",\"count\":3}")["nextRuns"]).Count);
        T.Eq("count 20", 20, Arr(rig.Ok(ToolCatalog.PreviewSchedule, "{\"templateId\":\"hourly\",\"count\":20}")["nextRuns"]).Count);
        T.Eq("US federal calendar", "Every weekday at 18:00, except US federal holidays", Str(rig.Ok(ToolCatalog.PreviewSchedule, "{\"templateId\":\"weekdays-6pm\",\"skipHolidays\":\"usFederal\"}")["description"]));
        foreach (Template tpl in Templates.All)
        {
            JsonObject p = rig.Ok(ToolCatalog.PreviewSchedule, "{\"templateId\":\"" + tpl.Id + "\"}");
            T.Eq("template " + tpl.Id + " is valid", true, p["valid"]!.GetValue<bool>());
        }
    }

    public static void PreviewAndTemplatesNeverTouchTheGateway()
    {
        using ReadRig rig = LateRig();
        rig.Seed(ReadRig.Stock());
        rig.Call(ToolCatalog.PreviewSchedule, "{\"cron\":[\"0 18 * * 1-5\"],\"skipHolidays\":\"nyse\"}");
        rig.Call(ToolCatalog.PreviewSchedule, "{\"cron\":[\"@reboot\"]}");
        rig.Call(ToolCatalog.PreviewSchedule, "{\"templateId\":\"hourly\"}");
        rig.Call(ToolCatalog.PreviewSchedule, "{}");
        rig.Call(ToolCatalog.ListTemplates);
        T.Eq("no Task Scheduler call of any kind (not even a listing)", 0, rig.Gw.Calls.Count);
        T.Eq("no write", 0, rig.Fake.Writes.Count);
        T.Eq("the runs root has no new folder", 0, Directory.GetFileSystemEntries(rig.RunsRoot).Length);
    }

    // ---- templates ----

    public static void ListTemplatesMatchesTemplatesAll()
    {
        using var rig = new ReadRig();
        JsonObject o = rig.Ok(ToolCatalog.ListTemplates);
        JsonArray rows = Arr(o["templates"]);
        T.Eq("nine", 9, rows.Count);
        for (int i = 0; i < 9; i++)
        {
            Template t = Templates.All[i];
            T.Eq("id " + i, t.Id, Str(rows[i]!["id"]));
            T.Eq("title " + i, t.Title, Str(rows[i]!["title"]));
            T.Eq("cron " + t.Id, t.Cron, Str(Arr(rows[i]!["cron"])[0]));
            T.Eq("suggestedName " + t.Id, t.SuggestedName, Str(rows[i]!["suggestedName"]));
            T.True("description " + t.Id, !string.IsNullOrEmpty(Str(rows[i]!["description"])));
        }
        JsonNode w = rows.First(r => Str(r!["id"]) == "weekdays-6pm")!;
        T.Eq("weekdays-6pm sentence", "Every weekday at 18:00", Str(w["description"]));
        T.Eq("weekdays-6pm suggests nyse", "nyse", Str(w["suggestedSkipHolidays"]));
        T.Eq("blank suggests none", "none", Str(rows.First(r => Str(r!["id"]) == "blank")!["suggestedSkipHolidays"]));
        T.Eq("blank cron", "0 9 * * *", Str(Arr(rows.First(r => Str(r!["id"]) == "blank")!["cron"])[0]));
        T.Eq("at-login sentence", "When you sign in", Str(rows.First(r => Str(r!["id"]) == "at-login")!["description"]));
    }

    // ---- through the protocol ----

    public static void EveryToolWorksThroughTheServerAndMirrorsItsStructuredContent()
    {
        using var rig = new ReadRig();
        rig.Seed(ReadRig.Stock());
        var s = new Session(host: rig.Host);
        s.Input.Line(Session.Init);
        s.Input.Line("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
        s.Input.Line("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}");
        string[] calls =
        [
            "{\"name\":\"cadence_get_status\"}", "{\"name\":\"cadence_list_jobs\",\"arguments\":{}}",
            "{\"name\":\"cadence_get_job\",\"arguments\":{\"jobId\":\"d4dst0ck\"}}", "{\"name\":\"cadence_preview_schedule\",\"arguments\":{\"templateId\":\"hourly\"}}",
            "{\"name\":\"cadence_list_templates\"}", "{\"name\":\"cadence_get_job\",\"arguments\":{\"jobId\":\"nonexist\"}}",
            "{\"name\":\"cadence_get_job\",\"arguments\":{\"jobid\":\"d4dst0ck\"}}",
        ];
        for (int i = 0; i < calls.Length; i++) s.Input.Line("{\"jsonrpc\":\"2.0\",\"id\":" + (10 + i) + ",\"method\":\"tools/call\",\"params\":" + calls[i] + "}");
        T.Eq("exit code", 0, s.Finish());
        List<JsonElement> replies = s.Replies();

        JsonElement tools = Session.ById(replies, 2).GetProperty("result").GetProperty("tools");
        T.Eq("tools/list: six", 6, tools.GetArrayLength());
        T.Eq("tools/list: order", string.Join(",", ToolCatalog.ReadTools.Select(t => t.Name)), string.Join(",", tools.EnumerateArray().Select(t => t.GetProperty("name").GetString())));
        foreach (JsonElement t in tools.EnumerateArray())
        {
            T.True(t.GetProperty("name").GetString() + ": annotations", t.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean() && !t.GetProperty("annotations").GetProperty("openWorldHint").GetBoolean());
            T.Eq(t.GetProperty("name").GetString() + ": additionalProperties false", false, t.GetProperty("inputSchema").GetProperty("additionalProperties").GetBoolean());
        }

        for (int i = 0; i < 5; i++)
        {
            JsonElement res = Session.ById(replies, 10 + i).GetProperty("result");
            T.Eq("call " + i + ": isError false", false, res.GetProperty("isError").GetBoolean());
            T.Eq("call " + i + ": text mirrors structuredContent", res.GetProperty("structuredContent").GetRawText(),
                JsonDocument.Parse(res.GetProperty("content")[0].GetProperty("text").GetString()!).RootElement.GetRawText());
        }
        JsonElement nf = Session.ById(replies, 15).GetProperty("result");
        T.True("not_found over the wire", nf.GetProperty("isError").GetBoolean() && nf.GetProperty("structuredContent").GetProperty("error").GetProperty("code").GetString() == "not_found");
        JsonElement bad = Session.ById(replies, 16).GetProperty("result");
        T.True("a wrong property name over the wire", bad.GetProperty("isError").GetBoolean() && bad.GetProperty("content")[0].GetProperty("text").GetString()!.Contains("jobid", StringComparison.Ordinal));
    }

    // ---- source rules ----

    static string? RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "Cadence.Agent"))) dir = Path.GetDirectoryName(dir);
        return dir;
    }

    static IEnumerable<(string File, string Code)> AgentSources(string dir)
    {
        foreach (string f in Directory.EnumerateFiles(Path.Combine(dir, "src", "Cadence.Agent"), "*.cs", SearchOption.AllDirectories))
        {
            if (f.Contains("\\obj\\", StringComparison.Ordinal) || f.Contains("\\bin\\", StringComparison.Ordinal)) continue;
            yield return (Path.GetFileName(f), string.Join("\n", File.ReadAllLines(f).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal))));
        }
    }

    public static void TheAgentCodeNeverListsOtherTasksOrWrites()
    {
        string? dir = RepoRoot();
        if (dir is null) { T.True("repository root not found (a missing root must fail, [CP-R8])", false); return; }
        var offenders = new List<string>();
        foreach ((string file, string code) in AgentSources(dir))
        {
            foreach (string bad in new[] { "ListOtherTasks", "LoadOthers", "includeForeign: true", "Load(true)", ".Register(", ".Delete(", ".Stop(", "DeleteFolderIfEmpty" })
                if (code.Contains(bad, StringComparison.Ordinal)) offenders.Add(file + ": " + bad);
            // [CP-R8] The Applier belongs to the write pipeline alone; the one direct gateway call is run-now (Phase 22, RunJobNowTool.cs).
            if (file != "WritePipeline.cs" && code.Contains("Applier", StringComparison.Ordinal)) offenders.Add(file + ": Applier outside WritePipeline.cs");
            if (file != "RunJobNowTool.cs" && code.Contains("Gateway.Run(", StringComparison.Ordinal)) offenders.Add(file + ": Gateway.Run outside RunJobNowTool.cs");
            // every use of Load must be the managed-folder-only form
            int at = 0;
            while ((at = code.IndexOf(".Load(", at, StringComparison.Ordinal)) >= 0)
            {
                string tail = code[at..Math.Min(code.Length, at + 40)];
                if (!tail.StartsWith(".Load(includeForeign: false)", StringComparison.Ordinal) && !tail.StartsWith(".Load(c,", StringComparison.Ordinal)) offenders.Add(file + ": " + tail.Split('\n')[0]);
                at += 6;
            }
        }
        T.True("no other-task listing and no write in Cadence.Agent (" + string.Join("; ", offenders) + ")", offenders.Count == 0);
    }

    public static void TheAgentCodeNeverTouchesTheRegistryOrCreatesFolders()
    {
        string? dir = RepoRoot();
        if (dir is null) { T.True("repository root not found (a missing root must fail, [CP-R8])", false); return; }
        var offenders = new List<string>();
        // Only the read-tool files: later phases add the registry policy store and the audit log to Cadence.Agent on purpose.
        string[] readFiles = ["ReadTools.cs", "JobViews.cs", "ArgReader.cs", "ToolCatalog.cs", "CadenceToolHost.cs", "AgentFileProbe.cs"];
        foreach ((string file, string code) in AgentSources(dir).Where(s => readFiles.Contains(s.File)))
            foreach (string bad in new[] { "Microsoft.Win32", "Registry.", "CreateDirectory", "File.Write", "File.Create", "File.AppendAll", "File.Move", "File.Copy", "Directory.Delete", "File.Delete" })
                if (code.Contains(bad, StringComparison.Ordinal)) offenders.Add(file + ": " + bad);
        T.True("the read-only agent code does not write files or touch the registry (" + string.Join("; ", offenders) + ")", offenders.Count == 0);
    }
}
