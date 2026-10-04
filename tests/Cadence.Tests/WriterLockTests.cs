using System.Diagnostics;
using Cadence.Core.Metadata;
using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.Scheduler;
using Cadence.Scheduler.Apply;
using Cadence.TestHarness;
using static Cadence.Tests.PlanSamples;

namespace Cadence.Tests;

/// <summary>
/// Phase 19 (PLAN 12.1.3): the cross-process writer lock and the revision check of the Applier, against an in-memory gateway whose folder name is
/// unique to each test (so the named mutex never collides with a real Cadence or with another test). One test starts a real helper process.
/// </summary>
public static class WriterLockTests
{
    static readonly DateTimeOffset T0 = ComposeSamples.Now;

    static string NewFolder() => "CadenceLockTest" + Guid.NewGuid().ToString("N")[..12];

    sealed class Rig
    {
        public readonly string Folder = NewFolder();
        public readonly FakeGateway Gw;
        public DateTimeOffset Clock = T0;

        public Rig() => Gw = new FakeGateway(Folder);

        public ManagedJobs Jobs(Actor? actor) => new(Gw, () => Clock, actor);

        public Applier Applier(Actor? actor, TimeSpan? timeout = null) => new(Jobs(actor), timeout ?? TimeSpan.FromSeconds(5));

        public ClassifiedTask Live() => Jobs(null).Reclassify(StockName)!;

        public CadenceMetadata Meta() => Live().Metadata!;
    }

    static JobDefinition Stock(string args = "-u a.py") =>
        ComposeSamples.StockIngest() with { Run = ComposeSamples.StockIngest().Run with { Args = args } };

    static ApplyResult Create(Rig r, Actor? actor, JobDefinition? job = null)
    {
        ApplyResult res = r.Applier(actor).Save(job ?? Stock(), null);
        T.True("fixture: the job is created (" + res.Message + ")", res.Success);
        return res;
    }

    // ---- attribution through the real pipeline ----

    public static void SavesRecordTheWriterAndKeepTheCreator()
    {
        var r = new Rig();
        Create(r, Actor.App);
        T.True("created by the app", r.Meta().CreatedBy == "app" && r.Meta().UpdatedBy == "app");

        r.Clock = T0.AddMinutes(10);
        ApplyResult cli = r.Applier(Actor.Cli).Save(Stock("-u b.py"), StockName);
        T.True("cli save: " + cli.Message, cli.Success);
        T.True("updatedBy cli, createdBy still app", r.Meta().CreatedBy == "app" && r.Meta().UpdatedBy == "cli");
        T.Eq("the update stamp moved", T0.AddMinutes(10), r.Meta().Updated);
        T.Eq("the created stamp did not", T0, r.Meta().Created);

        r.Clock = T0.AddMinutes(20);
        ApplyResult agent = r.Applier(Actor.Agent("Claude Code")).Save(Stock("-u c.py"), StockName);
        T.True("agent save: " + agent.Message, agent.Success);
        T.True("updatedBy the agent", r.Meta().UpdatedBy == "agent:Claude Code" && r.Meta().CreatedBy == "app");

        // An unchanged save writes nothing and keeps the stamp and the writer.
        string before = r.Gw.XmlOf(StockName)!;
        int writes = r.Gw.Writes.Count;
        r.Clock = T0.AddMinutes(30);
        ApplyResult same = r.Applier(Actor.App).Save(Stock("-u c.py"), StockName);
        T.True("no-op save succeeds: " + same.Message, same.Success);
        T.Eq("no-op save: nothing written", writes, r.Gw.Writes.Count);
        T.Eq("no-op save: the XML is byte for byte the same", before, r.Gw.XmlOf(StockName));
        T.Eq("no-op save: updated is not bumped", T0.AddMinutes(20), r.Meta().Updated);
        T.Eq("no-op save: updatedBy is not changed", "agent:Claude Code", r.Meta().UpdatedBy);

        r.Clock = T0.AddMinutes(40);
        ApplyResult drift = r.Applier(Actor.Cli).Reapply(StockName);
        T.True("a re-apply of a task that is in sync still succeeds: " + drift.Message, drift.Success);
        T.Eq("re-apply is attributed to its writer", "cli", r.Meta().UpdatedBy);
        T.Eq("re-apply keeps the creator", "app", r.Meta().CreatedBy);

        r.Clock = T0.AddMinutes(50);
        ApplyResult lockIt = r.Applier(Actor.Cli).Save(Stock("-u c.py") with { AgentLock = true }, StockName);
        T.True("lock via save: " + lockIt.Message, lockIt.Success);
        T.True("the lock is stored and the writer recorded", r.Meta().Job.AgentLock && r.Meta().UpdatedBy == "cli");
        T.Eq("still in sync after a lock change", SyncStatus.InSync, r.Live().Sync);
    }

    // ---- revision check ----

    public static void StaleRevisionIsRefusedAndNothingIsWritten()
    {
        var r = new Rig();
        Create(r, Actor.App);
        string r0 = JobRevision.Of(r.Live());

        // Someone else saves first.
        r.Clock = T0.AddMinutes(2);
        T.True("the other writer succeeds", r.Applier(Actor.Cli).Save(Stock("-u theirs.py"), StockName).Success);
        string theirXml = r.Gw.XmlOf(StockName)!;
        int writes = r.Gw.Writes.Count;

        r.Clock = T0.AddMinutes(5);
        ApplyResult stale = r.Applier(Actor.Agent("Claude Code")).Save(Stock("-u mine.py"), StockName, r0);
        T.True("stale save refused", !stale.Success);
        T.Eq("kind Stale", ApplyFailure.Stale, stale.Kind);
        T.Eq("message names who and when", "This job was changed 3 minutes ago by the command line after you read it. Read it again and re-apply your change.", stale.Message);
        T.Eq("nothing was registered", writes, r.Gw.Writes.Count);
        T.Eq("their XML is untouched", theirXml, r.Gw.XmlOf(StockName));

        // Read it again, then it goes through.
        string r1 = JobRevision.Of(r.Live());
        T.True("the revision moved", r1 != r0);
        ApplyResult ok = r.Applier(Actor.Agent("Claude Code")).Save(Stock("-u mine.py"), StockName, r1);
        T.True("a fresh revision is accepted: " + ok.Message, ok.Success);
        T.Eq("kind None on success", ApplyFailure.None, ok.Kind);
        T.Eq("their change was replaced by the agent's", "agent:Claude Code", r.Meta().UpdatedBy);
        T.True("and the revision moved again", JobRevision.Of(r.Live()) != r1);

        // No revision = no check (the CLI): last writer wins.
        T.True("without an expected revision the save is not checked", r.Applier(Actor.Cli).Save(Stock("-u cli.py"), StockName).Success);
    }

    public static void AnIdenticalSaveWithAStaleRevisionIsNotAnError()
    {
        var r = new Rig();
        Create(r, Actor.App);
        string r0 = JobRevision.Of(r.Live());
        r.Clock = T0.AddMinutes(1);
        T.True("the agent's change goes through", r.Applier(Actor.Agent("A")).Save(Stock("-u x.py"), StockName, r0).Success);
        int writes = r.Gw.Writes.Count;

        // The agent did not see the reply and retries the same save with the old revision.
        r.Clock = T0.AddMinutes(2);
        ApplyResult retry = r.Applier(Actor.Agent("A")).Save(Stock("-u x.py"), StockName, r0);
        T.True("the retry reports success (no changes), not stale: " + retry.Message, retry.Success);
        T.Eq("the retry wrote nothing", writes, r.Gw.Writes.Count);
    }

    public static void PlanTimeAndApplyTimeChecksBothExist()
    {
        var r = new Rig();
        Create(r, Actor.App);
        string r0 = JobRevision.Of(r.Live());
        ManagedJobs jobs = r.Jobs(Actor.Cli);

        PlannedChange plan = jobs.PlanSave(Stock("-u plan.py"), StockName, r0);
        T.Eq("the plan carries the expected revision", r0, plan.ExpectedRevision);
        T.True("a plan without a revision carries none", jobs.PlanSave(Stock("-u plan.py"), StockName).ExpectedRevision is null);

        // Plan-time check (early message).
        r.Clock = T0.AddMinutes(1);
        T.True("someone else saves", r.Applier(Actor.App).Save(Stock("-u other.py"), StockName).Success);
        PlanRefusedException? early = null;
        try { jobs.PlanSave(Stock("-u plan.py"), StockName, r0); }
        catch (PlanRefusedException ex) { early = ex; }
        T.True("planning with a stale revision is refused", early is not null);
        T.Eq("with the Stale kind", PlanRefusalKind.Stale, early?.Kind ?? PlanRefusalKind.Refused);
        T.True("and names the writer", early?.Message.Contains("by Cadence (the app)", StringComparison.Ordinal) == true, early?.Message);

        // Apply-time check: a plan made on a fresh revision, then another writer gets in before the Applier writes.
        string r1 = JobRevision.Of(r.Live());
        PlannedChange fresh = jobs.PlanSave(Stock("-u plan.py"), StockName, r1);
        r.Clock = T0.AddMinutes(2);
        T.True("another writer gets in between plan and apply", r.Applier(Actor.Agent("B")).Save(Stock("-u between.py"), StockName).Success);
        int writes = r.Gw.Writes.Count;
        ApplyResult late = r.Applier(Actor.Cli).Apply(fresh);
        T.True("the Applier refuses the stale plan: " + late.Message, !late.Success && late.Kind == ApplyFailure.Stale);
        T.Eq("nothing was written", writes, r.Gw.Writes.Count);

        // A rename and a delete are checked too.
        string r2 = JobRevision.Of(r.Live());
        r.Clock = T0.AddMinutes(3);
        T.True("a change lands", r.Applier(Actor.App).Save(Stock("-u again.py"), StockName).Success);
        ApplyResult rename = r.Applier(Actor.Cli).Save(Stock("-u again.py") with { Name = "Another name" }, StockName, r2);
        T.True("rename with a stale revision refused", !rename.Success && rename.Kind == ApplyFailure.Stale, rename.Message);
        ApplyResult del = r.Applier(Actor.Cli).Delete(StockName, r2);
        T.True("delete with a stale revision refused", !del.Success && del.Kind == ApplyFailure.Stale, del.Message);
        T.True("the task is still there", r.Gw.XmlOf(StockName) is not null);
        ApplyResult delOk = r.Applier(Actor.Cli).Delete(StockName, JobRevision.Of(r.Live()));
        T.True("delete with the live revision works: " + delOk.Message, delOk.Success);
        T.True("and it is gone", r.Gw.XmlOf(StockName) is null);
    }

    public static void FailureKindsAreSet()
    {
        var r = new Rig();
        Applier applier = r.Applier(Actor.Cli);

        ApplyResult missing = applier.Save(Stock(), "No such task");
        T.True("saving over a missing task: NotFound", !missing.Success && missing.Kind == ApplyFailure.NotFound, missing.Kind + " " + missing.Message);
        ApplyResult reapplyMissing = applier.Reapply("No such task");
        T.Eq("re-applying a missing task: NotFound", ApplyFailure.NotFound, reapplyMissing.Kind);

        r.Gw.Seed("Somebody else", Plain());
        ApplyResult foreign = applier.Delete("Somebody else");
        T.True("a foreign task: Refused", !foreign.Success && foreign.Kind == ApplyFailure.Refused, foreign.Kind + " " + foreign.Message);

        ApplyResult badCron = applier.Save(Stock() with { Schedule = Stock().Schedule with { CronLines = ValueList.Of("not a cron") } }, null);
        T.True("a job that can't be composed: Refused", !badCron.Success && badCron.Kind == ApplyFailure.Refused, badCron.Kind + " " + badCron.Message);

        Create(r, Actor.Cli);
        r.Gw.RegisterFails = new SchedulerException(SchedulerErrorKind.Other, "boom", unchecked((int)0x80070005));
        ApplyResult ts = applier.Save(Stock("-u z.py"), StockName);
        T.True("Task Scheduler refused: TaskScheduler", !ts.Success && ts.Kind == ApplyFailure.TaskScheduler, ts.Kind + " " + ts.Message);
        r.Gw.RegisterFails = null;

        r.Gw.OnRegister = (_, xml) => xml.Replace("-u y.py", "-u TAMPERED", StringComparison.Ordinal).Replace("<Enabled>true</Enabled>", "<Enabled>false</Enabled>", StringComparison.Ordinal);
        ApplyResult verify = applier.Save(Stock("-u y.py"), StockName);
        T.True("stored differently from what was written: Verify", !verify.Success && verify.Kind == ApplyFailure.Verify, verify.Kind + " " + verify.Message);
        r.Gw.OnRegister = null;

        T.Eq("success has no kind", ApplyFailure.None, applier.Save(Stock("-u fine.py"), StockName).Kind);
    }

    // ---- the lock itself ----

    public static void LockNameAndReentrancy()
    {
        T.Eq("name", @"Local\CadenceWrite.CadenceTest", WriterLock.MutexName("CadenceTest"));
        T.Eq("a backslash in the folder name can't break the mutex name", @"Local\CadenceWrite.a_b", WriterLock.MutexName(@"a\b"));
        T.Throws<ArgumentException>("an empty folder name is refused", () => WriterLock.MutexName(""));
        T.Eq("default timeout", TimeSpan.FromSeconds(15), WriterLock.DefaultTimeout);
        T.Eq("busy message", "Another Cadence window or agent is saving right now; try again in a moment.", WriterLock.BusyMessage);

        string folder = NewFolder();
        using WriterLock? outer = WriterLock.TryAcquire(folder, TimeSpan.FromSeconds(2));
        T.True("taken", outer is not null);
        using (WriterLock? inner = WriterLock.TryAcquire(folder, TimeSpan.Zero))
            T.True("re-entrant on the same thread (plan inside the lock, then apply)", inner is not null);
        outer?.Dispose();
        outer?.Dispose();   // twice is harmless
        T.True("released: another thread gets it at once", OnThread(() => { using WriterLock? w = WriterLock.TryAcquire(folder, TimeSpan.FromSeconds(2)); return w is not null; }));

        string other = NewFolder();
        using WriterLock? a = WriterLock.TryAcquire(folder, TimeSpan.FromSeconds(2));
        T.True("a different folder is a different lock", OnThread(() => { using WriterLock? w = WriterLock.TryAcquire(other, TimeSpan.Zero); return w is not null; }));
        a?.Dispose();
    }

    static bool OnThread(Func<bool> f)
    {
        bool result = false;
        var t = new Thread(() => result = f());
        t.Start();
        t.Join();
        return result;
    }

    public static void ABusyLockGivesAClearMessageNotAHang()
    {
        var r = new Rig();
        Create(r, Actor.App);
        string before = r.Gw.XmlOf(StockName)!;
        int writes = r.Gw.Writes.Count;

        var held = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using WriterLock? w = WriterLock.TryAcquire(r.Folder, TimeSpan.FromSeconds(5));
            if (w is not null) held.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        });
        holder.Start();
        try
        {
            T.True("the holder has the lock", held.Wait(TimeSpan.FromSeconds(10)));
            var watch = Stopwatch.StartNew();
            ApplyResult save = r.Applier(Actor.Cli, TimeSpan.FromMilliseconds(300)).Save(Stock("-u busy.py"), StockName);
            watch.Stop();
            T.True("save refused", !save.Success);
            T.Eq("kind Busy", ApplyFailure.Busy, save.Kind);
            T.Eq("message", "Another Cadence window or agent is saving right now; try again in a moment.", save.Message);
            T.True("it waited for the timeout and no longer (" + watch.ElapsedMilliseconds + " ms)", watch.ElapsedMilliseconds is >= 250 and < 5000);
            T.Eq("nothing written", writes, r.Gw.Writes.Count);
            T.Eq("task untouched", before, r.Gw.XmlOf(StockName));

            T.Eq("delete is Busy too", ApplyFailure.Busy, r.Applier(Actor.Cli, TimeSpan.FromMilliseconds(50)).Delete(StockName).Kind);
            T.Eq("re-apply is Busy too", ApplyFailure.Busy, r.Applier(Actor.Cli, TimeSpan.FromMilliseconds(50)).Reapply(StockName).Kind);
            T.Eq("untrack is Busy too", ApplyFailure.Busy, r.Applier(Actor.Cli, TimeSpan.FromMilliseconds(50)).Untrack(StockName).Kind);
            PlannedChange plan = r.Jobs(Actor.Cli).PlanSave(Stock("-u busy2.py"), StockName);
            T.Eq("a plan applied while busy is Busy", ApplyFailure.Busy, r.Applier(Actor.Cli, TimeSpan.FromMilliseconds(50)).Apply(plan).Kind);
            T.Eq("still nothing written", writes, r.Gw.Writes.Count);

            // Reading never needs the lock.
            T.True("classifying while another writer holds the lock still works", r.Live().Sync == SyncStatus.InSync);
        }
        finally
        {
            release.Set();
            holder.Join(TimeSpan.FromSeconds(10));
        }
        ApplyResult after = r.Applier(Actor.Cli).Save(Stock("-u after.py"), StockName);
        T.True("after the holder lets go the save works: " + after.Message, after.Success);
    }

    public static void ALockLeftByADeadHolderIsTakenOver()
    {
        var r = new Rig();
        Create(r, Actor.App);
        // A thread takes the mutex and ends without releasing it: the kernel marks it abandoned, exactly as when a process crashes.
        var t = new Thread(() =>
        {
            var m = new Mutex(false, WriterLock.MutexName(r.Folder));
            m.WaitOne();
            // no ReleaseMutex, no Dispose: the thread just ends
            GC.KeepAlive(m);
        });
        t.Start();
        t.Join();

        ApplyResult save = r.Applier(Actor.Cli, TimeSpan.FromSeconds(3)).Save(Stock("-u after-crash.py"), StockName);
        T.True("an abandoned lock does not block the next writer: " + save.Message, save.Success);
        T.Eq("the write happened", "cli", r.Meta().UpdatedBy);
        T.True("and the lock is usable again afterwards", r.Applier(Actor.App).Save(Stock("-u next.py"), StockName).Success);
    }

    public static void TwoThreadsWithTheSameRevisionOneWinsOneIsStale()
    {
        for (int round = 0; round < 5; round++)
        {
            var r = new Rig();
            Create(r, Actor.App);
            string rev = JobRevision.Of(r.Live());
            var results = new ApplyResult[2];
            var start = new Barrier(2);
            var threads = new Thread[2];
            for (int i = 0; i < 2; i++)
            {
                int n = i;
                threads[i] = new Thread(() =>
                {
                    start.SignalAndWait();
                    results[n] = r.Applier(n == 0 ? Actor.Cli : Actor.Agent("Race")).Save(Stock("-u racer" + n + ".py"), StockName, rev);
                });
                threads[i].Start();
            }
            foreach (Thread t in threads) t.Join();
            int ok = results.Count(x => x.Success);
            int stale = results.Count(x => !x.Success && x.Kind == ApplyFailure.Stale);
            T.True("round " + round + ": exactly one success and one Stale (" + results[0].Kind + "/" + results[1].Kind + ")", ok == 1 && stale == 1);
            T.Eq("round " + round + ": the task is in sync", SyncStatus.InSync, r.Live().Sync);
        }
    }

    // ---- a real second process ----

    static Process StartHolder(string folder)
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        // Started through the dotnet host ("dotnet Cadence.Tests.dll") or as the apphost itself, whichever ran this suite.
        if (string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add(Environment.GetCommandLineArgs()[0].EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? Environment.GetCommandLineArgs()[0] : typeof(LockHolder).Assembly.Location);
        psi.ArgumentList.Add(LockHolder.Switch);
        psi.ArgumentList.Add(folder);
        psi.Environment["DOTNET_DISABLE_GUI_ERRORS"] = "1";
        return Process.Start(psi) ?? throw new InvalidOperationException("could not start the helper process");
    }

    public static void ASecondProcessIsBlockedWhileTheFirstHoldsTheLock()
    {
        string folder = NewFolder();
        Process? p = null;
        try
        {
            p = StartHolder(folder);
            Task<string?> firstLine = Task.Run(() => p.StandardOutput.ReadLine());
            bool started = firstLine.Wait(TimeSpan.FromSeconds(60));
            T.True("the helper process reports that it holds the lock", started && firstLine.Result == "held", started ? firstLine.Result : "no answer in 60 s");
            if (!started || firstLine.Result != "held") return;

            var watch = Stopwatch.StartNew();
            using (WriterLock? blocked = WriterLock.TryAcquire(folder, TimeSpan.FromMilliseconds(400)))
                T.True("this process can't take it while the helper holds it", blocked is null);
            watch.Stop();
            T.True("and it gave up at the timeout (" + watch.ElapsedMilliseconds + " ms)", watch.ElapsedMilliseconds is >= 350 and < 10000);

            // Through the Applier: a clear message, no hang.
            var gw = new FakeGateway(folder);
            ApplyResult busy = new Applier(new ManagedJobs(gw, () => T0, Actor.Cli), TimeSpan.FromMilliseconds(300)).Save(Stock(), null);
            T.True("an Applier in this process reports Busy while the other process holds the lock", !busy.Success && busy.Kind == ApplyFailure.Busy && busy.Message == WriterLock.BusyMessage, busy.Kind + " " + busy.Message);
            T.Eq("nothing was written", 0, gw.Writes.Count);

            // Clean release: the helper's stdin closes, it disposes the lock and exits 0.
            p.StandardInput.Close();
            T.True("the helper exits after its stdin closes", p.WaitForExit(30000));
            T.Eq("helper exit code", 0, p.ExitCode);
            using WriterLock? free = WriterLock.TryAcquire(folder, TimeSpan.FromSeconds(5));
            T.True("after a clean release this process takes it", free is not null);
        }
        finally
        {
            Kill(p);
        }
    }

    public static void ALockHeldByAProcessThatIsKilledIsReleased()
    {
        string folder = NewFolder();
        Process? p = null;
        try
        {
            p = StartHolder(folder);
            Task<string?> firstLine = Task.Run(() => p.StandardOutput.ReadLine());
            bool started = firstLine.Wait(TimeSpan.FromSeconds(60));
            T.True("the helper process holds the lock", started && firstLine.Result == "held", started ? firstLine.Result : "no answer in 60 s");
            if (!started || firstLine.Result != "held") return;
            using (WriterLock? blocked = WriterLock.TryAcquire(folder, TimeSpan.FromMilliseconds(200)))
                T.True("blocked while it lives", blocked is null);

            p.Kill(entireProcessTree: true);
            T.True("the helper is gone", p.WaitForExit(30000));

            // The crash left the mutex abandoned: it counts as acquired and the apply goes through.
            var gw = new FakeGateway(folder);
            ApplyResult save = new Applier(new ManagedJobs(gw, () => T0, Actor.Cli), TimeSpan.FromSeconds(5)).Save(Stock(), null);
            T.True("a writer proceeds after the holder crashed: " + save.Message, save.Success);
            T.Eq("and wrote the job", 1, gw.Writes.Count);
        }
        finally
        {
            Kill(p);
        }
    }

    static void Kill(Process? p)
    {
        if (p is null) return;
        try
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
            p.WaitForExit(10000);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        p.Dispose();
    }
}
