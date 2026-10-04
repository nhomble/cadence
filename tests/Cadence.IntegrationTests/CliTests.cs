using System.Globalization;
using System.Text;
using System.Text.Json;
using Cadence.App.Cli;
using Cadence.Core.History;
using Cadence.Core.Planning;
using Cadence.Core.TaskXml;
using Cadence.History;
using Cadence.Scheduler;
using Cadence.Scheduler.Com;
using Cadence.TestHarness;

namespace Cadence.IntegrationTests;

/// <summary>
/// The headless commands of Cadence.exe (PLAN Phase 11) against the real Task Scheduler, always in \CadenceTest with the runs root
/// %LOCALAPPDATA%\CadenceTest\runs. The App's Cli sources are compiled into this exe, so these are the very commands the switches run;
/// the real exe is exercised by hand (and its --selftest by build.cmd). Jobs here cannot fire by themselves (anchor 2099) and only
/// run when a test starts them.
/// </summary>
public static class CliTests
{
    const string Id = "cli00001";
    const string Cmd = "cmd.exe";

    sealed class Cli(E2E e)
    {
        public (int rc, string text) Run(params string[] args)
        {
            CliParseResult p = CliParser.Parse(args);
            if (p.Command is not { } cmd) throw new InvalidOperationException("bad test command line: " + p.Error);
            var sb = new StringBuilder();
            var env = new CliEnv(e.Gw, e.Store, () => Fx.Now, TimeZoneInfo.Local, t => sb.Append(t));
            int rc;
            if (cmd.Action == CliAction.Probe)
            {
                env.Raw(Probe.Build(env, cmd.All, "integration") + "\r\n");
                rc = 0;
            }
            else rc = JobCommands.Execute(cmd, env);
            return (rc, sb.ToString());
        }

        public string WriteJob(string name, string json)
        {
            string path = Path.Combine(e.Work, name);
            File.WriteAllText(path, json, new UTF8Encoding(false));
            return path;
        }
    }

    /// <summary>A job file whose trigger is dated 2099 (never fires) and is enabled (so Run works).</summary>
    static string JobJson(string id, string name, string cron = "0 18 * * *", string args = "/d /c echo cli says hi") =>
        "{\"id\":\"" + id + "\",\"name\":\"" + name + "\",\"enabled\":true,"
        + "\"schedule\":{\"cron\":[\"" + cron + "\"],\"anchor\":\"2099-01-01\"},"
        + "\"run\":{\"exe\":\"" + Cmd + "\",\"args\":\"" + args + "\",\"hidden\":true}}";

    static JsonDocument Json(string text) => JsonDocument.Parse(text);

    static JsonElement JobOf(JsonDocument d, string id) =>
        d.RootElement.GetProperty("jobs").EnumerateArray().First(j => j.GetProperty("id").GetString() == id);

    public static void ApplyProbeRunHistoryUntrackAgainstTheRealScheduler() => E2E.Scenario(e =>
    {
        var cli = new Cli(e);
        string file = cli.WriteJob("job.json", JobJson(Id, "cli round trip job"));

        var (rc, text) = cli.Run("--apply", file, "--yes", "--folder", "CadenceTest");
        T.Eq("apply: exit", 0, rc);
        T.True("apply: says created (" + text.Trim() + ")", text.StartsWith("OK: Created \\CadenceTest\\cli round trip job", StringComparison.Ordinal));

        (rc, text) = cli.Run("--probe", "--folder", "CadenceTest");
        T.Eq("probe: exit", 0, rc);
        using (JsonDocument doc = Json(text))
        {
            JsonElement j = JobOf(doc, Id);
            T.Eq("probe: folder", "CadenceTest", doc.RootElement.GetProperty("folder").GetString());
            T.Eq("probe: managed", "managed", j.GetProperty("ownership").GetString());
            T.Eq("probe: InSync", "inSync", j.GetProperty("sync").GetString());
            T.Eq("probe: task path", "\\CadenceTest\\cli round trip job", j.GetProperty("taskPath").GetString());
            T.Eq("probe: enabled", true, j.GetProperty("enabled").GetBoolean());
            T.Eq("probe: no history yet", "none", j.GetProperty("badge").GetString());
            T.True("probe: raw Task Scheduler state is reported", j.GetProperty("taskScheduler").GetProperty("state").GetString() is "Ready" or "Queued");
            T.True("probe: no foreign key without --all", !doc.RootElement.TryGetProperty("foreign", out _));
        }

        ClassifiedTask c = e.Jobs.Load(false).Single(x => x.Job?.Id == Id);
        IReadOnlyList<RunRecord>? recs = e.RunAndWaitUnlessBlocked(c, 1, TimeSpan.FromSeconds(90), "CLI --run",
            () => { var r = cli.Run("--run", Id, "--folder", "CadenceTest"); T.Eq("--run: exit", 0, r.rc); T.True("--run: says started", r.text.StartsWith("Started \\CadenceTest\\cli round trip job", StringComparison.Ordinal), r.text); });

        if (recs is null)
        {
            // Windows refused to start the wrapper (Finding A): the run itself cannot be verified, but the product behaviour can.
            T.KnownIssue("CLI --run: the run, its record and the success rate were NOT verified",
                "Windows refused to start the wrapper (LastTaskResult 0x80070005, no wrapper process, no run record): Windows Defender, see the README Safety notes (Checkpoint B). Not worked around on purpose.");
            e.WaitIdle(c.Observed.Name, TimeSpan.FromSeconds(10));
            (rc, text) = cli.Run("--history", Id, "--folder", "CadenceTest", "--json");
            using JsonDocument h = Json(text);
            JsonElement row = h.RootElement.GetProperty("recent")[0];
            T.Eq("blocked: --history shows a NotReported row", "notReported", row.GetProperty("status").GetString());
            T.Eq("blocked: --history carries the plain message", TaskResultCodes.AccessDeniedLaunch, row.GetProperty("message").GetString());
            (rc, text) = cli.Run("--probe", "--folder", "CadenceTest");
            using JsonDocument p = Json(text);
            T.Eq("blocked: the probe badge comes from the records", "notReported", JobOf(p, Id).GetProperty("badge").GetString());
        }
        else
        {
            e.WaitIdle(c.Observed.Name, TimeSpan.FromSeconds(30));
            (rc, text) = cli.Run("--history", Id, "--folder", "CadenceTest", "--json");
            T.Eq("history: exit", 0, rc);
            using (JsonDocument h = Json(text))
            {
                JsonElement s = h.RootElement.GetProperty("summary");
                T.Eq("history: successRate = 1", 1.0, s.GetProperty("successRate").GetDouble());
                T.Eq("history: lastResult ok", "ok", s.GetProperty("lastResult").GetString());
                T.True("history: at least one run", h.RootElement.GetProperty("recent").GetArrayLength() >= 1);
                JsonElement r0 = h.RootElement.GetProperty("recent")[0];
                T.Eq("history: newest row ok", "ok", r0.GetProperty("status").GetString());
                T.Eq("history: exit code 0", 0, r0.GetProperty("exitCode").GetInt32());
                T.True("history: the program printed something", r0.GetProperty("hasOutput").GetBoolean());
            }
            (rc, text) = cli.Run("--history", Id, "--folder", "CadenceTest");
            T.True("history text shows the ok row", text.Contains(" ok ", StringComparison.Ordinal) && text.Contains("cli round trip job  [" + Id + "]", StringComparison.Ordinal), text);

            (rc, text) = cli.Run("--probe", "--folder", "CadenceTest");
            using JsonDocument p = Json(text);
            T.Eq("probe after the run: lastResult ok", "ok", JobOf(p, Id).GetProperty("lastResult").GetString());
            T.Eq("probe after the run: successRate 1", 1.0, JobOf(p, Id).GetProperty("successRate").GetDouble());
            T.Eq("probe after the run: the raw code is only a field (hidden jobs report 0)", "0x00000000", JobOf(p, Id).GetProperty("taskScheduler").GetProperty("lastTaskResult").GetString());
        }

        // Untrack: the task stays, becomes foreign, and is listed under foreign only with --all.
        (rc, text) = cli.Run("--untrack", Id, "--folder", "CadenceTest", "--yes");
        T.Eq("untrack: exit", 0, rc);
        T.True("untrack: the task is still in Task Scheduler", e.Gw.Get("cli round trip job") is not null);
        (rc, text) = cli.Run("--probe", "--folder", "CadenceTest");
        using (JsonDocument plain = Json(text))
            T.Eq("probe: no managed job any more", 0, plain.RootElement.GetProperty("jobs").GetArrayLength());
        (rc, text) = cli.Run("--probe", "--folder", "CadenceTest", "--all");
        using (JsonDocument all = Json(text))
        {
            string[] paths = all.RootElement.GetProperty("foreign").EnumerateArray().Select(f => f.GetProperty("taskPath").GetString()!).ToArray();
            T.True("probe --all: the untracked task is listed as foreign", paths.Contains("\\CadenceTest\\cli round trip job", StringComparer.OrdinalIgnoreCase));
            T.True("probe --all: so are the machine's other tasks (read-only)", paths.Length > 1);
            T.True("probe --all: none of them is a Cadence job", all.RootElement.GetProperty("foreign").EnumerateArray().All(f => f.GetProperty("ownership").GetString() == "foreign"));
        }

        // The untracked task can no longer be addressed by id: delete/run/reapply find nothing, and the task is left alone.
        string before = e.Gw.Get("cli round trip job")!.Xml;
        (rc, text) = cli.Run("--delete", Id, "--folder", "CadenceTest", "--yes");
        T.Eq("delete of an untracked job: exit", 1, rc);
        T.True("delete of an untracked job: says there is no such job", text.Contains("No job with id " + Id, StringComparison.Ordinal), text);
        T.Eq("the untracked task is byte for byte unchanged", before, e.Gw.Get("cli round trip job")!.Xml);
    }, spawnsProcesses: true);

    public static void UpdateRenameDriftReapplyAndForeignSafety() => E2E.Scenario(e =>
    {
        var cli = new Cli(e);
        string foreignXml = Fx.Xml(Fx.FarTrigger);
        e.Gw.Register("cli same name", foreignXml);
        string foreignBefore = e.Gw.Get("cli same name")!.Xml;

        // A new job whose name is taken by a foreign task gets a free name; the foreign task is never overwritten.
        var (rc, text) = cli.Run("--apply", cli.WriteJob("a.json", JobJson(Id, "cli same name")), "--yes", "--folder", "CadenceTest");
        T.Eq("apply over a taken name: exit", 0, rc);
        T.True("a free name was used (" + text.Trim() + ")", text.Contains("\\CadenceTest\\cli same name (2)", StringComparison.Ordinal));
        T.Eq("the foreign task is byte for byte unchanged", foreignBefore, e.Gw.Get("cli same name")!.Xml);

        // Same file again: no change. Changed cron: updated in place.
        (rc, text) = cli.Run("--apply", cli.WriteJob("a.json", JobJson(Id, "cli same name")), "--yes", "--folder", "CadenceTest");
        T.True("again: no changes (" + text.Trim() + ")", rc == 0 && text.Contains("No changes", StringComparison.Ordinal));
        (rc, text) = cli.Run("--apply", cli.WriteJob("a2.json", JobJson(Id, "cli same name", cron: "30 18 * * *")), "--yes", "--folder", "CadenceTest");
        T.True("changed cron: saved (" + text.Trim() + ")", rc == 0 && text.Contains("Saved \\CadenceTest\\cli same name (2)", StringComparison.Ordinal));
        (rc, text) = cli.Run("--probe", "--folder", "CadenceTest");
        using (JsonDocument doc = Json(text))
        {
            T.Eq("probe: new cron", "30 18 * * *", JobOf(doc, Id).GetProperty("cron")[0].GetString());
            T.Eq("probe: InSync", "inSync", JobOf(doc, Id).GetProperty("sync").GetString());
            T.Eq("probe: next runs are empty or in 2099 (the anchor decides, nothing fires today)", true,
                JobOf(doc, Id).GetProperty("next").EnumerateArray().All(n => n.GetString()!.StartsWith("2099-", StringComparison.Ordinal)));
        }

        // Rename: the new task is created first, then the old one is removed.
        (rc, text) = cli.Run("--apply", cli.WriteJob("a3.json", JobJson(Id, "cli renamed job", cron: "30 18 * * *")), "--yes", "--folder", "CadenceTest");
        T.True("rename: ok (" + text.Trim() + ")", rc == 0 && text.Contains("Renamed", StringComparison.Ordinal));
        T.True("rename: the old task is gone, the new one is there", e.Gw.Get("cli same name (2)") is null && e.Gw.Get("cli renamed job") is not null);
        T.Eq("rename: the foreign task is still there, unchanged", foreignBefore, e.Gw.Get("cli same name")!.Xml);

        // Drift: an edit made outside Cadence is reported and discarded by --reapply.
        ApplyFx.MoveFirstTrigger("cli renamed job", 5);
        (rc, text) = cli.Run("--probe", "--folder", "CadenceTest");
        using (JsonDocument doc = Json(text))
        {
            JsonElement j = JobOf(doc, Id);
            T.Eq("drift: sync", "drifted", j.GetProperty("sync").GetString());
            T.True("drift: lists what differs", j.GetProperty("drift").GetArrayLength() >= 1);
        }
        (rc, text) = cli.Run("--reapply", Id, "--folder", "CadenceTest", "--yes");
        T.True("reapply: ok (" + text.Trim() + ")", rc == 0 && text.StartsWith("OK: Re-applied", StringComparison.Ordinal));
        (rc, text) = cli.Run("--probe", "--folder", "CadenceTest");
        using (JsonDocument doc = Json(text))
            T.Eq("reapply: InSync again", "inSync", JobOf(doc, Id).GetProperty("sync").GetString());

        // Nothing but our own task changed: the other task of this folder is untouched at the end of all that.
        T.Eq("the foreign task survived every step byte for byte", foreignBefore, e.Gw.Get("cli same name")!.Xml);
    });

    public static void DeleteRemovesTheTaskAndTheRunHistory() => E2E.Scenario(e =>
    {
        var cli = new Cli(e);
        var (rc, _) = cli.Run("--apply", cli.WriteJob("d.json", JobJson(Id, "cli delete job")), "--yes", "--folder", "CadenceTest");
        T.Eq("apply: exit", 0, rc);

        // A run history for the job, and one for another job that must survive.
        string mine = Path.Combine(e.Store.Root, Id);
        string other = Path.Combine(e.Store.Root, "other001");
        Directory.CreateDirectory(mine);
        File.WriteAllText(Path.Combine(mine, "runs.jsonl"), "{}\n");
        File.WriteAllText(Path.Combine(mine, "20261001-000000-000.out.txt"), "output");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "runs.jsonl"), "{}\n");

        string text;
        (rc, text) = cli.Run("--delete", Id, "--folder", "CadenceTest", "--yes");
        T.Eq("delete: exit", 0, rc);
        T.True("delete: says deleted and history removed (" + text.Trim() + ")", text.Contains("OK: Deleted \\CadenceTest\\cli delete job", StringComparison.Ordinal) && text.Contains("Run history of " + Id + " removed.", StringComparison.Ordinal));
        T.True("the task is gone from Task Scheduler", e.Gw.Get("cli delete job") is null);
        T.True("the run folder is gone", !Directory.Exists(mine));
        T.True("another job's run folder is untouched", File.Exists(Path.Combine(other, "runs.jsonl")));

        (rc, text) = cli.Run("--delete", Id, "--folder", "CadenceTest", "--yes");
        T.Eq("delete again: exit", 1, rc);
    });

    public static void ReadOnlySwitchesDoNotCreateAnything()
    {
        // 1. --probe against a managed folder that does not exist (\CadenceTest after cleanup): an empty list, and the folder is NOT created.
        TestFolder.Cleanup();
        T.Eq("setup: \\CadenceTest does not exist", false, TestFolder.Exists());
        TaskSchedulerGateway test = TestFolder.NewGateway();
        var sb = new StringBuilder();
        var env = new CliEnv(test, new RunRecordStore(Path.Combine(ApplyFx.LocalDataDir, "runs")), () => Fx.Now, TimeZoneInfo.Local, t => sb.Append(t));
        using (JsonDocument doc = Json(Probe.Build(env, all: true, "integration")))
        {
            T.Eq("probe of a missing folder: no jobs", 0, doc.RootElement.GetProperty("jobs").GetArrayLength());
            T.True("probe --all of a missing folder still lists the machine's other tasks", doc.RootElement.GetProperty("foreign").GetArrayLength() > 0);
        }
        T.Eq("the probe did not create \\CadenceTest", false, TestFolder.Exists());
        T.True("the probe did not create the runs root", !Directory.Exists(ApplyFx.LocalDataDir));

        // 2. The REAL folder (\Cadence): --selftest and --probe only read. Nothing may appear, whether or not the product folder exists yet.
        bool folderBefore = RealFolderExists();
        string localCadence = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cadence");
        bool dataBefore = Directory.Exists(localCadence);
        var real = new TaskSchedulerGateway();   // the product folder, default options
        T.Eq("the default gateway points at \\Cadence", "\\Cadence", real.FolderPath);

        var self = new StringBuilder();
        int rc = SelfTest.Run(real, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows", t => self.Append(t));
        T.True("selftest against the real folder passes (" + self.ToString().Replace("\r\n", " | ", StringComparison.Ordinal) + ")", rc == 0);
        T.True("selftest prints at least 9 PASS lines", self.ToString().Split("\r\n").Count(l => l.StartsWith("PASS ", StringComparison.Ordinal)) >= 9);

        var realEnv = new CliEnv(real, new RunRecordStore(RunRecordStore.DefaultRoot()), () => Fx.Now, TimeZoneInfo.Local, _ => { });
        using (JsonDocument doc = Json(Probe.Build(realEnv, all: false, "integration")))
            T.Eq("probe of the real folder is valid JSON for folder Cadence", "Cadence", doc.RootElement.GetProperty("folder").GetString());
        using (JsonDocument doc = Json(Probe.Build(realEnv, all: true, "integration")))
            T.True("probe --all of the real folder carries a foreign list", doc.RootElement.TryGetProperty("foreign", out _));

        T.Eq("\\Cadence exists exactly as before (a read never creates it)", folderBefore, RealFolderExists());
        T.Eq("%LOCALAPPDATA%\\Cadence exists exactly as before", dataBefore, Directory.Exists(localCadence));
    }

    static bool RealFolderExists()
    {
        using TaskServiceSession s = TaskServiceSession.Open();
        object? folder = s.TryGetFolder("\\Cadence");
        ComRelease.Release(folder);
        return folder is not null;
    }
}
