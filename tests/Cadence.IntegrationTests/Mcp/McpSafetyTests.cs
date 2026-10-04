using System.Globalization;
using System.Text;
using System.Text.Json;
using Cadence.Agent.Audit;
using Cadence.Core.Metadata;
using Cadence.Core.Planning;
using Cadence.Core.TaskXml;
using Cadence.IntegrationTests.Agents;
using Cadence.Scheduler;
using Cadence.Scheduler.Apply;
using Cadence.TestHarness;
using static Cadence.IntegrationTests.Mcp.McpKit;

namespace Cadence.IntegrationTests.Mcp;

/// <summary>
/// Phase 22, the end-to-end SAFETY SUITE (PLAN 12.5, Review R CP-R11): every guarantee of the agent surface is attacked through the REAL <c>Cadence.exe --mcp --folder CadenceTest</c>
/// with the in-repo test client (<see cref="McpTestClient"/>) against a real \CadenceTest\. Harmless programs only; jobs are pinned to yesterday's date so nothing fires; the
/// only runs are explicit and use <c>E2E.RunAndWaitUnlessBlocked</c> (see <see cref="McpWriteToolTests"/>). Helper processes (servers, the CLI, the lock holder) are tracked and
/// checked by <see cref="ZzProcessHygiene"/>, which must stay the last test of the class.
/// </summary>
public static class McpSafetyTests
{
    static readonly string[] WriteTools = ["cadence_save_job", "cadence_set_job_enabled", "cadence_run_job_now", "cadence_delete_job"];

    static ClassifiedTask SaveByApp(E2E e, string name, string args = "/c exit 0") => e.Save(E2E.Job(name, "cmd.exe", args));

    static bool IsGone(McpReply r) => !r.IsError && r.Structured.TryGetProperty("alreadyGone", out JsonElement g) && g.GetBoolean();

    /// <summary>Each write tool, once, aimed at <paramref name="id"/>: every one must be refused (or report the job as not there) and nothing may be written.</summary>
    static void EveryWriteToolRefuses(McpTestClient c, string id, string name, string label, params string[] codes)
    {
        foreach ((string tool, string args) in new[]
        {
            ("cadence_set_job_enabled", Enable(id, false)), ("cadence_set_job_enabled", Enable(id, true)), ("cadence_run_job_now", Id(id)),
            ("cadence_delete_job", Delete(id, name)), ("cadence_save_job", Update(id, "0123456789ab", "\"enabled\":false")),
        })
        {
            McpReply r = Call(c, tool, args);
            T.True(label + ": " + tool + " is refused" + (r.IsError ? " (" + r.Code + ")" : ""), r.IsError ? codes.Contains(r.Code, StringComparer.Ordinal) : IsGone(r), r.Text);
        }
    }

    // ============================================================ 1. suspension, end to end ============================================================

    public static void SuspensionEndToEnd() => Scenario(e =>
    {
        ClassifiedTask j1 = SaveByApp(e, "Suspension job one"), j2 = SaveByApp(e, "Suspension job two");
        string id1 = j1.Job!.Id, id2 = j2.Job!.Id;
        string cron = Yesterday();
        using McpTestClient c = Start();
        try
        {
            string hash0 = FolderHash(e);
            string rev1 = JobRevision.Of(j1), rev2 = JobRevision.Of(j2);
            DateTime? ran1 = e.Gw.Get("Suspension job one")!.LastRunTime;

            T.Eq("CLI suspend: exit 0", 0, RealCli.Run(CliArgs("--agents", "suspend")).Exit);
            (string Tool, string Args)[] calls =
            [
                ("cadence_save_job", Create("Blocked create", cron)), ("cadence_save_job", Update(id1, rev1, "\"enabled\":false")),
                ("cadence_set_job_enabled", Enable(id1, false)), ("cadence_set_job_enabled", Enable(id1, true)), ("cadence_run_job_now", Id(id1)), ("cadence_delete_job", Delete(id2, "Suspension job two")),
                ("cadence_delete_job", Delete("zzzzzzzz", "nothing")),
            ];
            foreach ((string tool, string args) in calls)
            {
                McpReply r = Call(c, tool, args);
                T.True("suspended by the CLI: " + tool + " refuses on the very next call", r.IsError && r.Code == "suspended" && r.Message.StartsWith("AI agents are suspended on this PC", StringComparison.Ordinal), r.Text);
            }
            T.Eq("suspended: the folder listing hash is unchanged", hash0, FolderHash(e));
            T.True("suspended: every job's revision is unchanged and nothing ran", JobRevision.Of(e.Jobs.Reclassify("Suspension job one")!) == rev1 && JobRevision.Of(e.Jobs.Reclassify("Suspension job two")!) == rev2 && e.Gw.Get("Suspension job one")!.LastRunTime == ran1);
            T.True("suspended: reading keeps working (get_job, list_jobs, get_status)", !Call(c, "cadence_get_job", Id(id1)).IsError && !Call(c, "cadence_list_jobs", "{}").IsError
                && Call(c, "cadence_get_status", "{}").Structured.GetProperty("agents").GetProperty("suspended").GetBoolean());

            foreach (string attempt in new[] { "cadence_set_agents_suspended", "agents_resume", "cadence_resume", "cadence_set_suspended", "cadence_set_limits", "cadence_unsuspend" })
                T.Eq(attempt + " is not a tool (-32602)", -32602, RpcErrorCode(c, attempt));

            // [OWNER-Q14] An agent can use the two switch tools: they are named, attributed and audited, and are the only way an agent touches the policy.
            McpReply resumed = Call(c, "cadence_resume_agents", "{\"reason\":\"the user asked me to continue\"}");
            T.True("the agent's own resume tool works while suspended: " + resumed.Text, !resumed.IsError && resumed.Structured.GetProperty("changed").GetBoolean() && !resumed.Structured.GetProperty("suspended").GetBoolean());
            T.Eq("the policy changed by an agent (stored route)", "agent:Cadence Test Agent", resumed.Structured.GetProperty("changedBy").GetString());
            McpReply off = Call(c, "cadence_set_job_enabled", Enable(id1, false));
            T.True("after the agent's resume the write works on the next call: " + off.Text, !off.IsError && off.Str("outcome") == "updated");

            McpReply suspended = Call(c, "cadence_suspend_agents", "{\"reason\":\"safety test\"}");
            T.True("the agent's suspend tool works: " + suspended.Text, !suspended.IsError && suspended.Structured.GetProperty("suspended").GetBoolean());
            string hash1 = FolderHash(e);
            foreach ((string tool, string args) in calls)
                T.True("suspended by the agent: " + tool + " refuses", Call(c, tool, args) is { IsError: true } r && r.Code == "suspended");
            T.Eq("suspended by the agent: the folder is byte-identical", hash1, FolderHash(e));
            RealCli.Result status = RealCli.Run(CliArgs("--agents", "status"));
            T.True("the CLI sees the agent's suspension (owner-visible)", status.Exit == 0 && status.Stdout.Contains("SUSPENDED", StringComparison.Ordinal), status.Stdout);

            T.Eq("CLI resume: exit 0", 0, RealCli.Run(CliArgs("--agents", "resume")).Exit);
            McpReply on = Call(c, "cadence_set_job_enabled", Enable(id1, true));
            T.True("resumed by the CLI: the same session writes again, no restart: " + on.Text, !on.IsError && on.Str("outcome") == "updated");
            Finish(c, "suspension session");
        }
        finally { c.Kill(); }

        IReadOnlyList<AuditEntry> lines = [.. new FileAuditLog(Folder).Tail(1000)];
        T.True("audit: the agent's suspend and resume are recorded with its name", lines.Any(x => x.Tool == "cadence_suspend_agents" && x.Outcome == "suspended" && x.Client == "Cadence Test Agent") && lines.Any(x => x.Tool == "cadence_resume_agents" && x.Outcome == "resumed"));
        T.True("audit: every write tool has a refused/suspended line", WriteTools.All(t => lines.Any(x => x.Tool == t && x.Outcome == "refused" && x.Code == "suspended")));
    });

    // ============================================================ 2. foreign and damaged tasks are untouchable ============================================================

    public static void ForeignAndDamagedTasksAreUntouchableByEveryTool() => Scenario(e =>
    {
        ClassifiedTask good = SaveByApp(e, "Good managed job");
        // (a) a raw task: no marker at all.
        e.Gw.Register("Raw plain task", Fx.Xml(Fx.FarTrigger));
        // (b) a Cadence job edited in Task Scheduler (drifted).
        SaveByApp(e, "Drifted job");
        ApplyFx.MoveFirstTrigger("Drifted job", 5);
        // (c) written by a newer generator, (d) metadata of a newer schema, (e) damaged metadata.
        SaveByApp(e, "Newer generation job");
        ApplyFx.RegisterWithEditedMetadata(e.Gw, "Newer generation job", m => m with { Gen = m.Gen + 1 });
        TaskDefinitionModel model = TaskXmlReader.Read(e.Gw.Get("Good managed job")!.Xml);
        e.Gw.Register("Newer schema task", TaskXmlWriter.Write(model with { RegistrationInfo = model.RegistrationInfo with { Documentation = "cadence/2\n{\"schema\":2}" } }));
        e.Gw.Register("Damaged metadata task", TaskXmlWriter.Write(model with { RegistrationInfo = model.RegistrationInfo with { Documentation = "cadence/1\n{broken" } }));
        // (f) a copy of a job (a copied Cadence marker under another task name: same job id, wrong $tp).
        e.Gw.Register("Z copy of the good job", e.Gw.Get("Good managed job")!.Xml);

        string[] odd = ["Raw plain task", "Drifted job", "Newer generation job", "Newer schema task", "Damaged metadata task", "Z copy of the good job"];
        Dictionary<string, string> hashes = odd.ToDictionary(n => n, n => TaskHash(e, n));
        DateTime?[] lastRuns = [.. odd.Select(n => e.Gw.Get(n)!.LastRunTime)];

        using McpTestClient c = Start();
        try
        {
            McpReply list = Call(c, "cadence_list_jobs", "{}");
            JsonElement[] rows = [.. list.Structured.GetProperty("jobs").EnumerateArray()];
            T.True("the raw task is not listed", rows.All(r => r.GetProperty("name").GetString() != "Raw plain task"));
            T.True("every other odd task is listed only as a non-writable problem row", rows.Where(r => r.GetProperty("state").GetString() != "ok").All(r => r.GetProperty("problem").ValueKind == JsonValueKind.String));

            // Every id the agent can see for a task that is not a healthy original: every write tool refuses.
            int tried = 0;
            foreach (JsonElement r in rows)
            {
                string state = r.GetProperty("state").GetString()!;
                // A copy row carries the id of its ORIGINAL (a healthy job, writable by design: the original wins); the copy itself is checked by its hash below.
                if (state is "ok" or "copy" || r.GetProperty("jobId").ValueKind != JsonValueKind.String) continue;
                tried++;
                EveryWriteToolRefuses(c, r.GetProperty("jobId").GetString()!, r.GetProperty("name").GetString()!, "'" + r.GetProperty("name").GetString() + "' (" + state + ")", "not_allowed_in_this_state", "not_found");
            }
            T.True("at least the drifted and the newer-generation jobs were attacked (" + tried + ")", tried >= 2);

            // The id shared by a copy resolves to the original only; the copy stays byte-identical.
            McpReply viaCopyId = Call(c, "cadence_set_job_enabled", Enable(good.Job!.Id, false));
            T.True("the id of a copied job changes the original only: " + viaCopyId.Text, !viaCopyId.IsError && TaskHash(e, "Z copy of the good job") == hashes["Z copy of the good job"]);

            // Guesses: names, paths, wildcards, other folders' tasks. None is an id; none is accepted.
            foreach (string guess in new[] { "Raw plain task", "raw", "*", "..\\Raw plain task", "\\CadenceTest\\Raw plain task", "\\Microsoft\\Windows\\Defrag\\ScheduledDefrag", "Newer schema task", "00000000", "zzzzzzzz" })
                foreach (string tool in WriteTools)
                {
                    string args = tool switch
                    {
                        "cadence_set_job_enabled" => Enable(guess, false),
                        "cadence_delete_job" => Delete(guess, "Raw plain task"),
                        "cadence_save_job" => Update(guess, "0123456789ab", "\"enabled\":false"),
                        _ => Id(guess),
                    };
                    McpReply r = Call(c, tool, args);
                    T.True("guess '" + guess + "' / " + tool + ": refused or not there", r.IsError || IsGone(r), r.Text);
                }
            foreach (string tool in new[] { "cadence_set_job_enabled", "cadence_run_job_now", "cadence_delete_job", "cadence_save_job" })
                T.True(tool + ": a taskPath/taskName argument is not a thing", Call(c, tool, "{\"taskPath\":\"\\\\CadenceTest\\\\Raw plain task\",\"jobId\":\"" + good.Job!.Id + "\"}") is { IsError: true } r && r.Code == "invalid_argument");

            Finish(c, "foreign session");
        }
        finally { c.Kill(); }

        foreach (string n in odd) T.Eq("'" + n + "' is byte-identical after every attempt", hashes[n], TaskHash(e, n));
        for (int i = 0; i < odd.Length; i++) T.True("'" + odd[i] + "' never ran", e.Gw.Get(odd[i])!.LastRunTime == lastRuns[i]);
        T.Eq("the raw task is still Foreign", Ownership.Foreign, e.Jobs.Reclassify("Raw plain task")!.Ownership);
        T.True("the good job is still there (nothing was deleted)", e.Gw.Get("Good managed job") is not null);
    });

    // ============================================================ 3. a locked job is untouchable ============================================================

    public static void ALockedJobIsUntouchableByEveryWriteTool() => Scenario(e =>
    {
        ClassifiedTask job = SaveByApp(e, "Locked job");
        string id = job.Job!.Id;
        T.Eq("CLI lock: exit 0", 0, RealCli.Run(CliArgs("--agents", "lock", id, "--yes")).Exit);
        string hash = TaskHash(e, "Locked job");
        string revision = JobRevision.Of(e.Jobs.Reclassify("Locked job")!);
        DateTime? ran = e.Gw.Get("Locked job")!.LastRunTime;

        using McpTestClient c = Start();
        try
        {
            T.True("the job is shown as locked", Call(c, "cadence_get_job", Id(id)).Structured.GetProperty("lockedForAgents").GetBoolean());
            foreach ((string tool, string args) in new[]
            {
                ("cadence_set_job_enabled", Enable(id, false)), ("cadence_set_job_enabled", Enable(id, true, revision)), ("cadence_run_job_now", Id(id)), ("cadence_delete_job", Delete(id, "Locked job")),
                ("cadence_save_job", Update(id, revision, "\"program\":{\"arguments\":\"/c exit 9\"}")), ("cadence_save_job", Update(id, revision, "\"name\":\"Renamed by agent\"")),
                ("cadence_delete_job", Delete(id, "wrong name")),
            })
            {
                McpReply r = Call(c, tool, args);
                T.True("locked: " + tool + " is refused with 'locked'", r.IsError && r.Code == "locked" && r.Message.StartsWith("The owner locked this job against changes by AI agents.", StringComparison.Ordinal), r.Text);
            }
            T.True("an agent cannot send or clear the lock", Call(c, "cadence_save_job", Update(id, revision, "\"agentLock\":false")) is { IsError: true } l && l.Code == "invalid_argument");
            T.Eq("the locked task is byte-identical", hash, TaskHash(e, "Locked job"));
            T.True("it never ran and is still locked", e.Gw.Get("Locked job")!.LastRunTime == ran && e.Jobs.Reclassify("Locked job")!.Job!.AgentLock);

            // The owner unlocks through the CLI: the same session can write on the next call.
            T.Eq("CLI unlock: exit 0", 0, RealCli.Run(CliArgs("--agents", "unlock", id, "--yes")).Exit);
            McpReply ok = Call(c, "cadence_set_job_enabled", Enable(id, false));
            T.True("unlocked: the write works: " + ok.Text, !ok.IsError && ok.Str("outcome") == "updated");
            Finish(c, "lock session");
        }
        finally { c.Kill(); }
        T.True("audit: every write tool has a refused/locked line", WriteTools.All(t => new FileAuditLog(Folder).Tail(1000).Any(x => x.Tool == t && x.Outcome == "refused" && x.Code == "locked")));
    });

    // ============================================================ 4. two writers ============================================================

    public static void TwoAgentsSavingAtTheSameMomentExactlyOneWins() => Scenario(e =>
    {
        ClassifiedTask job = SaveByApp(e, "Contended job");
        string id = job.Job!.Id;
        using McpTestClient a = Start("Agent A"), b = Start("Agent B");
        try
        {
            int updated = 0, stale = 0;
            for (int round = 1; round <= 10; round++)
            {
                string rev = Revision(a, id);
                var barrier = new Barrier(2);
                McpReply? ra = null, rb = null;
                Task ta = Task.Run(() => { barrier.SignalAndWait(); ra = Call(a, "cadence_save_job", Update(id, rev, "\"program\":{\"arguments\":\"/c exit " + round.ToString(CultureInfo.InvariantCulture) + "1\"}")); });
                Task tb = Task.Run(() => { barrier.SignalAndWait(); rb = Call(b, "cadence_save_job", Update(id, rev, "\"program\":{\"arguments\":\"/c exit " + round.ToString(CultureInfo.InvariantCulture) + "2\"}")); });
                T.True("round " + round + ": both calls answered", Task.WaitAll([ta, tb], 120000));
                int wins = new[] { ra!, rb! }.Count(r => !r.IsError && r.Str("outcome") == "updated");
                int stales = new[] { ra!, rb! }.Count(r => r.IsError && r.Code == "stale_revision");
                T.True("round " + round + ": exactly one updated and one stale_revision (" + (ra!.IsError ? ra.Code : "ok") + ", " + (rb!.IsError ? rb.Code : "ok") + ")", wins == 1 && stales == 1);
                updated += wins;
                stale += stales;
                ClassifiedTask live = e.Jobs.Reclassify("Contended job")!;
                T.True("round " + round + ": the task is in sync and holds exactly one of the two changes", live.Sync == SyncStatus.InSync && live.Job!.Run.Args.StartsWith("/c exit " + round.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
            }
            T.Eq("10 rounds: 10 winners and 10 stale refusals", "10/10", updated + "/" + stale);
            Finish(a, "agent A");
            Finish(b, "agent B");
        }
        finally { a.Kill(); b.Kill(); }
    });

    public static void TheAppTheCommandLineAndTheAgentCannotOverwriteEachOther() => Scenario(e =>
    {
        ClassifiedTask job = SaveByApp(e, "Shared job");
        string id = job.Job!.Id;
        using McpTestClient c = Start();
        try
        {
            // The app saves between the agent's read and the agent's write.
            string seen = Revision(c, id);
            ClassifiedTask live = e.Jobs.Reclassify("Shared job")!;
            var app = new Applier(new ManagedJobs(e.Gw, () => DateTimeOffset.Now, Actor.App));
            T.True("the app edits the job", app.Save(live.Job! with { Run = live.Job.Run with { Args = "/c exit 21" } }, "Shared job").Success);
            McpReply s1 = Call(c, "cadence_save_job", Update(id, seen, "\"program\":{\"arguments\":\"/c exit 22\"}"));
            T.True("app vs agent: stale_revision naming the app", s1.IsError && s1.Code == "stale_revision" && s1.Message.Contains("by Cadence (the app) after you read it", StringComparison.Ordinal), s1.Text);
            McpReply d1 = Call(c, "cadence_delete_job", Delete(id, "Shared job"));
            T.True("the delete the agent confirmed by name still works against the CURRENT job (it re-reads)", !d1.IsError && d1.Structured.GetProperty("deleted").GetBoolean(), d1.Text);

            // The command line changes the job (lock then unlock) between the agent's read and write.
            ClassifiedTask j2 = SaveByApp(e, "Shared job two");
            string id2 = j2.Job!.Id;
            string seen2 = Revision(c, id2);
            T.Eq("CLI lock: exit 0", 0, RealCli.Run(CliArgs("--agents", "lock", id2, "--yes")).Exit);
            T.Eq("CLI unlock: exit 0", 0, RealCli.Run(CliArgs("--agents", "unlock", id2, "--yes")).Exit);
            McpReply s2 = Call(c, "cadence_set_job_enabled", Enable(id2, false, seen2));
            T.True("CLI vs agent: stale_revision naming the command line", s2.IsError && s2.Code == "stale_revision" && s2.Message.Contains("the command line", StringComparison.Ordinal), s2.Text);
            T.True("nothing was written by the stale call", e.Jobs.Reclassify("Shared job two")!.Job!.Enabled);
            T.True("with the fresh revision it works", Call(c, "cadence_set_job_enabled", Enable(id2, false, Revision(c, id2))) is { IsError: false });
            Finish(c, "app/cli session");
        }
        finally { c.Kill(); }
    });

    public static void AWriterLockHeldByAnotherProcessIsBusyAndAKilledHolderIsReleased() => Scenario(e =>
    {
        ClassifiedTask job = SaveByApp(e, "Lock job");
        string id = job.Job!.Id;
        string hash = TaskHash(e, "Lock job");
        using McpTestClient c = Start();
        try
        {
            using var holder = new LockHolderProcess();
            DateTime t0 = DateTime.UtcNow;
            McpReply busy = Call(c, "cadence_set_job_enabled", Enable(id, false), timeoutMs: 60000);
            double waited = (DateTime.UtcNow - t0).TotalSeconds;
            T.True("held by another process: busy after the writer-lock timeout (" + waited.ToString("0.0", CultureInfo.InvariantCulture) + " s)", busy.IsError && busy.Code == "busy" && waited >= 10, busy.Text);
            T.True("busy says nothing was changed and to try again", busy.Structured.GetProperty("error").GetProperty("hint").GetString()!.Contains("Nothing was changed", StringComparison.Ordinal), busy.Text);
            T.Eq("the task is byte-identical", hash, TaskHash(e, "Lock job"));

            holder.Kill();   // the holder dies while holding the mutex: it is ABANDONED, and the next writer proceeds at once
            DateTime t1 = DateTime.UtcNow;
            McpReply ok = Call(c, "cadence_set_job_enabled", Enable(id, false));
            T.True("after the holder was killed the next write succeeds at once (" + (DateTime.UtcNow - t1).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s): " + ok.Text, !ok.IsError && ok.Str("outcome") == "updated" && (DateTime.UtcNow - t1).TotalSeconds < 10);
            T.True("and the job is in sync", e.Jobs.Reclassify("Lock job") is { Sync: SyncStatus.InSync, Job.Enabled: false });
            Finish(c, "busy session");
        }
        finally { c.Kill(); }
    });

    public static void AServerKilledWhileAWriteWaitsLeavesNothingBehindAndEofWaitsForAWriteInProgress() => Scenario(e =>
    {
        string cron = Yesterday();

        // (10a) a write stalled behind a lock held by another process, then the server is killed: nothing was written, the lock is not left held.
        using (var holder = new LockHolderProcess())
        {
            var victim = Start();
            try
            {
                victim.SendRaw("{\"jsonrpc\":\"2.0\",\"id\":901,\"method\":\"tools/call\",\"params\":{\"name\":\"cadence_save_job\",\"arguments\":" + Create("Stalled create", cron) + "}}");
                Thread.Sleep(2500);
                T.True("the write is stalled (no answer, server alive)", !victim.HasExited && !victim.StdoutLines.Any(l => l.Contains("\"id\":901", StringComparison.Ordinal)));
                victim.Kill();
                T.True("the killed server is gone", victim.HasExited);
            }
            finally { victim.Dispose(); }
            holder.Kill();
        }
        T.True("nothing was created by the killed write", e.Jobs.Load(false).Count == 0);
        using (McpTestClient fresh = Start())
        {
            try
            {
                McpReply next = Call(fresh, "cadence_save_job", Create("After the kill", cron));
                T.True("the next write from a fresh server succeeds (abandoned mutex): " + next.Text, !next.IsError && next.Str("outcome") == "created");
                T.True("and the job is in sync", e.Jobs.Reclassify("After the kill") is { Sync: SyncStatus.InSync });
                Finish(fresh, "fresh server");
            }
            finally { fresh.Kill(); }
        }

        // (10b) CP-R7: end of input while a write waits for the lock: the server finishes THAT write, answers it, then exits 0 (a queued, unstarted one would be dropped).
        using var holder2 = new LockHolderProcess();
        var drain = Start();
        try
        {
            drain.SendRaw("{\"jsonrpc\":\"2.0\",\"id\":902,\"method\":\"tools/call\",\"params\":{\"name\":\"cadence_save_job\",\"arguments\":" + Create("Drained create", cron) + "}}");
            Thread.Sleep(1500);
            bool exitedEarly = drain.CloseStdinAndWait(3500);
            T.True("EOF during a stalled write: the server does NOT exit while the write is in progress", !exitedEarly);
            holder2.Kill();
            bool exited = drain.CloseStdinAndWait(60000);
            T.True("once the lock is free the write finishes and the server exits", exited);
            if (exited) T.Eq("exit code 0", 0, drain.ExitCode);
            string? answer = drain.StdoutLines.FirstOrDefault(l => l.Contains("\"id\":902", StringComparison.Ordinal));
            T.True("the write was answered (created)", answer is not null && answer.Contains("created", StringComparison.Ordinal), answer);
            T.True("and the job exists and is in sync", e.Jobs.Reclassify("Drained create") is { Sync: SyncStatus.InSync });
        }
        finally { drain.Dispose(); }
    });

    // ============================================================ 5. malicious and oversized input ============================================================

    static string SaveJson(string name, string cron, string path, string args, IEnumerable<KeyValuePair<string, string>>? env = null)
    {
        var program = new Dictionary<string, object> { ["path"] = path, ["arguments"] = args };
        if (env is not null) program["environment"] = env.ToDictionary(kv => kv.Key, kv => kv.Value);
        return JsonSerializer.Serialize(new Dictionary<string, object> { ["name"] = name, ["schedule"] = new Dictionary<string, object> { ["cron"] = new[] { cron } }, ["program"] = program });
    }

    public static void MaliciousAndOversizedInputIsRefusedByEveryTool() => Scenario(e =>
    {
        ClassifiedTask victim = SaveByApp(e, "Victim job");
        string vid = victim.Job!.Id;
        string foreignName = "Bystander task";
        e.Gw.Register(foreignName, Fx.Xml(Fx.FarTrigger));
        string foreignHash = TaskHash(e, foreignName);
        string victimHash = TaskHash(e, "Victim job");
        string cron = Yesterday();
        string system = "C:\\Windows\\System32\\cmd.exe";

        // ---- protocol level: a 2 MiB line, JSON nested 100 deep ----
        using (McpTestClient p = Start())
        {
            try
            {
                byte[] big = new byte[2 << 20];
                Array.Fill(big, (byte)'a');
                var writer = Task.Run(() => p.SendRawBytes([.. big, (byte)'\n']));
                JsonElement? over = p.ReceiveLine(30000);
                T.True("the 2 MiB line was written", writer.Wait(30000));
                T.Eq("a 2 MiB line is -32600", -32600, over?.GetProperty("error").GetProperty("code").GetInt32());
                T.True("ping works afterwards", p.Request("ping") is not null);
                p.SendRaw("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{\"name\":\"cadence_get_status\",\"arguments\":" + string.Concat(Enumerable.Repeat("{\"a\":", 100)) + "1" + new string('}', 100) + "}}");
                T.Eq("JSON nested 100 deep is -32700", -32700, p.ReceiveLine()?.GetProperty("error").GetProperty("code").GetInt32());
                T.True("ping works afterwards too", p.Request("ping") is not null);
                Finish(p, "protocol session");
            }
            finally { p.Kill(); }
        }

        using McpTestClient c = Start();
        try
        {
            // ---- names, schedules, programs, arguments ----
            McpReply traversal = Call(c, "cadence_save_job", Create("..\\..\\Windows\\System32\\x", cron));
            T.True("a path-like name is just a name: " + traversal.Text, !traversal.IsError);
            string tp = traversal.Str("job", "taskPath");
            T.True("its task path is inside \\CadenceTest\\ and has no extra folder level (" + tp + ")", tp.StartsWith("\\CadenceTest\\", StringComparison.Ordinal) && tp.IndexOf('\\', "\\CadenceTest\\".Length) < 0 && !tp.Contains("..", StringComparison.Ordinal), tp);
            T.True("exactly one new task exists and it is in the managed folder", e.Jobs.Load(false).Count(j => j.Job is not null && j.Job.Name.Contains("System32", StringComparison.Ordinal)) == 1);
            string travId = traversal.Str("job", "jobId");
            McpReply travDelete = Call(c, "cadence_delete_job", Delete(travId, "..\\..\\Windows\\System32\\x"));
            T.True("and it deletes by its exact name: " + travDelete.Text, !travDelete.IsError && travDelete.Structured.GetProperty("deleted").GetBoolean());

            (string What, McpReply Reply, string Code)[] refusals =
            [
                ("cron with a shell tail", Call(c, "cadence_save_job", Create("Shell tail", "0 9 * * * ; calc")), "schedule_invalid"),
                ("a UNC program", Call(c, "cadence_save_job", SaveJson("Unc", cron, "\\\\localhost\\c$\\Windows\\System32\\cmd.exe", "/c exit 0")), "program_rejected"),
                ("a \\\\?\\ program", Call(c, "cadence_save_job", SaveJson("Verbatim", cron, "\\\\?\\C:\\Windows\\System32\\cmd.exe", "/c exit 0")), "program_rejected"),
                ("CR/LF in the arguments", Call(c, "cadence_save_job", SaveJson("Crlf", cron, system, "/c echo a\r\nb")), "program_rejected"),
                ("8001 characters of arguments", Call(c, "cadence_save_job", SaveJson("Long", cron, system, new string('a', 8001))), "invalid_argument"),
                ("31 environment variables", Call(c, "cadence_save_job", SaveJson("Env", cron, system, "/c exit 0", Enumerable.Range(0, 31).Select(i => KeyValuePair.Create("V" + i.ToString(CultureInfo.InvariantCulture), "x")))), "program_rejected"),
                ("an environment name with =", Call(c, "cadence_save_job", SaveJson("EnvName", cron, system, "/c exit 0", [KeyValuePair.Create("A=B", "x")])), "program_rejected"),
                ("a bare program name", Call(c, "cadence_save_job", SaveJson("Bare", cron, "cmd.exe", "/c exit 0")), "program_rejected"),
                ("a relative program path", Call(c, "cadence_save_job", SaveJson("Rel", cron, "..\\Windows\\System32\\cmd.exe", "/c exit 0")), "program_rejected"),
                ("the unknown property agentLock", Call(c, "cadence_save_job", Update(vid, Revision(c, vid), "\"agentLock\":false")), "invalid_argument"),
                ("an unknown property in program", Call(c, "cadence_save_job", Create("Extra", cron, extraProgram: ",\"runAs\":\"SYSTEM\"")), "invalid_argument"),
                ("a null name", Call(c, "cadence_save_job", "{\"name\":null}"), "invalid_argument"),
                ("a jobId with a path", Call(c, "cadence_delete_job", Delete("..\\..\\x", "Victim job")), "invalid_argument"),
                ("a wildcard job id", Call(c, "cadence_delete_job", Delete("*", "Victim job")), "invalid_argument"),
                ("a confirmName mismatch", Call(c, "cadence_delete_job", Delete(vid, "Victim")), "invalid_argument"),
                ("a confirmName wildcard", Call(c, "cadence_delete_job", Delete(vid, "*")), "invalid_argument"),
                ("a 101-character confirmName", Call(c, "cadence_delete_job", Delete(vid, new string('x', 101))), "invalid_argument"),
                ("an arguments value of the wrong type", Call(c, "cadence_run_job_now", "[\"" + vid + "\"]"), "rpc-32602"),
                ("a command smuggled into run now", Call(c, "cadence_run_job_now", "{\"jobId\":\"" + vid + "\",\"command\":\"calc.exe\"}"), "invalid_argument"),
            ];
            foreach ((string what, McpReply r, string code) in refusals)
                T.True(what + ": " + code, r.IsError && r.Code == code, r.Code + " " + r.Text);

            // CP-R9 (11): a command that reaches back into Cadence is refused whatever program carries it.
            foreach (string args in new[]
            {
                "/c Cadence.exe --agents resume --yes", "/c \"C:\\x\\Cadence.exe\" --agents resume --yes", "--agents resume", "/c start Cadence-standalone.exe", "/c Cad^ence.exe --ag^ents resume",
                "reg add HKCU\\Software\\Cadence\\Agents /v Suspended /d 0", "-NoProfile -Command \"& 'C:\\x\\Cadence.exe' --mcp\"",
            })
            {
                McpReply r = Call(c, "cadence_save_job", SaveJson("Selfish", cron, system, args));
                T.True("CP-R9 '" + args + "': program_rejected", r.IsError && r.Code == "program_rejected", r.Text);
            }
            T.True("agents are still not suspended and the victim and the bystander are byte-identical", Call(c, "cadence_get_status", "{}").Structured.GetProperty("agents").GetProperty("suspended").GetBoolean() == false
                && TaskHash(e, "Victim job") == victimHash && TaskHash(e, foreignName) == foreignHash);
            T.True("no job was created by any refused request", e.Jobs.Load(false).Select(j => j.Job!.Name).OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(["Victim job"]));
            Finish(c, "malicious session");
        }
        finally { c.Kill(); }

        // ---- the rate limit: the 31st write call of a minute ----
        using McpTestClient r2 = Start();
        try
        {
            int limited = 0;
            for (int i = 1; i <= 31; i++)
                if (Call(r2, "cadence_set_job_enabled", Enable(vid, true)) is { IsError: true } r && r.Code == "rate_limited") limited++;
            T.Eq("31 write calls within a minute: exactly the last is rate_limited", 1, limited);
            Finish(r2, "rate limit session");
        }
        finally { r2.Kill(); }
        T.Eq("the victim is byte-identical after all of it", victimHash, TaskHash(e, "Victim job"));
        T.Eq("the bystander is byte-identical after all of it", foreignHash, TaskHash(e, foreignName));
    });

    // ============================================================ 6. attribution that tries to look like the owner ============================================================

    public static void AnAgentCallingItselfTheAppIsStillAnAgent() => Scenario(e =>
    {
        string cron = Yesterday();
        using McpTestClient c = Start("Cadence (the app)");
        try
        {
            McpReply made = Call(c, "cadence_save_job", Create("Spoof job", cron));
            T.True("the spoofing agent saves: " + made.Text, !made.IsError);
            string id = made.Str("job", "jobId");
            T.Eq("cadence_get_job shows it as an AI agent", "Cadence (the app) (AI agent)", Call(c, "cadence_get_job", Id(id)).Str("updated", "by"));
            T.Eq("and the kind is agent", "agent", Call(c, "cadence_get_job", Id(id)).Str("updated", "byKind"));

            // The owner's side (the app) saves with the revision it read before the agent's change: its stale message names the agent, not the app.
            string seen = JobRevision.Of(e.Jobs.Reclassify("Spoof job")!);
            McpReply upd = Call(c, "cadence_save_job", Update(id, seen, "\"program\":{\"arguments\":\"/c exit 5\"}"));
            T.True("the agent changes the job", !upd.IsError);
            ClassifiedTask live = e.Jobs.Reclassify("Spoof job")!;
            ApplyResult stale = new Applier(new ManagedJobs(e.Gw, () => DateTimeOffset.Now, Actor.App)).Save(live.Job! with { Enabled = false }, "Spoof job", seen);
            T.True("the app's save is stale and names 'Cadence (the app) (AI agent)'", !stale.Success && stale.Kind == ApplyFailure.Stale && stale.Message.Contains("by Cadence (the app) (AI agent) after you read it", StringComparison.Ordinal), stale.Message);
            Finish(c, "spoof session");
        }
        finally { c.Kill(); }
    });

    // ============================================================ 7. process hygiene (keep last) ============================================================

    public static void ZzProcessHygiene()
    {
        List<string> alive = McpTestClient.AliveDescriptions();
        T.True("no MCP server process started by the tests is still running (" + string.Join("; ", alive) + ")", alive.Count == 0);
        T.True("no CLI process started by the tests is still running", !RealCli.AnyAlive());
        T.True("no lock-holder helper is still running", !AnyHelperAlive());
        // The user may have real schedules, settings and data in the product's locations, so they are not required to be absent: the run must not have created or changed anything there.
        // The baseline was taken by Program.cs before the first test (ProductLocations); a fresh snapshot is compared with it.
        ProductLocations? baseline = ProductLocations.Baseline;
        T.True("a baseline of the product's locations was taken before the run", baseline is not null);
        if (baseline is null) return;
        List<(string Area, string Text)> changes = ProductLocations.Differences(baseline, ProductLocations.Take());
        string Said(string area) => string.Join("; ", changes.Where(c => c.Area == area).Select(c => c.Text));
        T.True("the product's agent key (HKCU\\Software\\Cadence) is as it was before the run", Said("key").Length == 0, Said("key"));
        T.True("the product's task folder (\\Cadence: existence and number of tasks) is as it was before the run", Said("tasks").Length == 0, Said("tasks"));
        T.True("the product's data folder (%LOCALAPPDATA%\\Cadence) is as it was before the run", Said("data").Length == 0, Said("data"));
    }
}
