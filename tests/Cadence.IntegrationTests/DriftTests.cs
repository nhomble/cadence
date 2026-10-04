using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.Core.TaskXml;
using Cadence.Scheduler;
using Cadence.Scheduler.Apply;
using Cadence.TestHarness;

namespace Cadence.IntegrationTests;

/// <summary>PLAN 6.3 #6: drift detected after an object-model edit, Re-apply, Untrack (the task keeps running). Real Task Scheduler, \CadenceTest only.</summary>
public static class DriftTests
{
    static string Describe(ClassifiedTask c) => string.Join(" | ", c.DriftDiffs.Select(d => d.Describe()));

    public static void ObjectModelEditIsDriftedThenReapplied() => ApplyFx.Isolated(() =>
    {
        var (gw, jobs, applier) = ApplyFx.Stack();
        const string name = "drift one";
        JobDefinition job = Fx.Job(name, ["0 18 * * *"]);
        ApplyResult created = applier.Save(job, null);
        T.True("created: " + created.Message, created.Success);
        T.Eq("fresh task is InSync", SyncStatus.InSync, jobs.Reclassify(name)!.Sync);

        // "OK without changes" in the MMC re-registers the same definition: it must stay InSync.
        ApplyFx.RawEdit(name, _ => { });
        ClassifiedTask same = jobs.Reclassify(name)!;
        T.True("an unchanged re-registration stays InSync: " + same.Problem + " " + Describe(same), same.Sync == SyncStatus.InSync);

        // Edit the time through the object model.
        ApplyFx.MoveFirstTrigger(name, 19);
        ClassifiedTask drifted = jobs.Reclassify(name)!;
        T.Eq("edited task is Managed", Ownership.Managed, drifted.Ownership);
        T.Eq("edited task is Drifted", SyncStatus.Drifted, drifted.Sync);
        T.Eq("the diff", "Time 18:00 → 19:00", Describe(drifted));
        T.Eq("the problem text", "Changed in Task Scheduler: Time 18:00 → 19:00", drifted.Problem);

        // Re-apply discards the edit.
        PlannedChange plan = jobs.PlanReapply(name);
        T.Eq("planned as Reapply", ChangeKind.Reapply, plan.Kind);
        T.True("the plan names what is discarded", plan.Summary.Contains("time 18:00 → 19:00", StringComparison.Ordinal), plan.Summary);
        ApplyResult reapplied = applier.Apply(plan);
        T.True("re-apply: " + reapplied.Message, reapplied.Success);
        ClassifiedTask back = jobs.Reclassify(name)!;
        T.Eq("re-apply -> InSync", SyncStatus.InSync, back.Sync);
        T.Eq("trigger is back at 18:00", "18:00", TaskXmlReader.Read(gw.Get(name)!.Xml).Triggers[0].StartBoundary.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture));
        T.Eq("job id kept", job.Id, back.Job!.Id);
    });

    public static void SettingsAndEnabledDrift() => ApplyFx.Isolated(() =>
    {
        var (gw, jobs, applier) = ApplyFx.Stack();
        const string name = "drift settings";
        T.True("created", applier.Save(Fx.Job(name, ["30 7 * * 1-5"]) with { Enabled = true }, null).Success);

        ApplyFx.RawEdit(name, def =>
        {
            def.Settings.Priority = 4;
            def.Settings.Enabled = false;
        });
        ClassifiedTask c = jobs.Reclassify(name)!;
        T.Eq("Drifted", SyncStatus.Drifted, c.Sync);
        string[] diffs = c.DriftDiffs.Select(d => d.Describe()).Order(StringComparer.Ordinal).ToArray();
        T.Eq("diffs", "Disabled|Setting: Priority 7 → 4", string.Join("|", diffs));

        ApplyFx.RawEdit(name, def => { def.Settings.Priority = 7; def.Settings.Enabled = true; });
        ClassifiedTask healed = jobs.Reclassify(name)!;
        T.True("editing it back heals the drift: " + Describe(healed), healed.Sync == SyncStatus.InSync);

        // Days of the week through the object model: Mon-Fri -> Mon-Sat.
        ApplyFx.RawEdit(name, def => def.Triggers.Item(1).DaysOfWeek = 2 | 4 | 8 | 16 | 32 | 64);
        ClassifiedTask days = jobs.Reclassify(name)!;
        T.Eq("days: diff", "Days Mon-Fri → Mon-Sat", Describe(days));
        T.True("days: re-apply", applier.Reapply(name).Success);
        T.Eq("days: InSync", SyncStatus.InSync, jobs.Reclassify(name)!.Sync);
        T.True("task still there", gw.Get(name) is not null);
    });

    public static void ActionReplacedIsDriftedWithProgram() => ApplyFx.Isolated(() =>
    {
        var (_, jobs, applier) = ApplyFx.Stack();
        const string name = "drift action";
        T.True("created", applier.Save(Fx.Job(name, ["0 18 * * *"]), null).Success);

        ApplyFx.RawEdit(name, def =>
        {
            dynamic action = def.Actions.Item(1);
            action.Path = "cmd.exe";
            action.Arguments = "/c exit 0";
        });
        ClassifiedTask c = jobs.Reclassify(name)!;
        T.Eq("Drifted", SyncStatus.Drifted, c.Sync);
        T.True("a Program diff: " + Describe(c), c.DriftDiffs.Any(d => d.Field == "Program"));
        T.True("re-apply: ", applier.Reapply(name).Success);
        T.Eq("InSync after re-apply", SyncStatus.InSync, jobs.Reclassify(name)!.Sync);
    });

    public static void ACopyUnderAnotherNameIsACopyUntilTheOriginalIsGone() => ApplyFx.Isolated(() =>
    {
        var (gw, jobs, applier) = ApplyFx.Stack();
        const string original = "drift original";
        const string copy = "drift copy";
        T.True("created", applier.Save(Fx.Job(original, ["0 18 * * *"]), null).Success);

        // Export/import: same XML, other name. Its wrapper still points at the original, and both carry one job id (PLAN risk R21).
        gw.Register(copy, gw.Get(original)!.Xml);
        ClassifiedTask c = jobs.Reclassify(copy)!;
        T.Eq("copy is Managed", Ownership.Managed, c.Ownership);
        T.Eq("copy is Drifted", SyncStatus.Drifted, c.Sync);
        T.Eq("copy names its original", original, c.DuplicateOf);
        T.True("problem says it is a copy: " + c.Problem, c.Problem == "This is a copy of 'drift original' made outside Cadence; delete it, or stop managing it");
        T.Eq("the original is still InSync", SyncStatus.InSync, jobs.Reclassify(original)!.Sync);
        T.True("the listing agrees", jobs.Load(false).Single(t => t.Observed.Name == copy).DuplicateOf == original);

        // Two tasks must never share one run history: the copy can be neither re-applied nor saved.
        ApplyResult refused = applier.Reapply(copy);
        T.True("re-apply of the copy is refused: " + refused.Message, !refused.Success && refused.Message.Contains("copy", StringComparison.Ordinal));
        T.True("save over the copy is refused", !applier.Save(Fx.Job(original, ["0 19 * * *"]), copy).Success);
        T.True("the copy's wrapper still points at the original (nothing was written)", gw.Get(copy)!.Xml.Contains("$tp='\\CadenceTest\\" + original + "'", StringComparison.Ordinal));

        // Once the original is gone the survivor is only a renamed task, and Re-apply registers it under its own name.
        T.True("delete the original", applier.Delete(original).Success);
        ClassifiedTask alone = jobs.Reclassify(copy)!;
        T.True("the survivor is no copy, but renamed", alone.DuplicateOf is null && alone.Sync == SyncStatus.Drifted && alone.Problem!.Contains("Renamed or moved", StringComparison.Ordinal), alone.Problem);
        ApplyResult r = applier.Reapply(copy);
        T.True("re-apply registers under the current name: " + r.Message, r.Success);
        T.Eq("copy InSync", SyncStatus.InSync, jobs.Reclassify(copy)!.Sync);
        T.True("copy's wrapper names itself", gw.Get(copy)!.Xml.Contains("$tp='\\CadenceTest\\" + copy + "'", StringComparison.Ordinal));
    });

    public static void UntrackKeepsTheTaskRunningWithoutTheWrapper() => ApplyFx.Isolated(() =>
    {
        var (gw, jobs, applier) = ApplyFx.Stack();
        const string name = "untrack run";
        Directory.CreateDirectory(ApplyFx.LocalDataDir);
        string marker = System.IO.Path.Combine(ApplyFx.LocalDataDir, "untrack-marker.txt");
        if (File.Exists(marker)) File.Delete(marker);

        // cmd.exe writes a side-effect file; the schedule is in 2099, so only the explicit Run() below executes it.
        JobDefinition job = ApplyFx.RunnableJob(name, "cmd.exe", "/c echo untracked> \"" + marker + "\"");
        T.True("created", applier.Save(job, null).Success);
        T.Eq("InSync", SyncStatus.InSync, jobs.Reclassify(name)!.Sync);

        // A second edit (the user's own change), then Untrack.
        ApplyFx.MoveFirstTrigger(name, 20);
        T.Eq("drifted before untrack", SyncStatus.Drifted, jobs.Reclassify(name)!.Sync);

        PlannedChange plan = jobs.PlanUntrack(name);
        T.Eq("planned as Untrack", ChangeKind.Untrack, plan.Kind);
        ApplyResult r = applier.Apply(plan);
        T.True("untrack: " + r.Message, r.Success);

        RegisteredTaskInfo? after = gw.Get(name);
        T.True("the task is still there", after is not null);
        ClassifiedTask c = jobs.Reclassify(name)!;
        T.Eq("it is Foreign now", Ownership.Foreign, c.Ownership);
        TaskDefinitionModel model = TaskXmlReader.Read(after!.Xml);
        T.Eq("no Documentation", true, string.IsNullOrEmpty(model.RegistrationInfo.Documentation));
        T.Eq("no Source", true, string.IsNullOrEmpty(model.RegistrationInfo.Source));
        T.Eq("direct program", "cmd.exe", model.ExecActions.Single().Command);
        T.True("direct arguments", model.ExecActions.Single().Arguments?.Contains("untrack-marker.txt", StringComparison.Ordinal) == true);
        T.True("no wrapper left", !after.Xml.Contains("$tp=", StringComparison.Ordinal));
        T.Eq("the user's trigger edit (20:00) is kept", "20:00", model.Triggers[0].StartBoundary.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture));
        T.True("description says it is no longer managed", model.RegistrationInfo.Description?.EndsWith("(no longer managed by Cadence)", StringComparison.Ordinal) == true, model.RegistrationInfo.Description);

        // Run() executes the program directly.
        gw.Run(name);
        T.True("Run() executed the program (side-effect file)", Fx.WaitFor(() => File.Exists(marker), TimeSpan.FromSeconds(30)));
        T.Eq("the file holds the echoed text", "untracked", File.Exists(marker) ? File.ReadAllText(marker).Trim() : "(missing)");

        // Foreign now: every further operation is refused.
        T.True("second untrack refused", !applier.Untrack(name).Success);
        T.True("re-apply refused", !applier.Reapply(name).Success);
        T.True("delete refused", !applier.Delete(name).Success);
        T.True("save refused", !applier.Save(job, name).Success);
        T.True("still there", gw.Get(name) is not null);
    });

    public static void UntrackOfAnInSyncJobAndOfAVisibleJob() => ApplyFx.Isolated(() =>
    {
        var (gw, jobs, applier) = ApplyFx.Stack();
        const string name = "untrack sync";
        JobDefinition job = ApplyFx.RunnableJob(name, "cmd.exe", "/c exit 0") with { Run = new RunSpec { Exe = "cmd.exe", Args = "/c exit 0", Hidden = false, WorkingDirectory = Environment.SystemDirectory } };
        T.True("created", applier.Save(job, null).Success);
        T.Eq("InSync", SyncStatus.InSync, jobs.Reclassify(name)!.Sync);

        ApplyResult r = applier.Untrack(name);
        T.True("untrack: " + r.Message, r.Success);
        ClassifiedTask c = jobs.Reclassify(name)!;
        T.Eq("Foreign", Ownership.Foreign, c.Ownership);
        ExecActionModel action = c.Actual!.ExecActions.Single();
        T.Eq("program", "cmd.exe", action.Command);
        T.Eq("working directory kept", Environment.SystemDirectory, action.WorkingDirectory);
        T.True("task exists", gw.Get(name) is not null);
    });
}
