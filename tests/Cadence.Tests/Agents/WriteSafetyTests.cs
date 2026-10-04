using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cadence.Mcp;
using Cadence.Core.Metadata;
using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.Scheduler;
using Cadence.Scheduler.Apply;
using Cadence.Tests.Mcp;
using Cadence.TestHarness;
using static Cadence.Tests.PlanSamples;

namespace Cadence.Tests;

/// <summary>
/// Phase 21: the pieces of the write path that live below the tool. The Applier's pre-write guard hook (evaluated inside the writer lock), the per-job lock enforced
/// in <c>ManagedJobs</c> and the Applier for every agent writer ([CP-R6]), and the server finishing a write in progress before it exits ([CP-R7]).
/// </summary>
public static class WriteSafetyTests
{
    static string NewFolder() => "CadenceGuardTest" + Guid.NewGuid().ToString("N")[..10];

    sealed class Rig
    {
        public readonly string Folder = NewFolder();
        public readonly FakeGateway Gw;
        public DateTimeOffset Clock = ComposeSamples.Now;

        public Rig() => Gw = new FakeGateway(Folder);

        public ManagedJobs Jobs(Actor? actor) => new(Gw, () => Clock, actor);

        public Applier Applier(Actor? actor, Func<string?>? guard = null) => new(Jobs(actor), TimeSpan.FromSeconds(5), guard);

        public ClassifiedTask Live(string name = StockName) => Jobs(null).Reclassify(name)!;
    }

    static JobDefinition Stock(string args = "-u a.py", bool locked = false) =>
        ComposeSamples.StockIngest() with { Run = ComposeSamples.StockIngest().Run with { Args = args }, AgentLock = locked };

    static void Seed(Rig r, bool locked = false)
    {
        ApplyResult res = r.Applier(Actor.App).Save(Stock(locked: locked), null);
        T.True("fixture: seeded (" + res.Message + ")", res.Success);
    }

    // ---- the Applier's pre-write guard ----

    public static void TheGuardRefusesInsideTheLockAndNothingIsWritten()
    {
        var r = new Rig();
        int calls = 0;
        bool heldDuringGuard = false;
        string? reason = null;
        Func<string?> guard = () =>
        {
            calls++;
            // Another thread can't take the writer lock while the guard runs: it runs INSIDE the lock.
            heldDuringGuard = Task.Run(() => { using WriterLock? l = WriterLock.TryAcquire(r.Folder, TimeSpan.Zero); return l is null; }).GetAwaiter().GetResult();
            return reason;
        };

        ApplyResult ok = r.Applier(Actor.App, guard).Save(Stock(), null);
        T.True("a guard that returns null lets the write through: " + ok.Message, ok.Success);
        T.Eq("the guard ran once", 1, calls);
        T.True("and it ran while the writer lock was held", heldDuringGuard);
        T.Eq("one write", 1, r.Gw.Writes.Count);
        string xml = r.Gw.XmlOf(StockName)!;

        reason = "Agents are suspended right now.";
        ApplyResult refused = r.Applier(Actor.App, guard).Save(Stock("-u b.py"), StockName);
        T.True("a guard that returns a reason refuses", !refused.Success);
        T.Eq("as Refused", ApplyFailure.Refused, refused.Kind);
        T.Eq("with the guard's own text", "Agents are suspended right now.", refused.Message);
        T.Eq("nothing was written", 1, r.Gw.Writes.Count);
        T.Eq("the task is byte-identical", xml, r.Gw.XmlOf(StockName));
        T.Eq("the guard ran once more", 2, calls);

        // Every kind of write passes the guard: delete and apply of a forged plan too.
        T.True("delete is refused by the guard", !r.Applier(Actor.App, guard).Delete(StockName).Success);
        T.True("reapply is refused by the guard", !r.Applier(Actor.App, guard).Reapply(StockName).Success);
        PlannedChange plan = r.Jobs(Actor.App).PlanSave(Stock("-u c.py"), StockName);
        T.True("Apply(plan) is refused by the guard", !r.Applier(Actor.App, guard).Apply(plan).Success);
        T.Eq("still nothing written", 1, r.Gw.Writes.Count);
        T.Eq("still byte-identical", xml, r.Gw.XmlOf(StockName));
    }

    public static void TheGuardIsNotEvaluatedWhenTheLockIsBusyAndAppsHaveNone()
    {
        var r = new Rig();
        int calls = 0;
        using var held = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var holder = new Thread(() =>
        {
            using WriterLock? l = WriterLock.TryAcquire(r.Folder, TimeSpan.FromSeconds(5));
            held.Set();
            release.Wait(TimeSpan.FromSeconds(20));
        }) { IsBackground = true };
        holder.Start();
        T.True("fixture: another thread holds the lock", held.Wait(TimeSpan.FromSeconds(10)));
        try
        {
            var busy = new Applier(r.Jobs(Actor.App), TimeSpan.FromMilliseconds(200), () => { calls++; return null; });
            ApplyResult res = busy.Save(Stock(), null);
            T.True("busy", !res.Success && res.Kind == ApplyFailure.Busy, res.Message);
            T.Eq("the guard was not evaluated outside the lock", 0, calls);
            T.Eq("nothing written", 0, r.Gw.Writes.Count);
        }
        finally { release.Set(); holder.Join(5000); }

        T.True("no guard: the app's own path is unchanged", r.Applier(Actor.App).Save(Stock(), null).Success);
        T.True("an Applier built the old way (two arguments) still works", new Applier(r.Jobs(Actor.Cli), TimeSpan.FromSeconds(5)).Save(Stock("-u z.py"), StockName).Success);
    }

    // ---- [CP-R6] the per-job lock in ManagedJobs and the Applier ----

    public static void AnAgentPlanNeverTouchesALockedJob()
    {
        var r = new Rig();
        Seed(r, locked: true);
        string xml = r.Gw.XmlOf(StockName)!;
        ManagedJobs agent = r.Jobs(Actor.Agent("Claude Code"));

        PlanRefusedException? save = null, delete = null;
        try { agent.PlanSave(Stock("-u b.py"), StockName); } catch (PlanRefusedException ex) { save = ex; }
        try { agent.PlanDelete(StockName); } catch (PlanRefusedException ex) { delete = ex; }
        T.True("PlanSave of a locked job is refused as Locked", save is { Kind: PlanRefusalKind.Locked });
        T.True("PlanDelete of a locked job is refused as Locked", delete is { Kind: PlanRefusalKind.Locked });
        T.Eq("the text", ManagedJobs.AgentLockedMessage, save!.Message);
        T.Eq("the text is the plan's sentence", "The owner locked this job against changes by AI agents. Tell the user; only they can unlock it in Cadence.", ManagedJobs.AgentLockedMessage);

        // Through the Applier convenience entry points, with the new failure kind.
        ApplyResult s = r.Applier(Actor.Agent("Claude Code")).Save(Stock("-u b.py"), StockName);
        ApplyResult d = r.Applier(Actor.Agent("Claude Code")).Delete(StockName);
        T.True("Applier.Save: Locked", !s.Success && s.Kind == ApplyFailure.Locked, s.Message);
        T.True("Applier.Delete: Locked", !d.Success && d.Kind == ApplyFailure.Locked, d.Message);
        T.True("Applier.Reapply by an agent: Locked", r.Applier(Actor.Agent("Claude Code")).Reapply(StockName) is { Success: false, Kind: ApplyFailure.Locked });
        T.True("Applier.Untrack by an agent: Locked", r.Applier(Actor.Agent("Claude Code")).Untrack(StockName) is { Success: false, Kind: ApplyFailure.Locked });
        T.Eq("nothing written but the owner's save", 1, r.Gw.Writes.Count);
        T.Eq("byte-identical", xml, r.Gw.XmlOf(StockName));

        // The owner's side is unaffected: the app and the command line save, rename and delete a locked job.
        T.True("an app save of a locked job works", r.Applier(Actor.App).Save(Stock("-u c.py", locked: true), StockName).Success);
        T.True("a command-line save works", r.Applier(Actor.Cli).Save(Stock("-u d.py", locked: true), StockName).Success);
        T.True("a save with no actor (a reader's ManagedJobs) works", r.Applier(null).Save(Stock("-u e.py", locked: true), StockName).Success);
        T.True("the lock survived all of them", r.Live().Job!.AgentLock);
        T.True("the app can unlock it", r.Applier(Actor.App).Save(Stock("-u f.py", locked: false), StockName).Success && !r.Live().Job!.AgentLock);
        T.True("and an agent can then save", r.Applier(Actor.Agent("Claude Code")).Save(Stock("-u g.py"), StockName).Success);
        T.True("an app delete of a locked job works", r.Applier(Actor.App).Save(Stock("-u h.py", locked: true), StockName).Success && r.Applier(Actor.App).Delete(StockName).Success && r.Gw.XmlOf(StockName) is null);
    }

    public static void AnAgentSaveCopiesTheLiveLockAndCanNeitherSetNorClearIt()
    {
        var r = new Rig();
        Seed(r);
        ManagedJobs agent = r.Jobs(Actor.Agent("Claude Code"));
        PlannedChange sets = agent.PlanSave(Stock("-u b.py", locked: true), StockName);
        T.True("an agent that sends AgentLock=true does not set it", !Meta(sets.NewXml!).Job.AgentLock);
        T.True("applying that plan leaves the job unlocked", r.Applier(Actor.Agent("Claude Code")).Apply(sets).Success && !r.Live().Job!.AgentLock);

        // A brand new job created by an agent is never locked, whatever the agent sent.
        var created = agent.PlanSave(Job("New one", "newjob01", "0 9 * * *") with { AgentLock = true }, null);
        T.True("a new job from an agent is unlocked", !Meta(created.NewXml!).Job.AgentLock);
        // The app and the command line set it as before.
        T.True("an app plan with AgentLock=true sets it", Meta(r.Jobs(Actor.App).PlanSave(Stock("-u c.py", locked: true), StockName).NewXml!).Job.AgentLock);
        T.True("a command-line plan with AgentLock=true sets it", Meta(r.Jobs(Actor.Cli).PlanSave(Stock("-u c.py", locked: true), StockName).NewXml!).Job.AgentLock);
        T.True("an app-made new job can be locked from the start", Meta(r.Jobs(Actor.App).PlanSave(Job("Locked new", "newjob02", "0 9 * * *") with { AgentLock = true }, null).NewXml!).Job.AgentLock);
    }

    public static void ALockSetBetweenPlanAndApplyIsRefusedAndNothingIsWritten()
    {
        var r = new Rig();
        Seed(r);
        ManagedJobs agent = r.Jobs(Actor.Agent("Claude Code"));
        PlannedChange savePlan = agent.PlanSave(Stock("-u b.py"), StockName);
        PlannedChange deletePlan = agent.PlanDelete(StockName);
        T.True("fixture: both plans exist while the job is unlocked", savePlan.NewXml is not null && deletePlan.Kind == ChangeKind.Delete);

        T.True("the owner locks the job", r.Applier(Actor.App).Save(Stock(locked: true), StockName).Success);
        string xml = r.Gw.XmlOf(StockName)!;
        int writes = r.Gw.Writes.Count;

        ApplyResult s = r.Applier(Actor.Agent("Claude Code")).Apply(savePlan);
        ApplyResult d = r.Applier(Actor.Agent("Claude Code")).Apply(deletePlan);
        T.True("the stale save plan is refused as Locked", !s.Success && s.Kind == ApplyFailure.Locked, s.Message);
        T.True("the stale delete plan is refused as Locked", !d.Success && d.Kind == ApplyFailure.Locked, d.Message);
        T.Eq("nothing written", writes, r.Gw.Writes.Count);
        T.Eq("byte-identical", xml, r.Gw.XmlOf(StockName));

        // The same plan applied by the app (the owner) goes through the normal revision rules, not the lock.
        ApplyResult app = r.Applier(Actor.App).Apply(savePlan);
        T.True("an app writer is not stopped by the lock (the revision decides)", app.Kind != ApplyFailure.Locked);
    }

    public static void RenamingAnUnlockedJobAsAnAgentStillWorks()
    {
        var r = new Rig();
        Seed(r);
        ApplyResult res = r.Applier(Actor.Agent("Claude Code")).Save(Stock("-u b.py") with { Name = "Stock ingest renamed" }, StockName);
        T.True("rename: " + res.Message, res.Success);
        T.True("the old task is gone and the new one is in sync", r.Gw.XmlOf(StockName) is null && r.Live("Stock ingest renamed").Sync == SyncStatus.InSync);
    }

    // ---- [CP-R7] finishing a write in progress on stdin EOF ----

    sealed class WriteHost : IToolHost, IWriteAwareToolHost
    {
        public readonly ManualResetEventSlim WriteStarted = new(false);
        public readonly ManualResetEventSlim WriteGate = new(false);
        public readonly ConcurrentQueue<string> Calls = new();
        public int Finished;

        public IReadOnlyList<ToolDefinition> Tools { get; } =
        [
            new("read", "A read.", "{\"type\":\"object\"}", new ToolAnnotations("Read", true, false, true)),
            new("write", "A write that blocks until released.", "{\"type\":\"object\"}", new ToolAnnotations("Write", false, true, true)),
        ];

        public bool Has(string name) => Tools.Any(t => t.Name == name);

        public bool IsWriteTool(string name) => name == "write";

        public ToolResult Call(string name, JsonElement arguments, CallContext context)
        {
            Calls.Enqueue(name);
            if (name == "write")
            {
                WriteStarted.Set();
                WriteGate.Wait(TimeSpan.FromSeconds(20));
                Interlocked.Increment(ref Finished);
                return new ToolResult(false, "written", new JsonObject { ["done"] = true });
            }
            return new ToolResult(false, "read", new JsonObject { ["done"] = true });
        }
    }

    static string Call(int id, string tool) => "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool + "\",\"arguments\":{}}}";

    static Session Start(WriteHost host, int drainMs, int writeDrainMs)
    {
        var s = new Session(host: host, drainLimit: TimeSpan.FromMilliseconds(drainMs), writeDrainLimit: TimeSpan.FromMilliseconds(writeDrainMs));
        s.Initialize();
        return s;
    }

    public static void EofWaitsForAWriteInProgressAndDropsQueuedWrites()
    {
        var host = new WriteHost();
        Session s = Start(host, drainMs: 300, writeDrainMs: 15000);
        s.Input.Line(Call(1, "write"));
        T.True("the write started", host.WriteStarted.Wait(TimeSpan.FromSeconds(10)));
        s.Input.Line(Call(2, "write"));    // queued behind it
        s.Input.Line(Call(3, "read"));     // queued behind it
        // Make sure both are read by the server before stdin closes.
        Thread.Sleep(150);
        s.Input.Complete();

        T.Eq("the server does NOT exit while the write is running, even after the 10 s limit of reads (300 ms here)", -999, s.WaitExit(1200));
        host.WriteGate.Set();
        T.Eq("it exits 0 once the write finished", 0, s.WaitExit(10000));
        T.Eq("the running write completed", 1, host.Finished);
        List<JsonElement> replies = s.Replies();
        T.True("the running write was answered", replies.Any(x => x.TryGetProperty("id", out JsonElement i) && i.ValueKind == JsonValueKind.Number && i.GetInt64() == 1 && x.TryGetProperty("result", out _)));
        T.True("the queued write was dropped without a response", !replies.Any(x => x.TryGetProperty("id", out JsonElement i) && i.ValueKind == JsonValueKind.Number && i.GetInt64() == 2));
        T.True("the queued read behind the long write was dropped when the drain time ran out", !replies.Any(x => x.TryGetProperty("id", out JsonElement i) && i.ValueKind == JsonValueKind.Number && i.GetInt64() == 3));
        T.Eq("the queued write never ran", 1, host.Calls.Count(c => c == "write"));
    }

    public static void QueuedWritesAreDroppedButQueuedReadsStillAnswerWhenTheDrainIsShortEnough()
    {
        var host = new WriteHost();
        Session s = Start(host, drainMs: 10000, writeDrainMs: 15000);
        s.Input.Line(Call(1, "write"));
        T.True("the write started", host.WriteStarted.Wait(TimeSpan.FromSeconds(10)));
        s.Input.Line(Call(2, "write"));
        s.Input.Line(Call(3, "read"));
        Thread.Sleep(150);
        s.Input.Complete();
        Thread.Sleep(100);
        host.WriteGate.Set();
        T.Eq("exit 0", 0, s.WaitExit(10000));
        List<JsonElement> replies = s.Replies();
        bool Has(long id) => replies.Any(x => x.TryGetProperty("id", out JsonElement i) && i.ValueKind == JsonValueKind.Number && i.GetInt64() == id);
        T.True("the running write is answered", Has(1));
        T.True("the queued write is dropped", !Has(2));
        T.True("the queued read is still answered (a piped batch of reads keeps working)", Has(3));
        T.Eq("exactly one write ran", 1, host.Calls.Count(c => c == "write"));
    }

    public static void AWriteThatNeverFinishesIsWaitedForOnlyUpToTheBound()
    {
        var host = new WriteHost();
        Session s = Start(host, drainMs: 200, writeDrainMs: 600);
        s.Input.Line(Call(1, "write"));
        T.True("the write started", host.WriteStarted.Wait(TimeSpan.FromSeconds(10)));
        s.Input.Complete();
        DateTime t0 = DateTime.UtcNow;
        int rc = s.WaitExit(10000);
        TimeSpan took = DateTime.UtcNow - t0;
        T.Eq("exit 0 at the bound", 0, rc);
        T.True("after waiting past the read drain limit (" + (int)took.TotalMilliseconds + " ms)", took >= TimeSpan.FromMilliseconds(500));
        host.WriteGate.Set();
    }

    public static void ReadsOnlyKeepTheOldDrainBehaviour()
    {
        var host = new WriteHost();
        Session s = Start(host, drainMs: 10000, writeDrainMs: 15000);
        for (int i = 1; i <= 5; i++) s.Input.Line(Call(i, "read"));
        T.Eq("exit 0", 0, s.Finish());
        T.Eq("all five reads were answered", 5, s.Replies().Count(x => x.TryGetProperty("result", out JsonElement r) && r.TryGetProperty("content", out _)));
    }
}
