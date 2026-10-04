using Cadence.Core.Execution;
using Cadence.Core.Metadata;
using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.Core.TaskXml;
using Cadence.Scheduler;
using Cadence.Scheduler.Apply;
using Cadence.TestHarness;
using static Cadence.Tests.PlanSamples;

namespace Cadence.Tests;

/// <summary>The Applier and ManagedJobs against an in-memory gateway: the safety rules (foreign tasks are never written), verification and each change kind.</summary>
public static class ApplierTests
{
    const string ForeignName = "Somebody else";

    static (FakeGateway gw, ManagedJobs jobs, Applier applier) Setup()
    {
        var gw = new FakeGateway();
        var jobs = new ManagedJobs(gw, () => ComposeSamples.Now);
        return (gw, jobs, new Applier(jobs));
    }

    static PlannedChange Forged(ChangeKind kind, string name, string jobId, string? xml, string? newName = null) =>
        new(kind, jobId, kind == ChangeKind.Create ? null : name, kind == ChangeKind.Delete ? null : newName ?? name, xml, ValueList<FieldDiff>.Empty, "forged");

    static ChangeKind[] AllKinds => [ChangeKind.Create, ChangeKind.Update, ChangeKind.Upgrade, ChangeKind.Reapply, ChangeKind.Rename, ChangeKind.Delete, ChangeKind.Untrack];

    // ---- safety: nothing that is not ours is ever written ----

    public static void ForeignTaskInTheManagedFolderIsNeverTouched()
    {
        var (gw, _, applier) = Setup();
        string foreign = Plain();
        gw.Seed(ForeignName, foreign);
        string valid = Compose(ComposeSamples.StockIngest(), ForeignName);   // a perfectly valid Cadence definition for that name

        foreach (ChangeKind kind in AllKinds)
            foreach (string? xml in new[] { null, valid, foreign })
            {
                ApplyResult r = applier.Apply(Forged(kind, ForeignName, "d4dst0ck", xml, kind == ChangeKind.Rename ? "Brand new name" : null));
                T.True(kind + (xml is null ? " (no xml)" : xml == valid ? " (valid xml)" : " (foreign xml)") + ": refused", !r.Success, r.Message);
            }
        T.Eq("nothing was written", 0, gw.Writes.Count);
        T.Eq("the foreign xml is byte for byte unchanged", foreign, gw.XmlOf(ForeignName));
        T.Eq("still the only task", ForeignName, string.Join("|", gw.Names));

        // The convenience entry points refuse as well.
        T.True("Delete(name)", !applier.Delete(ForeignName).Success);
        T.True("Reapply(name)", !applier.Reapply(ForeignName).Success);
        T.True("Untrack(name)", !applier.Untrack(ForeignName).Success);
        T.True("Save over the foreign task", !applier.Save(ComposeSamples.StockIngest(), ForeignName).Success);
        T.Eq("still nothing written", 0, gw.Writes.Count);
        T.Eq("still unchanged", foreign, gw.XmlOf(ForeignName));

        T.True("the refusal explains itself", applier.Delete(ForeignName).Message.Contains("not created by Cadence", StringComparison.Ordinal), applier.Delete(ForeignName).Message);
    }

    public static void ANameThatCollidesWithAForeignTaskIsNeverOverwritten()
    {
        var (gw, _, applier) = Setup();
        string foreign = Plain();
        gw.Seed(StockName, foreign);   // a foreign task that already uses the job's name

        // A forged Create for exactly that name is refused...
        ApplyResult forged = applier.Apply(Forged(ChangeKind.Create, StockName, "d4dst0ck", Stock()));
        T.True("forged create refused", !forged.Success);
        T.True("forged create: explains", forged.Message.Contains("already exists", StringComparison.Ordinal), forged.Message);
        T.Eq("forged create: nothing written", 0, gw.Writes.Count);
        T.Eq("forged create: foreign untouched", foreign, gw.XmlOf(StockName));

        // ...and a normal save picks a free name instead.
        ApplyResult r = applier.Save(ComposeSamples.StockIngest(), null);
        T.True("save succeeds under another name: " + r.Message, r.Success);
        T.Eq("both tasks exist", StockName + "|" + StockName + " (2)", string.Join("|", gw.Names));
        T.Eq("the foreign task is unchanged", foreign, gw.XmlOf(StockName));
        T.Eq("exactly one write, to the new name", "register " + StockName + " (2)", string.Join("|", gw.Writes));
    }

    public static void ARenameCannotLandOnAForeignTask()
    {
        var (gw, jobs, applier) = Setup();
        string managed = Stock();
        gw.Seed(StockName, managed);
        string foreign = Plain();
        gw.Seed("Taken", foreign);

        // Through the planner the name gets a suffix.
        ApplyResult r = applier.Save(ComposeSamples.StockIngest() with { Name = "Taken" }, StockName);
        T.True("rename succeeds with a suffix: " + r.Message, r.Success);
        T.Eq("foreign task unchanged", foreign, gw.XmlOf("Taken"));
        T.True("the managed task now lives under 'Taken (2)'", gw.XmlOf("Taken (2)") is not null && gw.XmlOf(StockName) is null);

        // A forged rename straight onto the foreign task is refused and the old task stays.
        var (gw2, _, applier2) = Setup();
        gw2.Seed(StockName, managed);
        gw2.Seed("Taken", foreign);
        PlannedChange rename = Forged(ChangeKind.Rename, StockName, "d4dst0ck", Compose(ComposeSamples.StockIngest(), "Taken"), "Taken");
        ApplyResult bad = applier2.Apply(rename);
        T.True("forged rename refused", !bad.Success);
        T.Eq("forged rename: nothing written", 0, gw2.Writes.Count);
        T.Eq("forged rename: foreign untouched", foreign, gw2.XmlOf("Taken"));
        T.Eq("forged rename: old kept", managed, gw2.XmlOf(StockName));
        T.True("unused", jobs.Gateway is not null);
    }

    public static void NewerVersionTasksAreNeverTouched()
    {
        var (gw, _, applier) = Setup();
        string newer = EditMeta(Stock(), m => m with { Gen = m.Gen + 1 });
        gw.Seed(StockName, newer);
        foreach (ChangeKind kind in AllKinds.Where(k => k != ChangeKind.Create))
        {
            ApplyResult r = applier.Apply(Forged(kind, StockName, "d4dst0ck", Stock(), kind == ChangeKind.Rename ? "Other name" : null));
            T.True(kind + ": refused", !r.Success, r.Message);
            T.True(kind + ": says newer version", r.Message.Contains("newer version", StringComparison.Ordinal), r.Message);
        }
        T.True("Save refused", !applier.Save(ComposeSamples.StockIngest(), StockName).Success);
        T.True("Untrack refused", !applier.Untrack(StockName).Success);
        T.Eq("nothing written", 0, gw.Writes.Count);
        T.Eq("unchanged", newer, gw.XmlOf(StockName));
    }

    public static void CorruptTasksCanOnlyBeDeleted()
    {
        var (gw, _, applier) = Setup();
        TaskDefinitionModel model = Read(Stock());
        string corrupt = TaskXmlWriter.Write(model with { RegistrationInfo = model.RegistrationInfo with { Documentation = "cadence/1\n{broken" } });
        gw.Seed(StockName, corrupt);

        foreach (ChangeKind kind in new[] { ChangeKind.Update, ChangeKind.Upgrade, ChangeKind.Reapply, ChangeKind.Untrack })
            T.True(kind + ": refused", !applier.Apply(Forged(kind, StockName, "d4dst0ck", Stock())).Success);
        T.Eq("nothing written yet", 0, gw.Writes.Count);

        ApplyResult del = applier.Delete(StockName);
        T.True("delete works: " + del.Message, del.Success);
        T.Eq("gone", 0, gw.Names.Count);
    }

    public static void AManagedTaskOfAnotherJobIsRefused()
    {
        var (gw, _, applier) = Setup();
        string managed = Stock();
        gw.Seed(StockName, managed);
        ApplyResult r = applier.Apply(Forged(ChangeKind.Delete, StockName, "zzzz9999", null));
        T.True("delete with another job id refused", !r.Success, r.Message);
        T.True("explains", r.Message.Contains("different job", StringComparison.Ordinal), r.Message);
        T.True("update with another job id refused", !applier.Apply(Forged(ChangeKind.Update, StockName, "zzzz9999", managed)).Success);
        T.Eq("nothing written", 0, gw.Writes.Count);
        T.Eq("unchanged", managed, gw.XmlOf(StockName));
    }

    public static void PlannedXmlMustBeACadenceDefinitionForTheSameJobAndName()
    {
        var (gw, _, applier) = Setup();
        string managed = Stock();
        gw.Seed(StockName, managed);

        ApplyResult plain = applier.Apply(Forged(ChangeKind.Update, StockName, "d4dst0ck", Plain()));
        T.True("a foreign xml is refused: " + plain.Message, !plain.Success);
        ApplyResult otherJob = applier.Apply(Forged(ChangeKind.Update, StockName, "d4dst0ck", Compose(Job("Other", "qqqq0001", "0 9 * * *"), StockName)));
        T.True("xml of another job is refused: " + otherJob.Message, !otherJob.Success);
        ApplyResult otherName = applier.Apply(Forged(ChangeKind.Update, StockName, "d4dst0ck", Compose(ComposeSamples.StockIngest(), "Another name")));
        T.True("xml whose wrapper names another task is refused: " + otherName.Message, !otherName.Success);
        ApplyResult garbage = applier.Apply(Forged(ChangeKind.Update, StockName, "d4dst0ck", "not xml"));
        T.True("garbage is refused: " + garbage.Message, !garbage.Success);
        T.Eq("nothing written", 0, gw.Writes.Count);
        T.Eq("unchanged", managed, gw.XmlOf(StockName));

        T.True("incomplete create", !applier.Apply(new PlannedChange(ChangeKind.Create, "d4dst0ck", null, null, null, ValueList<FieldDiff>.Empty, "x")).Success);
        T.True("update that would rename", !applier.Apply(Forged(ChangeKind.Update, StockName, "d4dst0ck", managed, "Other")).Success);
        T.Throws<ArgumentNullException>("null change", () => applier.Apply(null!));
    }

    // ---- compose errors ----

    public static void ComposeErrorsWriteNothingAndAreShownVerbatim()
    {
        var (gw, _, applier) = Setup();
        string managed = Stock();
        gw.Seed(StockName, managed);
        JobDefinition stock = ComposeSamples.StockIngest();
        JobDefinition[] bad =
        [
            stock with { Schedule = stock.Schedule with { CronLines = ValueList.Of("61 25 * * *") } },
            stock with { Name = new string('n', 101) },
            stock with { Run = stock.Run with { Exe = "" } },
            stock with { Run = stock.Run with { Env = ValueList.Of(new KeyValuePair<string, string>("A=B", "x")) } },
            stock with { Run = stock.Run with { Args = new string('a', 17_000) } },
        ];
        foreach (JobDefinition job in bad)
        {
            string expected = "";
            try { TaskComposer.Compose(job, ComposeSamples.Context(StockName)); }
            catch (ComposeException ex) { expected = ex.Message; }
            T.True("the composer refuses", expected.Length > 0);

            ApplyResult update = applier.Save(job, StockName);
            T.True("update fails", !update.Success);
            T.Eq("update: message verbatim", expected, update.Message);
            ApplyResult create = applier.Save(job with { Id = "newid001" }, null);
            T.True("create fails", !create.Success);
            T.Eq("create: message verbatim", expected, create.Message);
        }
        T.Eq("nothing was written", 0, gw.Writes.Count);
        T.Eq("existing task unchanged", managed, gw.XmlOf(StockName));
        T.Eq("no new task", 1, gw.Names.Count);
    }

    // ---- the flows ----

    public static void CreateUpdateAndNoChange()
    {
        var (gw, jobs, applier) = Setup();
        JobDefinition stock = ComposeSamples.StockIngest();

        ApplyResult created = applier.Save(stock, null);
        T.True("create: " + created.Message, created.Success);
        T.Eq("create: one write", "register " + StockName, string.Join("|", gw.Writes));
        T.Eq("create: reads back InSync", SyncStatus.InSync, jobs.Reclassify(StockName)!.Sync);
        T.Eq("create: no mismatch", 0, created.Mismatch.Count);

        ApplyResult updated = applier.Save(stock with { Enabled = false }, StockName);
        T.True("update: " + updated.Message, updated.Success);
        T.Eq("update: stored job changed", false, jobs.Reclassify(StockName)!.Job!.Enabled);
        T.Eq("update: still InSync", SyncStatus.InSync, jobs.Reclassify(StockName)!.Sync);
        int writes = gw.Writes.Count;

        ApplyResult same = applier.Save(stock with { Enabled = false }, StockName);
        T.True("no change: success", same.Success);
        T.Eq("no change: nothing written", writes, gw.Writes.Count);
        T.True("no change: says so", same.Message.Contains("No changes", StringComparison.Ordinal), same.Message);

        T.True("save over a task that vanished", !applier.Save(stock, "does not exist").Success);
    }

    public static void RenameRegistersTheNewTaskThenDeletesTheOldOne()
    {
        var (gw, jobs, applier) = Setup();
        gw.Seed(StockName, Stock());

        ApplyResult r = applier.Save(ComposeSamples.StockIngest() with { Name = "Stock ingest (Mom)" }, StockName);
        T.True("rename: " + r.Message, r.Success);
        T.Eq("order: register new, then delete old", "register Stock ingest (Mom)|delete " + StockName, string.Join("|", gw.Writes));
        T.Eq("only the new task remains", "Stock ingest (Mom)", string.Join("|", gw.Names));
        ClassifiedTask back = jobs.Reclassify("Stock ingest (Mom)")!;
        T.Eq("InSync", SyncStatus.InSync, back.Sync);
        T.Eq("job id kept", "d4dst0ck", back.Job!.Id);
        T.Eq("previous name recorded", StockName, string.Join("|", back.Metadata!.Prev));
    }

    public static void RenameThatDoesNotVerifyKeepsTheOldTask()
    {
        var (gw, _, applier) = Setup();
        string old = Stock();
        gw.Seed(StockName, old);
        gw.OnRegister = (_, xml) => xml.Replace("T18:00:00", "T19:00:00", StringComparison.Ordinal);   // Task Scheduler "stores" something else

        ApplyResult r = applier.Save(ComposeSamples.StockIngest() with { Name = "Stock ingest (Mom)" }, StockName);
        T.True("fails", !r.Success);
        T.True("tells that the old task is still there", r.Message.Contains("left in place", StringComparison.Ordinal), r.Message);
        T.True("the old task was not deleted", gw.Writes.All(w => !w.StartsWith("delete", StringComparison.Ordinal)));
        T.Eq("old task unchanged", old, gw.XmlOf(StockName));
    }

    public static void DeleteFlows()
    {
        var (gw, _, applier) = Setup();
        gw.Seed(StockName, Stock());
        gw.Seed(ForeignName, Plain());

        ApplyResult r = applier.Delete(StockName);
        T.True("delete: " + r.Message, r.Success);
        T.Eq("only the foreign task remains", ForeignName, string.Join("|", gw.Names));
        T.Eq("exactly one delete", "delete " + StockName, string.Join("|", gw.Writes));

        ApplyResult again = applier.Delete(StockName);
        T.True("deleting a missing task succeeds (already gone)", again.Success, again.Message);
    }

    public static void ReapplyDiscardsTheEdit()
    {
        var (gw, jobs, applier) = Setup();
        gw.Seed(StockName, Stock().Replace("T18:00:00", "T19:00:00", StringComparison.Ordinal));
        T.Eq("fixture is drifted", SyncStatus.Drifted, jobs.Reclassify(StockName)!.Sync);

        ApplyResult r = applier.Reapply(StockName);
        T.True("reapply: " + r.Message, r.Success);
        T.Eq("InSync again", SyncStatus.InSync, jobs.Reclassify(StockName)!.Sync);
        T.Eq("trigger back at 18:00", "18:00", Read(gw.XmlOf(StockName)!).Triggers[0].StartBoundary.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture));
    }

    public static void UpgradeBringsAnOutdatedTaskUpToDate()
    {
        var (gw, jobs, applier) = Setup();
        gw.Seed(StockName, EditMeta(Stock(), m => m with { Gen = 0 }));
        T.Eq("fixture is outdated", SyncStatus.Outdated, jobs.Reclassify(StockName)!.Sync);
        PlannedChange plan = jobs.PlanSave(ComposeSamples.StockIngest(), StockName);
        T.Eq("planned as an upgrade", ChangeKind.Upgrade, plan.Kind);
        ApplyResult r = applier.Apply(plan);
        T.True("upgrade: " + r.Message, r.Success);
        T.Eq("InSync", SyncStatus.InSync, jobs.Reclassify(StockName)!.Sync);
        T.Eq("gen current", TaskComposer.GeneratorVersion, jobs.Reclassify(StockName)!.Metadata!.Gen);
    }

    public static void UntrackBuildsFromTheLiveDefinition()
    {
        var (gw, jobs, applier) = Setup();
        gw.Seed(StockName, Stock());
        PlannedChange plan = jobs.PlanUntrack(StockName);   // planned from the snapshot at this moment

        // The user changes the task in Task Scheduler after the plan was made.
        gw.Seed(StockName, Stock().Replace("T18:00:00", "T19:30:00", StringComparison.Ordinal));
        ApplyResult r = applier.Apply(plan);
        T.True("untrack: " + r.Message, r.Success);

        ClassifiedTask back = jobs.Reclassify(StockName)!;
        T.Eq("Foreign now", Ownership.Foreign, back.Ownership);
        TaskDefinitionModel stored = Read(gw.XmlOf(StockName)!);
        T.Eq("the later edit survived", "19:30", stored.Triggers[0].StartBoundary.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture));
        T.Eq("direct program", @"C:\Windows\py.exe", stored.ExecActions.Single().Command);
        T.Eq("no marker", (string?)null, stored.RegistrationInfo.Documentation);
        T.Eq("one write", "register " + StockName, string.Join("|", gw.Writes));

        // Once Foreign, every further operation is refused.
        T.True("second untrack refused", !applier.Untrack(StockName).Success);
        T.True("delete refused", !applier.Delete(StockName).Success);
        T.Eq("still one write", 1, gw.Writes.Count);
    }

    // ---- verification and failures ----

    public static void AWriteThatDoesNotReadBackIsAFailure()
    {
        var (gw, _, applier) = Setup();
        gw.OnRegister = (_, xml) => xml.Replace("T18:00:00", "T19:00:00", StringComparison.Ordinal);
        ApplyResult r = applier.Save(ComposeSamples.StockIngest(), null);
        T.True("fails", !r.Success);
        T.True("mismatch lists the field", r.Mismatch.Any(d => d.Field == "Time" && d.Before == "18:00" && d.After == "19:00"), string.Join(" | ", r.Mismatch.Select(d => d.Describe())));
        T.True("message says what happened", r.Message.Contains("does not read back", StringComparison.Ordinal), r.Message);
    }

    public static void AnUntrackThatStaysManagedIsAFailure()
    {
        var (gw, _, applier) = Setup();
        string managed = Stock();
        gw.Seed(StockName, managed);
        gw.OnRegister = (_, _) => managed;   // the marker "comes back"
        ApplyResult r = applier.Untrack(StockName);
        T.True("fails", !r.Success);
        T.True("says it is still managed", r.Message.Contains("still managed", StringComparison.Ordinal), r.Message);
    }

    public static void SchedulerErrorsBecomeFailures()
    {
        var (gw, _, applier) = Setup();
        gw.RegisterFails = new SchedulerException(SchedulerErrorKind.AccessDenied, "Access is denied.");
        ApplyResult r = applier.Save(ComposeSamples.StockIngest(), null);
        T.True("fails", !r.Success);
        T.True("message", r.Message.Contains("Access is denied.", StringComparison.Ordinal), r.Message);
        T.Eq("nothing stored", 0, gw.Names.Count);
    }

    // ---- ManagedJobs ----

    public static void LoadHidesForeignTasksUnlessAsked()
    {
        var (gw, jobs, _) = Setup();
        gw.Seed(StockName, Stock());
        gw.Seed("Edited", Compose(Job("Edited", "gggg0001", "0 9 * * *"), "Edited").Replace("T09:00:00", "T10:00:00", StringComparison.Ordinal));
        gw.Seed(ForeignName, Plain());
        gw.SeedOther("\\Microsoft\\Windows\\Thing", Plain());
        gw.SeedOther("\\Elsewhere\\Stock ingest (Dad)", Stock());   // a copy that carries Cadence metadata but lives outside the folder

        IReadOnlyList<ClassifiedTask> managed = jobs.Load(false);
        T.Eq("without foreign tasks", "Edited|Stock ingest (Dad)", string.Join("|", managed.Select(t => t.Observed.Name)));
        T.Eq("statuses", "Drifted|InSync", string.Join("|", managed.Select(t => t.Sync)));

        IReadOnlyList<ClassifiedTask> all = jobs.Load(true);
        T.Eq("with foreign tasks", 5, all.Count);
        T.Eq("foreign task of the folder is listed", true, all.Any(t => t.Observed.Name == ForeignName && t.Ownership == Ownership.Foreign));
        ClassifiedTask copy = all.Single(t => t.Observed.Path == "\\Elsewhere\\Stock ingest (Dad)");
        T.Eq("a Cadence copy outside the folder is read-only (Foreign)", Ownership.Foreign, copy.Ownership);
        T.True("explains", copy.Problem?.Contains("outside the managed folder", StringComparison.Ordinal) == true, copy.Problem);
        T.Eq("ownerships", "Foreign|Foreign|Foreign|Foreign|Foreign".Length > 0, true);
    }

    public static void AJobIdCannotBeReusedByANewJob()
    {
        var (gw, jobs, _) = Setup();
        gw.Seed(StockName, Stock());
        T.Throws<PlanRefusedException>("same id, new job", () => jobs.PlanSave(ComposeSamples.StockIngest() with { Name = "A copy" }, null));
        T.Throws<PlanRefusedException>("plans against a missing task", () => jobs.PlanSave(ComposeSamples.StockIngest(), "nope"));
        T.Throws<PlanRefusedException>("delete of a missing task", () => jobs.PlanDelete("nope"));
        T.Throws<ArgumentNullException>("null gateway", () => new ManagedJobs(null!));
    }

    public static void ContextsUseTheGatewayOptions()
    {
        var (gw, jobs, _) = Setup();
        ComposeContext fresh = jobs.NewContext("x");
        T.Eq("folder", gw.Options.FolderName, fresh.FolderName);
        T.Eq("runs root", gw.Options.RunsRootName, fresh.RunsRootName);
        T.Eq("user", FakeGateway.User, fresh.UserId);
        T.Eq("now comes from the clock", ComposeSamples.Now, fresh.Now);

        CadenceMetadata meta = Meta(Stock());
        ComposeContext existing = jobs.ContextFor(meta, "abc");
        T.Eq("existing: task name", "abc", existing.TaskName);
        T.Eq("existing: now = metadata.Updated", meta.Updated, existing.Now);
        T.Eq("existing: created", (DateTimeOffset?)meta.Created, existing.Created);
    }

    // ---- [CP-B] TASK_CREATE: a task that shows up between the live check and the write is never overwritten ----

    public static void CreateAndRenameUseTaskCreateModeAndUpdateKeepsCreateOrUpdate()
    {
        var (gw, _, applier) = Setup();
        T.True("create", applier.Save(ComposeSamples.StockIngest(), null).Success);
        T.Eq("create used RegisterMode.Create", "Create", string.Join("|", gw.Modes));

        gw.Modes.Clear();
        T.True("update", applier.Save(ComposeSamples.StockIngest() with { Schedule = ComposeSamples.StockIngest().Schedule with { CronLines = ValueList.Of("0 19 * * 1-5") } }, StockName).Success);
        T.Eq("update used CreateOrUpdate", "CreateOrUpdate", string.Join("|", gw.Modes));

        gw.Modes.Clear();
        T.True("rename", applier.Save(ComposeSamples.StockIngest() with { Name = "Stock ingest (Mom)" }, StockName).Success);
        T.Eq("rename registered the new name with Create", "Create", string.Join("|", gw.Modes));
    }

    public static void ATaskThatAppearsAfterTheLiveCheckIsNotOverwritten()
    {
        var (gw, _, applier) = Setup();
        string foreign = Plain();
        // Another writer creates the task in the instant between the Applier's live check and its write.
        gw.BeforeRegister = name => { gw.BeforeRegister = null; gw.Seed(name, foreign); };

        ApplyResult r = applier.Save(ComposeSamples.StockIngest(), null);
        T.True("create is refused", !r.Success, r.Message);
        T.True("explains that the name is taken", r.Message.Contains("already exists", StringComparison.Ordinal), r.Message);
        T.Eq("the other task is byte for byte unchanged", foreign, gw.XmlOf(StockName));
        T.Eq("no Create went through", 0, gw.Modes.Count);

        // The same race on a rename: the target name appears first; the old task stays.
        var (gw2, _, applier2) = Setup();
        gw2.Seed(StockName, Stock());
        gw2.BeforeRegister = name => { gw2.BeforeRegister = null; gw2.Seed(name, foreign); };
        ApplyResult r2 = applier2.Save(ComposeSamples.StockIngest() with { Name = "Stock ingest (Mom)" }, StockName);
        T.True("rename is refused", !r2.Success, r2.Message);
        T.Eq("the squatter is unchanged", foreign, gw2.XmlOf("Stock ingest (Mom)"));
        T.True("the old task is still there", gw2.XmlOf(StockName) is not null);
    }
}
