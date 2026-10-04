using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Cadence.Core.Execution;
using Cadence.Core.History;
using Cadence.Core.Metadata;
using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.Core.TaskXml;
using Cadence.Scheduler;
using Cadence.Scheduler.Apply;
using Cadence.TestHarness;

namespace Cadence.IntegrationTests;

/// <summary>
/// Phase 19 against the real Task Scheduler, always in \CadenceTest: who wrote a job (app, command line) read back from the stored metadata,
/// the real <c>Cadence.exe</c> (spawned, output captured with System.Diagnostics.Process, every PID tracked and killed), a v0.1.0-style job
/// without the new fields, a real wrapper run of a job that carries them, the revision and the cross-process writer lock. Jobs are dated 2099
/// and run only when a test starts one on purpose (cmd /c echo). A run that Windows refuses to start (Defender, PLAN 10.2 Q6) is a KNOWN-ISSUE
/// through the existing detection, never worked around.
/// </summary>
public static class AttributionIntegrationTests
{
    static readonly List<int> Pids = [];

    /// <summary>The result of one run of the real Cadence.exe.</summary>
    sealed record Run(int Exit, string Stdout, string Stderr);

    static ProcessStartInfo Psi(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(Mcp.McpTestClient.ExePath())
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_DISABLE_GUI_ERRORS"] = "1";
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_ROOT")))
        {
            string perUser = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "dotnet");
            if (Directory.Exists(perUser)) psi.Environment["DOTNET_ROOT"] = perUser;
        }
        return psi;
    }

    /// <summary>A started CLI process whose output is being read (a GUI-subsystem exe: only a Process with redirected pipes can capture it).</summary>
    sealed class Started
    {
        public required Process Process { get; init; }
        public required Task<string> Out { get; init; }
        public required Task<string> Err { get; init; }

        public Run Finish(TimeSpan timeout)
        {
            if (!Process.WaitForExit(timeout))
            {
                Kill();
                throw new TimeoutException("Cadence.exe did not finish within " + (int)timeout.TotalSeconds + " s");
            }
            return new Run(Process.ExitCode, Out.GetAwaiter().GetResult(), Err.GetAwaiter().GetResult());
        }

        public void Kill()
        {
            try { if (!Process.HasExited) Process.Kill(entireProcessTree: true); Process.WaitForExit(10000); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }

    static Started Start(params string[] args)
    {
        Process p = Process.Start(Psi(args)) ?? throw new InvalidOperationException("could not start Cadence.exe");
        lock (Pids) Pids.Add(p.Id);
        p.StandardInput.Close();
        return new Started { Process = p, Out = p.StandardOutput.ReadToEndAsync(), Err = p.StandardError.ReadToEndAsync() };
    }

    static Run RunExe(params string[] args)
    {
        Started s = Start(args);
        try { return s.Finish(TimeSpan.FromSeconds(120)); }
        finally { s.Kill(); }
    }

    static string Write(E2E e, string name, string json)
    {
        string path = Path.Combine(e.Work, name);
        File.WriteAllText(path, json, new UTF8Encoding(false));
        return path;
    }

    static string JobFile(string id, string name, string cron = "0 18 * * *", string args = "/c exit 0", string extra = "") =>
        "{\"id\":\"" + id + "\",\"name\":\"" + name + "\",\"enabled\":false," + extra
        + "\"schedule\":{\"cron\":[\"" + cron + "\"],\"anchor\":\"2099-01-01\"},"
        + "\"run\":{\"exe\":\"cmd.exe\",\"args\":\"" + args + "\",\"hidden\":true}}";

    static CadenceMetadata MetaOf(E2E e, string taskName)
    {
        RegisteredTaskInfo info = e.Gw.Get(taskName) ?? throw new InvalidOperationException("no task " + taskName);
        TaskDefinitionModel model = TaskXmlReader.Read(info.Xml);
        T.Eq("'" + taskName + "' metadata decodes", MetadataProblem.None, MetadataCodec.TryDecode(model.RegistrationInfo.Documentation, out CadenceMetadata? m));
        return m!;
    }

    static JsonElement JobOf(JsonDocument d, string id) =>
        d.RootElement.GetProperty("jobs").EnumerateArray().First(j => j.GetProperty("id").GetString() == id);

    static Applier ApplierFor(E2E e, Actor actor) => new(new ManagedJobs(e.Gw, () => Fx.Now, actor));

    // ---- who wrote it: app, then the real command line ----

    public static void AppAndCliWritesRecordTheRightActors() => E2E.Scenario(e =>
    {
        const string Id = "att00001";
        JobDefinition job = Fx.Job("attribution job", ["0 18 * * *"]) with { Id = Id };
        ApplyResult created = ApplierFor(e, Actor.App).Save(job, null);
        T.True("the app creates the job: " + created.Message, created.Success);
        CadenceMetadata m0 = MetaOf(e, job.Name);
        T.True("created by the app, updated by the app", m0.CreatedBy == "app" && m0.UpdatedBy == "app");
        T.Eq("InSync", SyncStatus.InSync, e.Jobs.Reclassify(job.Name)!.Sync);

        // The REAL Cadence.exe changes it from the command line.
        string file = Write(e, "att.json", JobFile(Id, job.Name, cron: "30 18 * * *"));
        Run apply = RunExe("--apply", file, "--yes", "--folder", "CadenceTest");
        T.Eq("cadence.exe --apply: exit", 0, apply.Exit);
        T.True("cadence.exe --apply: says saved (" + apply.Stdout.Trim() + " / " + apply.Stderr.Trim() + ")", apply.Stdout.Contains("OK: Saved", StringComparison.Ordinal));
        CadenceMetadata m1 = MetaOf(e, job.Name);
        T.Eq("updatedBy is now the command line", "cli", m1.UpdatedBy);
        T.Eq("createdBy is still the app", "app", m1.CreatedBy);
        T.Eq("the cron changed", "30 18 * * *", string.Join("|", m1.Job.Schedule.CronLines));
        ClassifiedTask live = e.Jobs.Reclassify(job.Name)!;
        T.Eq("still InSync after the CLI write", SyncStatus.InSync, live.Sync);

        // --probe of the real exe shows it all.
        Run probe = RunExe("--probe", "--folder", "CadenceTest");
        T.Eq("cadence.exe --probe: exit", 0, probe.Exit);
        using (JsonDocument doc = JsonDocument.Parse(probe.Stdout))
        {
            JsonElement j = JobOf(doc, Id);
            T.Eq("probe createdBy", "app", j.GetProperty("createdBy").GetString());
            T.Eq("probe updatedBy", "cli", j.GetProperty("updatedBy").GetString());
            T.True("probe created is an ISO time", DateTimeOffset.TryParse(j.GetProperty("created").GetString(), out _));
            T.True("probe updated is an ISO time", DateTimeOffset.TryParse(j.GetProperty("updated").GetString(), out _));
            T.Eq("probe revision is JobRevision.Of the live task", JobRevision.Of(live), j.GetProperty("revision").GetString());
            T.Eq("probe agentLock", false, j.GetProperty("agentLock").GetBoolean());
            T.Eq("probe sync", "inSync", j.GetProperty("sync").GetString());
        }

        // The lock through the job file (the CLI has no switch for it before Phase 20), then a file that says nothing about it keeps it.
        Run lockIt = RunExe("--apply", Write(e, "att-lock.json", JobFile(Id, job.Name, cron: "30 18 * * *", extra: "\"agentLock\":true,")), "--yes", "--folder", "CadenceTest");
        T.Eq("apply with agentLock true: exit", 0, lockIt.Exit);
        T.True("the lock is stored", MetaOf(e, job.Name).Job.AgentLock);
        T.Eq("a locked job is still InSync", SyncStatus.InSync, e.Jobs.Reclassify(job.Name)!.Sync);
        Run keep = RunExe("--apply", Write(e, "att-keep.json", JobFile(Id, job.Name, cron: "45 18 * * *")), "--yes", "--folder", "CadenceTest");
        T.Eq("apply without agentLock: exit", 0, keep.Exit);
        T.True("the owner's lock survived a file that says nothing about it", MetaOf(e, job.Name).Job.AgentLock);
        Run reapply = RunExe("--reapply", Id, "--yes", "--folder", "CadenceTest");
        T.Eq("--reapply: exit", 0, reapply.Exit);
        T.Eq("--reapply is attributed to the command line and keeps the lock", "cli", MetaOf(e, job.Name).UpdatedBy);
        T.Eq("a typo in the job file is still refused (exit 1)", 1, RunExe("--apply", Write(e, "att-typo.json", JobFile(Id, job.Name, extra: "\"agentLok\":true,")), "--yes", "--folder", "CadenceTest").Exit);
    });

    // ---- a job written by v0.1.0 ----

    public static void AJobWithoutTheNewFieldsStillClassifiesInSyncAndReadsAsUnknown() => E2E.Scenario(e =>
    {
        // Composed with no actor: byte for byte what v0.1.0 wrote (no createdBy, updatedBy or agentLock).
        JobDefinition job = Fx.Job("legacy job", ["0 18 * * *"]);
        string xml = TaskComposer.ComposeXml(job, Fx.Ctx(e.Gw, job.Name));
        T.True("fixture: no new field in the text", !xml.Contains("createdBy", StringComparison.Ordinal) && !xml.Contains("updatedBy", StringComparison.Ordinal) && !xml.Contains("agentLock", StringComparison.Ordinal));
        e.Gw.Register(job.Name, xml);

        ClassifiedTask live = e.Jobs.Reclassify(job.Name)!;
        T.Eq("Managed", Ownership.Managed, live.Ownership);
        T.Eq("InSync (not Outdated: the new fields are optional and change no generator or wrapper version)", SyncStatus.InSync, live.Sync);
        T.True("no actor is known", live.Metadata is { CreatedBy: null, UpdatedBy: null });

        Run probe = RunExe("--probe", "--folder", "CadenceTest");
        T.Eq("probe exit", 0, probe.Exit);
        using (JsonDocument doc = JsonDocument.Parse(probe.Stdout))
        {
            JsonElement j = JobOf(doc, job.Id);
            T.Eq("probe: createdBy unknown", "unknown", j.GetProperty("createdBy").GetString());
            T.Eq("probe: updatedBy unknown", "unknown", j.GetProperty("updatedBy").GetString());
            T.Eq("probe: sync", "inSync", j.GetProperty("sync").GetString());
            T.Eq("probe: not locked", false, j.GetProperty("agentLock").GetBoolean());
        }

        // A no-op apply of the very same job from the CLI does not rewrite the task (a v0.1.0 job is not touched by a no-op).
        string before = e.Gw.Get(job.Name)!.Xml;
        Run noop = RunExe("--apply", Write(e, "legacy.json", JobFile(job.Id, job.Name)), "--yes", "--folder", "CadenceTest");
        T.Eq("no-op apply: exit", 0, noop.Exit);
        T.True("no-op apply: says no changes (" + noop.Stdout.Trim() + ")", noop.Stdout.Contains("No changes", StringComparison.Ordinal));
        T.Eq("no-op apply: the stored XML is byte for byte the same", before, e.Gw.Get(job.Name)!.Xml);

        // A real change by the CLI then records the CLI and leaves the creator unknown.
        Run change = RunExe("--apply", Write(e, "legacy2.json", JobFile(job.Id, job.Name, args: "/d /c exit 1")), "--yes", "--folder", "CadenceTest");
        T.Eq("change: exit", 0, change.Exit);
        CadenceMetadata m = MetaOf(e, job.Name);
        T.True("the creator stays unknown, the writer is recorded", m.CreatedBy is null && m.UpdatedBy == "cli");
        T.Eq("still InSync", SyncStatus.InSync, e.Jobs.Reclassify(job.Name)!.Sync);
    });

    // ---- what Task Scheduler stores ----

    public static void MetadataWithAttributionSurvivesRegistrationByteForByte() => E2E.Scenario(e =>
    {
        JobDefinition job = Fx.Job("stored fields", ["0 18 * * *"]) with { AgentLock = true };
        var ctx = Fx.Ctx(e.Gw, job.Name) with { Actor = Actor.Agent("Claude Code") };
        TaskDefinitionModel model = TaskComposer.Compose(job, ctx);
        RegisteredTaskInfo info = e.Gw.Register(job.Name, TaskXmlWriter.Write(model));
        TaskDefinitionModel stored = TaskXmlReader.Read(info.Xml);
        T.Eq("Documentation is stored byte for byte", model.RegistrationInfo.Documentation, stored.RegistrationInfo.Documentation);
        T.Eq("fingerprint(stored) == fingerprint(composed): the actor and the lock are not in it", Fingerprint.Of(model), Fingerprint.Of(stored));
        T.Eq("and equals the fingerprint of the same job composed with no actor", Fingerprint.Of(TaskComposer.Compose(job with { AgentLock = false }, Fx.Ctx(e.Gw, job.Name))), Fingerprint.Of(stored));
        CadenceMetadata m = MetaOf(e, job.Name);
        T.True("fields stored", m.CreatedBy == "agent:Claude Code" && m.UpdatedBy == "agent:Claude Code" && m.Job.AgentLock);
        T.Eq("InSync", SyncStatus.InSync, e.Jobs.Reclassify(job.Name)!.Sync);

        // The 16 KB boundary with the largest attribution, padded from MaxEncodedLength minus the actual encoded length (never a magic number).
        JobDefinition big = Fx.Job("stored big fields", ["0 18 * * *"]) with { AgentLock = true };
        var bigCtx = Fx.Ctx(e.Gw, big.Name) with { Actor = Actor.Agent(new string('N', 200)) };
        JobDefinition Padded(int n) => big with { Run = big.Run with { Args = "/c exit 0 " + new string('x', n) } };
        int baseLen = TaskComposer.Compose(Padded(0), bigCtx).RegistrationInfo.Documentation!.Length;
        int pad = MetadataCodec.MaxEncodedLength - baseLen - 20;
        int docLen = TaskComposer.Compose(Padded(pad), bigCtx).RegistrationInfo.Documentation!.Length;
        T.True("the padded Documentation is within 20 characters of the limit (" + docLen + ")", docLen <= MetadataCodec.MaxEncodedLength && docLen >= MetadataCodec.MaxEncodedLength - 25);
        TaskDefinitionModel bigModel = TaskComposer.Compose(Padded(pad), bigCtx);
        RegisteredTaskInfo bigInfo = e.Gw.Register(big.Name, TaskXmlWriter.Write(bigModel));
        T.Eq("16 KB with attribution: stored byte for byte", bigModel.RegistrationInfo.Documentation, TaskXmlReader.Read(bigInfo.Xml).RegistrationInfo.Documentation);
        T.Eq("16 KB with attribution: InSync", SyncStatus.InSync, e.Jobs.Reclassify(big.Name)!.Sync);
        T.Throws<ComposeException>("one more character over the cap is refused by the composer", () => TaskComposer.Compose(Padded(MetadataCodec.MaxEncodedLength), bigCtx));
    });

    // ---- the wrapper ignores the new fields ----

    public static void ARealWrapperRunOfAJobCarryingTheNewFieldsRecordsOk() => E2E.Scenario(e =>
    {
        const string Marker = "PHASE19-WRAPPER-MARK";
        JobDefinition job = E2E.Job("fields wrapper run", "cmd.exe", "/d /c echo " + Marker);
        ApplyResult r = ApplierFor(e, Actor.Agent("Claude Code")).Save(job, null);
        T.True("saved by an agent actor: " + r.Message, r.Success);
        // [AG-P21, CP-R6] An agent writer can no longer set the lock (the Scheduler layer copies the live value): the owner's lock is simulated by editing the stored metadata.
        ApplyFx.RegisterWithEditedMetadata(e.Gw, "fields wrapper run", m => m with { Job = m.Job with { AgentLock = true } });
        ClassifiedTask c = e.Jobs.Load(false).Single(x => x.Job?.Id == job.Id);
        T.True("the job really carries the fields", c.Metadata is { CreatedBy: "agent:Claude Code", UpdatedBy: "agent:Claude Code" } && c.Job!.AgentLock);
        T.Eq("InSync", SyncStatus.InSync, c.Sync);

        IReadOnlyList<RunRecord>? recs = e.RunAndWaitUnlessBlocked(c, 1, TimeSpan.FromSeconds(90), "wrapper run with the new metadata fields");
        if (recs is null)
        {
            T.KnownIssue("a real wrapper run of a job carrying createdBy/updatedBy/agentLock was NOT verified",
                "Windows refused to start the wrapper (LastTaskResult 0x80070005, no wrapper process, no run record): Windows Defender, see the README Safety notes (Checkpoint B). Not worked around on purpose.");
            e.WaitIdle(c.Observed.Name, TimeSpan.FromSeconds(10));
            return;
        }
        if (recs.Count == 0) return;
        T.Eq("the wrapper (Windows PowerShell 5.1 ConvertFrom-Json) ignores the new fields: status Ok", RunStatus.Ok, recs[0].Status);
        T.Eq("exit code 0", 0, recs[0].ExitCode);
        string? output = e.Store.ReadOutput(job.Id, recs[0].RunId, false);
        T.True("and the program's output was captured", output is not null && output.Contains(Marker, StringComparison.Ordinal), output);
    }, spawnsProcesses: true);

    // ---- revision and concurrency ----

    public static void RevisionChangesOnEveryWriteAndOnAnEditMadeInTaskScheduler() => E2E.Scenario(e =>
    {
        JobDefinition job = Fx.Job("revision job", ["0 18 * * *"]);
        T.True("created", ApplierFor(e, Actor.App).Save(job, null).Success);
        ClassifiedTask t0 = e.Jobs.Reclassify(job.Name)!;
        string r0 = JobRevision.Of(t0);
        T.Eq("stable when nothing changed", r0, JobRevision.Of(e.Jobs.Reclassify(job.Name)!));

        // An MMC-style object-model edit: the Documentation stays, the fingerprint changes.
        ApplyFx.MoveFirstTrigger(job.Name, 20);
        ClassifiedTask edited = e.Jobs.Reclassify(job.Name)!;
        T.Eq("the edit made outside Cadence shows as drift", SyncStatus.Drifted, edited.Sync);
        string r1 = JobRevision.Of(edited);
        T.True("an edit in Task Scheduler changes the revision", r1 != r0, r1);
        T.Eq("the Documentation is untouched by the edit", t0.Actual!.RegistrationInfo.Documentation, edited.Actual!.RegistrationInfo.Documentation);

        // So a save that was prepared before the edit is refused, and says so.
        ApplyResult stale = ApplierFor(e, Actor.App).Save(job with { Run = job.Run with { Args = "/c exit 2" } }, job.Name, r0);
        T.True("a save with the old revision is refused", !stale.Success && stale.Kind == ApplyFailure.Stale, stale.Kind + " " + stale.Message);
        T.True("the message says it was changed in Task Scheduler: " + stale.Message, stale.Message.Contains("changed in Task Scheduler", StringComparison.Ordinal));
        T.Eq("nothing was written", r1, JobRevision.Of(e.Jobs.Reclassify(job.Name)!));

        // Re-apply (the owner's decision), then the revision is new again; a Cadence write moves it too.
        T.True("re-apply", ApplierFor(e, Actor.Cli).Reapply(job.Name).Success);
        string r2 = JobRevision.Of(e.Jobs.Reclassify(job.Name)!);
        T.True("a re-apply changes the revision", r2 != r1 && r2 != r0, r2);
        T.True("a Cadence save changes it", ApplierFor(e, Actor.App).Save(job with { Run = job.Run with { Args = "/c exit 3" } }, job.Name, r2).Success);
        T.True("and the revision moved", JobRevision.Of(e.Jobs.Reclassify(job.Name)!) != r2);
    });

    public static void TwoWritersWithTheSameRevisionExactlyOneWins() => E2E.Scenario(e =>
    {
        for (int round = 0; round < 5; round++)
        {
            JobDefinition job = Fx.Job("race job " + round, ["0 18 * * *"]);
            T.True("round " + round + ": created", ApplierFor(e, Actor.App).Save(job, null).Success);
            string rev = JobRevision.Of(e.Jobs.Reclassify(job.Name)!);

            var results = new ApplyResult[2];
            var start = new Barrier(2);
            var threads = new Thread[2];
            for (int i = 0; i < 2; i++)
            {
                int n = i;
                threads[i] = new Thread(() =>
                {
                    // Two separate gateways, ManagedJobs and Appliers: only the cross-process mutex and the revision stand between them.
                    var gw = TestFolder.NewGateway();
                    var applier = new Applier(new ManagedJobs(gw, () => Fx.Now, n == 0 ? Actor.App : Actor.Cli));
                    start.SignalAndWait();
                    results[n] = applier.Save(job with { Run = job.Run with { Args = "/c exit " + (10 + n) } }, job.Name, rev);
                });
                threads[i].Start();
            }
            foreach (Thread t in threads) t.Join();
            int ok = results.Count(x => x.Success);
            int stale = results.Count(x => !x.Success && x.Kind == ApplyFailure.Stale);
            T.True("round " + round + ": exactly one success and one Stale (" + results[0].Kind + ": " + results[0].Message + " / " + results[1].Kind + ": " + results[1].Message + ")", ok == 1 && stale == 1);
            ClassifiedTask live = e.Jobs.Reclassify(job.Name)!;
            T.Eq("round " + round + ": InSync", SyncStatus.InSync, live.Sync);
            string args = live.Job!.Run.Args;
            T.True("round " + round + ": the task holds one writer's job, whole", args is "/c exit 10" or "/c exit 11", args);
        }
    });

    public static void TwoRealCliProcessesNeverMixTheirChanges() => E2E.Scenario(e =>
    {
        const string Id = "att000cc";
        const string Name = "cross process job";
        Fx.Info("two real Cadence.exe --apply processes at once, 10 rounds (the CLI passes no revision: the last writer wins, but never a mixture)");
        T.True("created", ApplierFor(e, Actor.App).Save(Fx.Job(Name, ["0 18 * * *"]) with { Id = Id }, null).Success);
        string fileA = Write(e, "a.json", JobFile(Id, Name, cron: "0 18 * * *", args: "/d /c exit 21"));
        string fileB = Write(e, "b.json", JobFile(Id, Name, cron: "0 19 * * *", args: "/d /c exit 22"));
        int busy = 0;
        for (int round = 0; round < 10; round++)
        {
            // Alternate which file the starting job changes TO, so every round really changes something.
            Started a = Start("--apply", round % 2 == 0 ? fileA : fileB, "--yes", "--folder", "CadenceTest");
            Started b = Start("--apply", round % 2 == 0 ? fileB : fileA, "--yes", "--folder", "CadenceTest");
            Run ra, rb;
            try
            {
                ra = a.Finish(TimeSpan.FromSeconds(120));
                rb = b.Finish(TimeSpan.FromSeconds(120));
            }
            finally { a.Kill(); b.Kill(); }

            string both = ra.Stdout + rb.Stdout + ra.Stderr + rb.Stderr;
            bool oneBusy = both.Contains("Another Cadence window or agent is saving right now", StringComparison.Ordinal);
            if (oneBusy) busy++;
            T.True("round " + round + ": both exit 0, or one reports Busy (" + ra.Exit + "/" + rb.Exit + ")", (ra.Exit == 0 && rb.Exit == 0) || (oneBusy && ra.Exit + rb.Exit == 1));
            T.True("round " + round + ": no verification failure", !both.Contains("does not read back", StringComparison.Ordinal) && !both.Contains("Task Scheduler stored", StringComparison.Ordinal) && !both.Contains("verify", StringComparison.OrdinalIgnoreCase), both.Trim());

            ClassifiedTask live = e.Jobs.Reclassify(Name)!;
            T.Eq("round " + round + ": the task is InSync", SyncStatus.InSync, live.Sync);
            string cron = string.Join("|", live.Job!.Schedule.CronLines);
            string args = live.Job.Run.Args;
            bool isA = cron == "0 18 * * *" && args == "/d /c exit 21";
            bool isB = cron == "0 19 * * *" && args == "/d /c exit 22";
            T.True("round " + round + ": the final task equals exactly one of the two inputs, never a mixture (" + cron + " / " + args + ")", isA || isB);
            T.Eq("round " + round + ": the writer is the command line", "cli", live.Metadata!.UpdatedBy);
        }
        Fx.Info("cross-process rounds that reported Busy: " + busy);
    }, spawnsProcesses: true);

    public static void NoCadenceProcessIsLeftBehindByThesePhase19Tests()
    {
        int[] pids;
        lock (Pids) pids = [.. Pids];
        foreach (int pid in pids)
        {
            try
            {
                using Process p = Process.GetProcessById(pid);
                if (!p.HasExited) { p.Kill(entireProcessTree: true); p.WaitForExit(10000); T.True("process " + pid + " was still running and had to be killed", false); }
            }
            catch (ArgumentException) { /* gone */ }
            catch (InvalidOperationException) { /* gone */ }
        }
        T.True("every Cadence.exe this suite started (" + pids.Length + ") has exited", true);
    }
}
