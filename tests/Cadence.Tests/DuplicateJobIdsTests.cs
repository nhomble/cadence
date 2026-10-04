using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.Scheduler;
using Cadence.Scheduler.Apply;
using Cadence.TestHarness;
using static Cadence.Tests.PlanSamples;

namespace Cadence.Tests;

/// <summary>
/// PLAN risk R21 (Phase 15): two managed tasks that share one job id. The original is the one whose wrapper still names itself; every other one is
/// shown Drifted as "a copy", and a copy is never saved or re-applied (so two tasks never share one run history). Runs against the in-memory gateway.
/// </summary>
public static class DuplicateJobIdsTests
{
    const string CopyName = "Stock ingest (copy)";

    static (FakeGateway Gw, ManagedJobs Jobs, Applier Applier) Setup()
    {
        var gw = new FakeGateway();
        var jobs = new ManagedJobs(gw, () => ComposeSamples.Now);
        return (gw, jobs, new Applier(jobs));
    }

    /// <summary>A task exported and imported in Task Scheduler under another name: the same XML, its wrapper still pointing at the original.</summary>
    static (FakeGateway Gw, ManagedJobs Jobs, Applier Applier) WithCopy()
    {
        var s = Setup();
        s.Gw.Seed(StockName, Stock());
        s.Gw.Seed(CopyName, Stock());
        return s;
    }

    public static void ACopyIsMarkedAndTheOriginalIsNot()
    {
        var (_, jobs, _) = WithCopy();
        IReadOnlyList<ClassifiedTask> all = jobs.Load(false);
        T.Eq("both listed", 2, all.Count);
        ClassifiedTask original = all.Single(t => t.Observed.Name == StockName);
        ClassifiedTask copy = all.Single(t => t.Observed.Name == CopyName);

        T.Eq("original: in sync", SyncStatus.InSync, original.Sync);
        T.True("original: not a copy", original.DuplicateOf is null && original.Problem is null);
        T.Eq("copy: still Managed (Delete and Stop managing work on it)", Ownership.Managed, copy.Ownership);
        T.Eq("copy: drifted", SyncStatus.Drifted, copy.Sync);
        T.Eq("copy: names its original", StockName, copy.DuplicateOf);
        T.Eq("copy: the sentence", "This is a copy of 'Stock ingest (Dad)' made outside Cadence; delete it, or stop managing it", copy.Problem);
        T.Eq("copy: no field diffs (the banner shows the sentence)", 0, copy.DriftDiffs.Count);

        T.Eq("Reclassify(copy) agrees", StockName, jobs.Reclassify(CopyName)!.DuplicateOf);
        T.True("Reclassify(original) agrees", jobs.Reclassify(StockName)!.DuplicateOf is null);
    }

    public static void TheOlderTaskWinsEvenWhenItsNameSortsLater()
    {
        // The copy was re-applied (so its wrapper names itself) and is newer: both read InSync one by one, but only the older one is the original.
        var (gw, jobs, _) = Setup();
        gw.Seed(StockName, Stock());
        gw.Seed("A copy", Compose(ComposeSamples.StockIngest(), "A copy", ComposeSamples.Now.AddDays(1)));
        ClassifiedTask single = jobs.Classify(gw.Get("A copy")!);
        T.Eq("on its own the re-applied copy reads in sync", SyncStatus.InSync, single.Sync);

        IReadOnlyList<ClassifiedTask> all = jobs.Load(false);
        T.True("'Stock ingest (Dad)' is the original", all.Single(t => t.Observed.Name == StockName).DuplicateOf is null);
        T.Eq("'A copy' is the copy", StockName, all.Single(t => t.Observed.Name == "A copy").DuplicateOf);
        T.Eq("... and drifted", SyncStatus.Drifted, all.Single(t => t.Observed.Name == "A copy").Sync);
    }

    public static void WithTheSameAgeTheFirstNameIsTheOriginal()
    {
        var (gw, jobs, _) = Setup();
        gw.Seed("Beta", Compose(ComposeSamples.StockIngest(), "Beta"));
        gw.Seed("Alpha", Compose(ComposeSamples.StockIngest(), "Alpha"));
        IReadOnlyList<ClassifiedTask> all = jobs.Load(false);
        T.True("Alpha is the original", all.Single(t => t.Observed.Name == "Alpha").DuplicateOf is null);
        T.Eq("Beta is the copy", "Alpha", all.Single(t => t.Observed.Name == "Beta").DuplicateOf);
    }

    public static void ThreeTasksOnOneIdLeaveExactlyOneOriginal()
    {
        var (gw, jobs, _) = Setup();
        gw.Seed(StockName, Stock());
        gw.Seed("Copy 1", Stock());
        gw.Seed("Copy 2", Stock());
        IReadOnlyList<ClassifiedTask> all = jobs.Load(false);
        T.Eq("one original", 1, all.Count(t => t.DuplicateOf is null));
        T.Eq("two copies, both of the original", 2, all.Count(t => t.DuplicateOf == StockName));
    }

    public static void NoDuplicatesChangesNothing()
    {
        var (gw, jobs, _) = Setup();
        gw.Seed("One", Compose(Job("One", "aaaa0001", "0 9 * * *"), "One"));
        gw.Seed("Two", Compose(Job("Two", "aaaa0002", "0 9 * * *"), "Two"));
        IReadOnlyList<ClassifiedTask> before = jobs.ClassifyFolder(gw.ListManagedFolder());
        T.True("nothing is a copy", before.All(t => t.DuplicateOf is null && t.Sync == SyncStatus.InSync));
        IReadOnlyList<ClassifiedTask> again = DuplicateJobIds.Resolve(before);
        T.True("the very same list comes back", ReferenceEquals(before, again));
    }

    public static void ForeignCorruptAndNewerTasksAreNeverMarked()
    {
        var (gw, jobs, _) = Setup();
        gw.Seed(StockName, Stock());
        gw.Seed("Newer", EditMeta(Stock(), m => m with { Gen = m.Gen + 1 }));
        gw.Seed("Someone's", Plain());
        IReadOnlyList<ClassifiedTask> all = jobs.Load(true);
        T.True("original untouched", all.Single(t => t.Observed.Name == StockName).DuplicateOf is null);
        ClassifiedTask newer = all.Single(t => t.Observed.Name == "Newer");
        T.True("a newer-version task with the same id is not called a copy", newer.Ownership == Ownership.NewerVersion && newer.DuplicateOf is null);
        T.True("a foreign task is not either", all.Single(t => t.Observed.Name == "Someone's").DuplicateOf is null);
    }

    public static void ACopyIsNeverSavedOrReapplied()
    {
        var (gw, jobs, applier) = WithCopy();

        PlanRefusedException save = T_Throws(() => jobs.PlanSave(ComposeSamples.StockIngest(), CopyName));
        T.True("save refused: says copy", save.Message.Contains("is a copy of 'Stock ingest (Dad)'", StringComparison.Ordinal), save.Message);
        PlanRefusedException reapply = T_Throws(() => jobs.PlanReapply(CopyName));
        T.True("re-apply refused: says copy", reapply.Message.Contains("is a copy of", StringComparison.Ordinal), reapply.Message);

        T.True("Applier.Save refused", !applier.Save(ComposeSamples.StockIngest(), CopyName).Success);
        ApplyResult r = applier.Reapply(CopyName);
        T.True("Applier.Reapply refused", !r.Success && r.Message.Contains("copy", StringComparison.Ordinal), r.Message);
        T.Eq("nothing was written", 0, gw.Writes.Count);
        T.Eq("the copy's xml is unchanged", Stock(), gw.XmlOf(CopyName));
    }

    static PlanRefusedException T_Throws(Action action)
    {
        try { action(); }
        catch (PlanRefusedException ex) { return ex; }
        throw new InvalidOperationException("expected a PlanRefusedException");
    }

    public static void TheOriginalCanStillBeSavedAndReapplied()
    {
        var (gw, jobs, applier) = WithCopy();
        JobDefinition edited = ComposeSamples.StockIngest() with { Schedule = ComposeSamples.StockIngest().Schedule with { CronLines = ValueList.Of("30 18 * * 1-5") } };
        ApplyResult save = applier.Save(edited, StockName);
        T.True("save works: " + save.Message, save.Success);
        T.True("only the original was written", gw.Writes.All(w => w == "register " + StockName));
        T.Eq("the copy is untouched", Stock(), gw.XmlOf(CopyName));
        T.True("the original is in sync after the save", jobs.Reclassify(StockName)!.Sync == SyncStatus.InSync);
        T.True("the copy is still a copy", jobs.Reclassify(CopyName)!.DuplicateOf == StockName);
    }

    public static void DeleteAndStopManagingWorkOnACopyAndLeaveTheOriginalAlone()
    {
        var (gw, jobs, applier) = WithCopy();
        string original = gw.XmlOf(StockName)!;

        ApplyResult untrack = applier.Untrack(CopyName);
        T.True("stop managing the copy: " + untrack.Message, untrack.Success);
        T.Eq("the copy is now a plain task", Ownership.Foreign, jobs.Reclassify(CopyName)!.Ownership);
        T.Eq("the original is byte for byte unchanged", original, gw.XmlOf(StockName));
        T.True("and no longer a duplicate", jobs.Reclassify(StockName)!.DuplicateOf is null && jobs.Reclassify(StockName)!.Sync == SyncStatus.InSync);

        var (gw2, jobs2, applier2) = WithCopy();
        ApplyResult delete = applier2.Delete(CopyName);
        T.True("delete the copy: " + delete.Message, delete.Success);
        T.Eq("only the original is left", StockName, string.Join("|", gw2.Names));
        T.True("the original is fine", jobs2.Reclassify(StockName)!.Sync == SyncStatus.InSync);
    }

    public static void TheLastTaskOnAnIdIsNotACopyAnyMore()
    {
        var (gw, jobs, applier) = WithCopy();
        T.True("delete the original", applier.Delete(StockName).Success);
        ClassifiedTask c = jobs.Reclassify(CopyName)!;
        T.True("the survivor is no copy", c.DuplicateOf is null);
        T.Eq("it reads as renamed or moved (its wrapper still names the deleted task)", SyncStatus.Drifted, c.Sync);
        T.True("... so Re-apply works again", applier.Reapply(CopyName).Success);
        T.Eq("... and it is in sync", SyncStatus.InSync, jobs.Reclassify(CopyName)!.Sync);
        T.True("written once", gw.Writes.Count(w => w.StartsWith("register", StringComparison.Ordinal)) == 1);
    }

    public static void ADifferentUserTaskIsNeverReapplied()
    {
        var (gw, jobs, applier) = Setup();
        string other = Edit(Stock(), m => m with { Principal = m.Principal with { UserId = "S-1-5-21-1-2-3-9999" } });
        gw.Seed(StockName, other);
        ClassifiedTask c = jobs.Reclassify(StockName)!;
        T.True("drifted with a User diff", c.Sync == SyncStatus.Drifted && c.DriftDiffs.Any(d => d.Field == "User"));

        PlanRefusedException ex = T_Throws(() => jobs.PlanReapply(StockName));
        T.True("the refusal says why", ex.Message.Contains("different user", StringComparison.Ordinal) && ex.Message.Contains("run as you", StringComparison.Ordinal), ex.Message);
        T.True("Applier.Reapply refused", !applier.Reapply(StockName).Success);
        T.Eq("nothing written", 0, gw.Writes.Count);

        T.True("but Stop managing is allowed", applier.Untrack(StockName).Success);
        T.Eq("and the task is a plain one afterwards", Ownership.Foreign, jobs.Reclassify(StockName)!.Ownership);
    }

    public static void ProbeAndCliPreferTheOriginalWhenAnIdIsShared()
    {
        // The CLI looks jobs up by id; with a copy around the original (not the copy) is the one it finds.
        var (_, jobs, _) = WithCopy();
        ClassifiedTask found = jobs.Load(false).Where(c => c.Job is not null && c.Job.Id == "d4dst0ck").OrderBy(c => c.DuplicateOf is not null).First();
        T.Eq("the original", StockName, found.Observed.Name);
    }
}
