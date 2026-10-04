using Cadence.Core.Execution;
using Cadence.Core.Holidays;
using Cadence.Core.Metadata;
using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.Core.TaskXml;
using Cadence.Scheduler;
using Cadence.Scheduler.Apply;
using Cadence.TestHarness;

namespace Cadence.IntegrationTests;

/// <summary>The Applier against the real Task Scheduler: create, update, rename, delete, upgrade, with verification. \CadenceTest only.</summary>
public static class ApplyTests
{
    public static void CreateUpdateAndDelete() => ApplyFx.Isolated(() =>
    {
        var (gw, jobs, applier) = ApplyFx.Stack();
        const string name = "apply one";
        JobDefinition job = Fx.Job(name, ["0 18 * * 1-5"], skip: HolidayCalendar.Nyse);

        PlannedChange plan = jobs.PlanSave(job, null);
        T.Eq("create plan", ChangeKind.Create, plan.Kind);
        T.Eq("create summary", "Will create \\CadenceTest\\" + name, plan.Summary);
        ApplyResult created = applier.Apply(plan);
        T.True("create: " + created.Message, created.Success);
        ClassifiedTask c = jobs.Reclassify(name)!;
        T.Eq("created task is InSync", SyncStatus.InSync, c.Sync);
        T.Eq("the job round-trips through Task Scheduler", job, c.Job);
        T.True("the holiday list was stored", c.Metadata!.Skip is { } skip && skip.Dates.Count > 50);

        // Update: new time, an environment variable, no holidays.
        JobDefinition edited = job with
        {
            Schedule = job.Schedule with { CronLines = ValueList.Of("30 18 * * 1-5"), SkipHolidays = HolidayCalendar.None },
            Run = job.Run with { Env = ValueList.Of(new KeyValuePair<string, string>("A", "b")) },
        };
        PlannedChange update = jobs.PlanSave(edited, name);
        T.Eq("update plan", ChangeKind.Update, update.Kind);
        T.True("update summary names the time: " + update.Summary, update.Summary.Contains("time 18:00 → 18:30", StringComparison.Ordinal));
        ApplyResult updated = applier.Apply(update);
        T.True("update: " + updated.Message, updated.Success);
        ClassifiedTask u = jobs.Reclassify(name)!;
        T.Eq("updated task is InSync", SyncStatus.InSync, u.Sync);
        T.Eq("stored job is the edited one", edited, u.Job);
        T.Eq("holiday list dropped", true, u.Metadata!.Skip is null);
        T.Eq("trigger time stored", "18:30", TaskXmlReader.Read(gw.Get(name)!.Xml).Triggers[0].StartBoundary.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture));
        T.Eq("created timestamp kept", c.Metadata.Created, u.Metadata.Created);

        // No change: nothing is written.
        ApplyResult same = applier.Save(edited, name);
        T.True("no change: success", same.Success);
        T.True("no change: says so", same.Message.Contains("No changes", StringComparison.Ordinal), same.Message);

        ApplyResult deleted = applier.Delete(name);
        T.True("delete: " + deleted.Message, deleted.Success);
        T.True("deleted from Task Scheduler", gw.Get(name) is null);
        T.True("deleting again succeeds", applier.Delete(name).Success);
    });

    public static void RenameKeepsTheJobId() => ApplyFx.Isolated(() =>
    {
        var (gw, jobs, applier) = ApplyFx.Stack();
        JobDefinition job = Fx.Job("rename before", ["15 6 * * *"]);
        T.True("created", applier.Save(job, null).Success);
        string id = jobs.Reclassify("rename before")!.Job!.Id;

        PlannedChange plan = jobs.PlanSave(job with { Name = "rename after" }, "rename before");
        T.Eq("rename plan", ChangeKind.Rename, plan.Kind);
        T.Eq("old name", "rename before", plan.OldTaskName);
        T.Eq("new name", "rename after", plan.NewTaskName);
        ApplyResult r = applier.Apply(plan);
        T.True("rename: " + r.Message, r.Success);

        T.True("the old task is gone", gw.Get("rename before") is null);
        ClassifiedTask c = jobs.Reclassify("rename after")!;
        T.Eq("the new task is InSync", SyncStatus.InSync, c.Sync);
        T.Eq("the job id is unchanged", id, c.Job!.Id);
        T.Eq("the previous name is recorded", "rename before", string.Join("|", c.Metadata!.Prev));
        T.True("the wrapper names the new task", gw.Get("rename after")!.Xml.Contains("$tp='\\CadenceTest\\rename after'", StringComparison.Ordinal));
        T.Eq("exactly one task remains", 1, gw.ListManagedFolder().Count);

        // A rename that only changes the case of the name is the same task.
        ApplyResult cased = applier.Save(c.Job with { Name = "RENAME AFTER" }, "rename after");
        T.True("case-only change: " + cased.Message, cased.Success);
        T.Eq("still one task", 1, gw.ListManagedFolder().Count);
        T.Eq("still InSync", SyncStatus.InSync, jobs.Reclassify("rename after")!.Sync);
    });

    public static void OutdatedGenerationIsUpgradedBySaving() => ApplyFx.Isolated(() =>
    {
        var (gw, jobs, applier) = ApplyFx.Stack();
        const string name = "upgrade gen";
        JobDefinition job = Fx.Job(name, ["0 9 * * *"]);
        T.True("created", applier.Save(job, null).Success);

        ApplyFx.RegisterWithEditedMetadata(gw, name, m => m with { Gen = 0 });
        ClassifiedTask outdated = jobs.Reclassify(name)!;
        T.Eq("gen 0 -> Outdated", SyncStatus.Outdated, outdated.Sync);
        T.Eq("still Managed", Ownership.Managed, outdated.Ownership);

        PlannedChange plan = jobs.PlanSave(job, name);
        T.Eq("planned as Upgrade", ChangeKind.Upgrade, plan.Kind);
        ApplyResult r = applier.Apply(plan);
        T.True("upgrade: " + r.Message, r.Success);
        ClassifiedTask back = jobs.Reclassify(name)!;
        T.Eq("Save -> InSync", SyncStatus.InSync, back.Sync);
        T.Eq("generation is current", TaskComposer.GeneratorVersion, back.Metadata!.Gen);
        T.Eq("wrapper version is current", WrapperScript.Version, back.Metadata.Wv);
    });

    public static void ChangedHolidayRulesAreOutdated() => ApplyFx.Isolated(() =>
    {
        var (gw, jobs, applier) = ApplyFx.Stack();
        const string name = "upgrade holidays";
        JobDefinition job = Fx.Job(name, ["0 18 * * 1-5"], skip: HolidayCalendar.Nyse);
        T.True("created", applier.Save(job, null).Success);
        T.Eq("fresh: InSync", SyncStatus.InSync, jobs.Reclassify(name)!.Sync);

        // One date of the embedded list differs from the current rules: only the metadata changes, every fingerprint stays.
        ApplyFx.RegisterWithEditedMetadata(gw, name, m => m with
        {
            Skip = m.Skip! with { Dates = new ValueList<Holiday>(m.Skip!.Dates.Select((h, i) => i == 3 ? h with { Date = h.Date.AddDays(1) } : h)) },
        });
        T.Eq("edited holiday list -> Outdated", SyncStatus.Outdated, jobs.Reclassify(name)!.Sync);

        T.True("Save repairs it", applier.Save(job, name).Success);
        T.Eq("InSync after Save", SyncStatus.InSync, jobs.Reclassify(name)!.Sync);

        // The list ends within 365 days of "today": outdated, and saving renews it. Today is a parameter of the classifier (the real
        // clock is not used), so the jobs facade is given a clock near the end of the list.
        var late = new ManagedJobs(gw, () => new DateTimeOffset(2036, 6, 1, 9, 0, 0, TimeSpan.FromHours(-4)));
        T.Eq("list expires within 365 days -> Outdated", SyncStatus.Outdated, late.Reclassify(name)!.Sync);
    });

    public static void ComposeErrorsWriteNothing() => ApplyFx.Isolated(() =>
    {
        var (gw, jobs, applier) = ApplyStackWithOneTask(out JobDefinition job, out string name);
        string hash = ApplyFx.Sha(gw.Get(name)!.Xml);
        int count = gw.ListManagedFolder().Count;

        JobDefinition[] bad =
        [
            job with { Schedule = job.Schedule with { CronLines = ValueList.Of("61 25 * * *") } },
            job with { Name = new string('x', 101) },
            job with { Run = job.Run with { Exe = " " } },
        ];
        foreach (JobDefinition b in bad)
        {
            string expected = "";
            try { TaskComposer.Compose(b, Fx.Ctx(gw, name)); }
            catch (ComposeException ex) { expected = ex.Message; }
            ApplyResult update = applier.Save(b, name);
            T.True("update fails", !update.Success);
            T.Eq("update message verbatim", expected, update.Message);
            ApplyResult create = applier.Save(b with { Id = JobId.New() }, null);
            T.True("create fails", !create.Success);
            T.Eq("create message verbatim", expected, create.Message);
        }
        T.Eq("the existing task is unchanged", hash, ApplyFx.Sha(gw.Get(name)!.Xml));
        T.Eq("no task was added", count, gw.ListManagedFolder().Count);
        T.Eq("still InSync", SyncStatus.InSync, jobs.Reclassify(name)!.Sync);
    });

    static (TaskSchedulerGateway gw, ManagedJobs jobs, Applier applier) ApplyStackWithOneTask(out JobDefinition job, out string name)
    {
        var stack = ApplyFx.Stack();
        name = "one task";
        job = Fx.Job(name, ["0 9 * * *"]);
        T.True("setup: created", stack.applier.Save(job, null).Success);
        return stack;
    }

    public static void EveryScheduleKindSurvivesTheApplier() => ApplyFx.Isolated(() =>
    {
        var (gw, jobs, applier) = ApplyFx.Stack();
        (string name, string[] cron)[] table =
        [
            ("kind weekdays", ["0 18 * * 1-5"]),
            ("kind every 15 min", ["*/15 9-17 * * 1-5"]),
            ("kind two lines", ["0 7 * * *", "30 19 * * 1,3,5"]),
            ("kind monthly last day", ["0 23 L * *"]),
            ("kind last friday", ["0 17 * * 5L"]),
            ("kind logon", ["@logon"]),
            ("kind irregular minutes", ["5,20,40 */2 * * *"]),
        ];
        foreach ((string name, string[] cron) in table)
        {
            JobDefinition job = Fx.Job(name, cron);
            ApplyResult r = applier.Save(job, null);
            T.True(name + ": saved and verified: " + r.Message, r.Success);
            ClassifiedTask c = jobs.Reclassify(name)!;
            T.True(name + ": InSync " + c.Problem, c.Sync == SyncStatus.InSync);

            ApplyResult again = applier.Save(job with { Run = job.Run with { Args = "/c exit 5" } }, name);
            T.True(name + ": re-saved and verified: " + again.Message, again.Success);
        }
        T.Eq("all tasks exist", table.Length, gw.ListManagedFolder().Count);
    });

    public static void LoadListsManagedJobsWithTheirStatus() => ApplyFx.Isolated(() =>
    {
        var (gw, jobs, applier) = ApplyFx.Stack();
        T.True("a", applier.Save(Fx.Job("load a", ["0 9 * * *"]), null).Success);
        T.True("b", applier.Save(Fx.Job("load b", ["0 10 * * *"]), null).Success);
        ApplyFx.MoveFirstTrigger("load b", 11);
        gw.Register("load foreign", Fx.Xml(Fx.FarTrigger));

        IReadOnlyList<ClassifiedTask> managed = jobs.Load(false);
        T.Eq("managed only, by name", "load a|load b", string.Join("|", managed.Select(t => t.Observed.Name)));
        T.Eq("statuses", "InSync|Drifted", string.Join("|", managed.Select(t => t.Sync)));
        T.Eq("with foreign tasks the folder's foreign task is included", true, jobs.Load(true).Any(t => t.Observed.Name == "load foreign" && t.Ownership == Ownership.Foreign));
    });
}
