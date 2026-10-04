using Cadence.Core.Execution;
using Cadence.Core.Holidays;
using Cadence.Core.Metadata;
using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.Core.TaskXml;
using Cadence.TestHarness;
using static Cadence.Tests.PlanSamples;

namespace Cadence.Tests;

/// <summary>PLAN 3.12: Planner.ForSave / ForDelete / ForReapply / ForUntrack. Pure: no Task Scheduler.</summary>
public static class PlannerTests
{
    static readonly DateTimeOffset Later = new(2026, 11, 2, 10, 30, 0, TimeSpan.FromHours(-5));

    static ComposeContext Ctx(DateTimeOffset? now = null) => ComposeSamples.Context("ignored", now ?? Later);

    static ClassifiedTask Existing(JobDefinition job, string name) => Classify(Compose(job, name), name);

    static ClassifiedTask ExistingStock() => Existing(ComposeSamples.StockIngest(), StockName);

    static ClassifiedTask Drifted(string from, string to)
    {
        string xml = Stock().Replace(from, to, StringComparison.Ordinal);
        ClassifiedTask c = Classify(xml);
        T.Eq("fixture is drifted", SyncStatus.Drifted, c.Sync);
        return c;
    }

    static ClassifiedTask Foreign() => Classify(Plain(), "Somebody else");

    static ClassifiedTask Newer() => Classify(EditMeta(Stock(), m => m with { Gen = m.Gen + 1 }));

    static ClassifiedTask Corrupt()
    {
        TaskDefinitionModel model = Read(Stock());
        return Classify(TaskXmlWriter.Write(model with { RegistrationInfo = model.RegistrationInfo with { Documentation = "cadence/1\n{broken" } }));
    }

    static string Describe(PlannedChange c) => string.Join(" | ", c.Changes.Select(d => d.Describe()));

    // ---- create ----

    public static void CreateBasics()
    {
        JobDefinition job = ComposeSamples.StockIngest();
        PlannedChange p = Planner.ForSave(job, null, Names(), Ctx());
        T.Eq("kind", ChangeKind.Create, p.Kind);
        T.Eq("job id", "d4dst0ck", p.JobId);
        T.Eq("no old name", (string?)null, p.OldTaskName);
        T.Eq("new name", StockName, p.NewTaskName);
        T.Eq("summary", "Will create \\Cadence\\Stock ingest (Dad)", p.Summary);
        T.True("xml present", p.NewXml is not null);
        T.Eq("no field changes", 0, p.Changes.Count);

        ClassifiedTask back = Classify(p.NewXml!, StockName, ComposeSamples.Anchor);
        T.Eq("the new xml reads back InSync", SyncStatus.InSync, back.Sync);
        T.Eq("created = now (a new job)", Later.ToString("yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture),
            back.Metadata!.Created.ToString("yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture));
        T.Eq("no previous names", 0, back.Metadata.Prev.Count);
    }

    public static void CreateAvoidsTakenNames()
    {
        (string display, string[] taken, string expected)[] table =
        [
            ("Stock ingest (Dad)", [], "Stock ingest (Dad)"),
            ("Stock ingest (Dad)", ["Stock ingest (Dad)"], "Stock ingest (Dad) (2)"),
            ("Stock ingest (Dad)", ["stock INGEST (dad)"], "Stock ingest (Dad) (2)"),           // case-insensitive
            ("Backup", ["Backup", "Backup (2)"], "Backup (3)"),
            ("Backup notes!", [], "Backup notes-"),
            ("   ", [], "job"),
        ];
        foreach ((string display, string[] taken, string expected) in table)
        {
            JobDefinition job = Job(display, "cccc0001", "0 9 * * *");
            PlannedChange p = Planner.ForSave(job, null, Names(taken), Ctx());
            T.Eq("name for '" + display + "' with " + taken.Length + " taken", expected, p.NewTaskName);
            T.True("xml names the task: " + expected, p.NewXml!.Contains("\\Cadence\\" + expected + "'", StringComparison.Ordinal));
        }
    }

    // ---- update ----

    public static void UpdateSummaries()
    {
        ClassifiedTask existing = ExistingStock();
        JobDefinition stock = ComposeSamples.StockIngest();
        (string label, JobDefinition edited, string expected)[] table =
        [
            ("time", stock with { Schedule = stock.Schedule with { CronLines = ValueList.Of("30 18 * * 1-5") } }, "Will update: time 18:00 \u2192 18:30"),
            ("days", stock with { Schedule = stock.Schedule with { CronLines = ValueList.Of("0 18 * * 1-6") } }, "Will update: days Mon-Fri \u2192 Mon-Sat"),
            ("disabled", stock with { Enabled = false }, "Will update: disabled"),
            ("program", stock with { Run = stock.Run with { Exe = @"C:\x\tool.exe" } }, "Will update: program C:\\Windows\\py.exe \u2192 C:\\x\\tool.exe"),
            ("arguments", stock with { Run = stock.Run with { Args = "-v" } }, "Will update: arguments -u \"C:\\Users\\Dad\\stocks\\ingest.py\" \u2192 -v"),
            ("environment", stock with { Run = stock.Run with { Env = ValueList<KeyValuePair<string, string>>.Empty } }, "Will update: environment 1 variable \u2192 0 variables"),
            ("holidays", stock with { Schedule = stock.Schedule with { SkipHolidays = HolidayCalendar.None } }, "Will update: skip holidays NYSE \u2192 none"),
            ("window", stock with { Run = stock.Run with { Hidden = false } }, "Will update: window hidden \u2192 visible"),
            ("battery", stock with { Settings = stock.Settings with { RunOnBatteries = false } }, "Will update: setting: Don't start on batteries no \u2192 yes"),
        ];
        foreach ((string label, JobDefinition edited, string expected) in table)
        {
            PlannedChange p = Planner.ForSave(edited, existing, Names(StockName), Ctx());
            T.Eq(label + ": kind", ChangeKind.Update, p.Kind);
            T.Eq(label + ": summary", expected, p.Summary);
            T.Eq(label + ": name kept", StockName, p.NewTaskName);
            T.Eq(label + ": old name", StockName, p.OldTaskName);
            T.True(label + ": xml", p.NewXml is not null);
            T.True(label + ": at least one change", p.Changes.Count > 0);
            T.Eq(label + ": the written xml is InSync", SyncStatus.InSync, Classify(p.NewXml!).Sync);
        }
    }

    public static void UpdateKeepsCreatedAndTimestamps()
    {
        ClassifiedTask existing = ExistingStock();
        JobDefinition stock = ComposeSamples.StockIngest();
        PlannedChange p = Planner.ForSave(stock with { Enabled = false }, existing, Names(StockName), Ctx());
        CadenceMetadata back = Meta(p.NewXml!);
        T.Eq("created is kept", existing.Metadata!.Created, back.Created);
        T.Eq("updated is the new now", Later.ToUniversalTime().ToString("O"), back.Updated.ToUniversalTime().ToString("O"));
        T.Eq("job id is kept", "d4dst0ck", back.Job.Id);
    }

    public static void DisplayNameOnlyEditReregistersButKeepsTheTaskName()
    {
        ClassifiedTask existing = ExistingStock();
        JobDefinition stock = ComposeSamples.StockIngest();

        // The name lives in the metadata, so the task is re-registered; it is not renamed because the sanitized name is the same.
        PlannedChange p = Planner.ForSave(stock with { Name = "Stock ingest (Dad)  " }, existing, Names(StockName), Ctx());
        T.Eq("trailing space: sanitized name unchanged -> kind", ChangeKind.Update, p.Kind);
        T.Eq("trailing space: task name kept", StockName, p.NewTaskName);
        T.True("trailing space: still written", p.NewXml is not null);
        T.Eq("trailing space: metadata has the new display name", "Stock ingest (Dad)  ", Meta(p.NewXml!).Job.Name);

        PlannedChange q = Planner.ForSave(stock with { Name = "Stock ingest (Dad)!" }, existing, Names(StockName), Ctx());
        T.Eq("'!' sanitizes to '-': that is a rename", ChangeKind.Rename, q.Kind);

        // Same sanitized form with different raw characters: "a/b" and "a\\b" both become "a-b".
        ClassifiedTask slash = Existing(Job("a/b", "dddd0001", "0 9 * * *"), "a-b");
        PlannedChange r = Planner.ForSave(Job("a\\b", "dddd0001", "0 9 * * *"), slash, Names("a-b"), Ctx());
        T.Eq("a/b -> a\\b: update (same sanitized name)", ChangeKind.Update, r.Kind);
        T.Eq("a/b -> a\\b: name kept", "a-b", r.NewTaskName);
        T.True("a/b -> a\\b: the Name field is the change", r.Changes.Any(c => c.Field == "Name"));
    }

    public static void ADuplicateSuffixIsKept()
    {
        // The task is called "Backup (2)" because "Backup" was taken when it was created. Saving it unchanged must not rename it,
        // even if "Backup" has been freed since.
        JobDefinition job = Job("Backup", "eeee0001", "0 9 * * *");
        ClassifiedTask existing = Existing(job, "Backup (2)");
        PlannedChange p = Planner.ForSave(job with { Enabled = false }, existing, Names("Backup (2)"), Ctx());
        T.Eq("kind", ChangeKind.Update, p.Kind);
        T.Eq("name kept", "Backup (2)", p.NewTaskName);

        // A real change of display name may take the plain name again.
        PlannedChange q = Planner.ForSave(job with { Name = "Backup now" }, existing, Names("Backup (2)"), Ctx());
        T.Eq("renamed to a new name", "Backup now", q.NewTaskName);
        T.Eq("rename kind", ChangeKind.Rename, q.Kind);
    }

    public static void NoChangeWritesNothing()
    {
        ClassifiedTask existing = ExistingStock();
        PlannedChange p = Planner.ForSave(ComposeSamples.StockIngest(), existing, Names(StockName), Ctx());
        T.Eq("kind", ChangeKind.Update, p.Kind);
        T.Eq("nothing to write", (string?)null, p.NewXml);
        T.Eq("summary", "No changes to save", p.Summary);
        T.Eq("no changes", 0, p.Changes.Count);
    }

    public static void UpgradeOfAnOutdatedTask()
    {
        ClassifiedTask outdated = Classify(EditMeta(Stock(), m => m with { Gen = 0 }));
        T.Eq("fixture is outdated", SyncStatus.Outdated, outdated.Sync);
        PlannedChange p = Planner.ForSave(ComposeSamples.StockIngest(), outdated, Names(StockName), Ctx());
        T.Eq("kind", ChangeKind.Upgrade, p.Kind);
        T.True("xml present", p.NewXml is not null);
        T.True("summary says up to date", p.Summary.Contains("up to date", StringComparison.Ordinal), p.Summary);
        CadenceMetadata back = Meta(p.NewXml!);
        T.Eq("gen is current", TaskComposer.GeneratorVersion, back.Gen);
        T.Eq("wv is current", WrapperScript.Version, back.Wv);
        T.Eq("the upgraded xml is InSync", SyncStatus.InSync, Classify(p.NewXml!).Sync);

        // Outdated and edited at once: an ordinary Update (which upgrades as well).
        JobDefinition stock = ComposeSamples.StockIngest();
        PlannedChange both = Planner.ForSave(stock with { Enabled = false }, outdated, Names(StockName), Ctx());
        T.Eq("outdated + edit: kind", ChangeKind.Update, both.Kind);
        T.Eq("outdated + edit: gen is current", TaskComposer.GeneratorVersion, Meta(both.NewXml!).Gen);
    }

    public static void SavingADriftedTaskOverwritesTheEdit()
    {
        ClassifiedTask drifted = Drifted("T18:00:00", "T19:00:00");
        PlannedChange p = Planner.ForSave(ComposeSamples.StockIngest(), drifted, Names(StockName), Ctx());
        T.Eq("kind", ChangeKind.Update, p.Kind);
        T.True("the overwritten edit is listed", Describe(p).Contains("Time 19:00 \u2192 18:00", StringComparison.Ordinal), Describe(p));
        T.Eq("the result is InSync", SyncStatus.InSync, Classify(p.NewXml!).Sync);
    }

    // ---- rename ----

    public static void RenameKeepsTheJobIdAndRecordsThePreviousName()
    {
        ClassifiedTask existing = ExistingStock();
        JobDefinition renamed = ComposeSamples.StockIngest() with { Name = "Stock ingest (Mom)" };
        PlannedChange p = Planner.ForSave(renamed, existing, Names(StockName), Ctx());
        T.Eq("kind", ChangeKind.Rename, p.Kind);
        T.Eq("job id kept", "d4dst0ck", p.JobId);
        T.Eq("old name", StockName, p.OldTaskName);
        T.Eq("new name", "Stock ingest (Mom)", p.NewTaskName);
        T.True("summary", p.Summary.StartsWith("Will rename to \\Cadence\\Stock ingest (Mom)", StringComparison.Ordinal), p.Summary);

        CadenceMetadata back = Meta(p.NewXml!);
        T.Eq("job id in the metadata", "d4dst0ck", back.Job.Id);
        T.Eq("previous names", StockName, string.Join("|", back.Prev));
        T.Eq("created kept", existing.Metadata!.Created, back.Created);
        T.Eq("the new xml reads back InSync under the new name", SyncStatus.InSync, Classify(p.NewXml!, "Stock ingest (Mom)").Sync);
        T.True("the wrapper names the new task", p.NewXml!.Contains("\\Cadence\\Stock ingest (Mom)'", StringComparison.Ordinal));

        // Renaming again appends.
        ClassifiedTask second = Classify(p.NewXml!, "Stock ingest (Mom)");
        PlannedChange p2 = Planner.ForSave(renamed with { Name = "Stock ingest (Pop)" }, second, Names("Stock ingest (Mom)"), Ctx());
        T.Eq("two previous names, oldest first", StockName + "|Stock ingest (Mom)", string.Join("|", Meta(p2.NewXml!).Prev));
    }

    public static void RenameAvoidsTakenNames()
    {
        ClassifiedTask existing = ExistingStock();
        JobDefinition renamed = ComposeSamples.StockIngest() with { Name = "Taken" };
        PlannedChange p = Planner.ForSave(renamed, existing, Names(StockName, "Taken", "Taken (2)"), Ctx());
        T.Eq("suffix", "Taken (3)", p.NewTaskName);
        T.Eq("kind", ChangeKind.Rename, p.Kind);

        // A rename that only changes letter case is the same task name for Task Scheduler: not a rename.
        PlannedChange c = Planner.ForSave(ComposeSamples.StockIngest() with { Name = "STOCK INGEST (DAD)" }, existing, Names(StockName), Ctx());
        T.Eq("case-only: kind", ChangeKind.Update, c.Kind);
        T.Eq("case-only: name kept", StockName, c.NewTaskName);
    }

    // ---- errors and refusals ----

    public static void ComposeExceptionsPropagateUnchanged()
    {
        ClassifiedTask existing = ExistingStock();
        JobDefinition stock = ComposeSamples.StockIngest();
        JobDefinition[] bad =
        [
            stock with { Schedule = stock.Schedule with { CronLines = ValueList.Of("61 25 * * *") } },
            stock with { Name = new string('x', 101) },
            stock with { Run = stock.Run with { Exe = "  " } },
        ];
        foreach (JobDefinition job in bad)
        {
            string expected = "";
            try { TaskComposer.Compose(job, ComposeSamples.Context(StockName)); }
            catch (ComposeException ex) { expected = ex.Message; }
            T.True("the composer refuses: " + job.Name.Length + " chars", expected.Length > 0);

            ComposeException? seen = null;
            try { Planner.ForSave(job, existing, Names(StockName), Ctx()); }
            catch (ComposeException ex) { seen = ex; }
            T.True("ForSave (update) throws ComposeException", seen is not null);
            T.Eq("update: message verbatim", expected, seen?.Message);

            ComposeException? seenNew = null;
            try { Planner.ForSave(job, null, Names(), Ctx()); }
            catch (ComposeException ex) { seenNew = ex; }
            T.True("ForSave (create) throws ComposeException", seenNew is not null);
        }
    }

    public static void JobIdCannotChange()
    {
        ClassifiedTask existing = ExistingStock();
        T.Throws<ArgumentException>("another job id", () => Planner.ForSave(ComposeSamples.StockIngest() with { Id = "zzzz9999" }, existing, Names(StockName), Ctx()));
        T.Throws<ArgumentNullException>("null job", () => Planner.ForSave(null!, existing, Names(), Ctx()));
    }

    public static void ForeignNewerAndCorruptTasksAreRefused()
    {
        JobDefinition stock = ComposeSamples.StockIngest();
        (string label, ClassifiedTask task)[] untouchable = [("foreign", Foreign()), ("newer version", Newer())];
        foreach ((string label, ClassifiedTask task) in untouchable)
        {
            T.Throws<PlanRefusedException>(label + ": save", () => Planner.ForSave(stock, task, Names(), Ctx()));
            T.Throws<PlanRefusedException>(label + ": delete", () => Planner.ForDelete(task));
            T.Throws<PlanRefusedException>(label + ": re-apply", () => Planner.ForReapply(task, Ctx()));
            T.Throws<PlanRefusedException>(label + ": untrack", () => Planner.ForUntrack(task));
        }

        ClassifiedTask corrupt = Corrupt();
        T.Eq("fixture is corrupt", Ownership.CorruptMetadata, corrupt.Ownership);
        T.Throws<PlanRefusedException>("corrupt: save", () => Planner.ForSave(stock, corrupt, Names(), Ctx()));
        T.Throws<PlanRefusedException>("corrupt: re-apply", () => Planner.ForReapply(corrupt, Ctx()));
        T.Throws<PlanRefusedException>("corrupt: untrack", () => Planner.ForUntrack(corrupt));

        PlannedChange del = Planner.ForDelete(corrupt);
        T.Eq("corrupt: delete is allowed", ChangeKind.Delete, del.Kind);
        T.Eq("corrupt: no job id", "", del.JobId);
        T.Eq("corrupt: no xml", (string?)null, del.NewXml);

        try { Planner.ForDelete(Foreign()); }
        catch (PlanRefusedException ex) { T.True("the refusal names the task and says why", ex.Message.Contains("Somebody else", StringComparison.Ordinal) && ex.Message.Contains("not created by Cadence", StringComparison.Ordinal), ex.Message); }
    }

    // ---- delete / re-apply ----

    public static void Delete()
    {
        PlannedChange p = Planner.ForDelete(ExistingStock());
        T.Eq("kind", ChangeKind.Delete, p.Kind);
        T.Eq("job id", "d4dst0ck", p.JobId);
        T.Eq("old name", StockName, p.OldTaskName);
        T.Eq("no new name", (string?)null, p.NewTaskName);
        T.Eq("no xml", (string?)null, p.NewXml);
        T.Eq("summary", "Will delete \\Cadence\\Stock ingest (Dad)", p.Summary);
    }

    public static void Reapply()
    {
        ClassifiedTask drifted = Drifted("T18:00:00", "T19:00:00");
        PlannedChange p = Planner.ForReapply(drifted, Ctx());
        T.Eq("kind", ChangeKind.Reapply, p.Kind);
        T.Eq("job id", "d4dst0ck", p.JobId);
        T.Eq("same task name", StockName, p.NewTaskName);
        T.True("summary lists what is discarded", p.Summary.Contains("time 18:00 \u2192 19:00", StringComparison.Ordinal), p.Summary);
        T.True("summary starts", p.Summary.StartsWith("Will re-apply Cadence's version of \\Cadence\\Stock ingest (Dad)", StringComparison.Ordinal), p.Summary);
        T.Eq("changes are the drift diffs", drifted.DriftDiffs, p.Changes);

        ClassifiedTask back = Classify(p.NewXml!);
        T.Eq("the written xml is InSync", SyncStatus.InSync, back.Sync);
        T.Eq("created kept", drifted.Metadata!.Created, back.Metadata!.Created);
        T.Eq("the trigger is back to 18:00", "18:00", Read(p.NewXml!).Triggers[0].StartBoundary.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture));
    }

    public static void ReapplyOfARenamedTaskRegistersUnderTheCurrentName()
    {
        // An exported/imported copy: it points at \Cadence\Stock ingest (Dad) but lives under another name.
        ClassifiedTask copy = Classify(Stock(), "Copy of Stock ingest");
        T.Eq("fixture is drifted (renamed)", SyncStatus.Drifted, copy.Sync);
        PlannedChange p = Planner.ForReapply(copy, Ctx());
        T.Eq("registered under the current name", "Copy of Stock ingest", p.NewTaskName);
        T.True("the wrapper names the current name", p.NewXml!.Contains("\\Cadence\\Copy of Stock ingest'", StringComparison.Ordinal));
        T.Eq("InSync under that name", SyncStatus.InSync, Classify(p.NewXml!, "Copy of Stock ingest").Sync);
        T.Eq("renamed: previous names untouched", 0, Meta(p.NewXml!).Prev.Count);
    }

    public static void ReapplyAlsoUpgradesAnOutdatedTask()
    {
        ClassifiedTask outdated = Classify(EditMeta(Stock(), m => m with { Gen = 0 }));
        PlannedChange p = Planner.ForReapply(outdated, Ctx());
        T.Eq("current gen", TaskComposer.GeneratorVersion, Meta(p.NewXml!).Gen);
        T.True("nothing discarded: plain summary", !p.Summary.Contains("discards", StringComparison.Ordinal), p.Summary);
    }

    // ---- untrack ----

    public static void Untrack()
    {
        ClassifiedTask managed = ExistingStock();
        PlannedChange p = Planner.ForUntrack(managed);
        T.Eq("kind", ChangeKind.Untrack, p.Kind);
        T.Eq("job id", "d4dst0ck", p.JobId);
        T.Eq("task name unchanged", StockName, p.NewTaskName);
        T.True("summary", p.Summary.StartsWith("Will stop managing \\Cadence\\Stock ingest (Dad)", StringComparison.Ordinal), p.Summary);
        T.True("summary says what stops", p.Summary.Contains("output capture", StringComparison.Ordinal) && p.Summary.Contains("holiday", StringComparison.Ordinal), p.Summary);

        TaskDefinitionModel after = Read(p.NewXml!);
        T.Eq("no Documentation", (string?)null, after.RegistrationInfo.Documentation);
        T.Eq("no Source", (string?)null, after.RegistrationInfo.Source);
        T.Eq("Author kept", "Cadence", after.RegistrationInfo.Author);
        T.True("Description appended", after.RegistrationInfo.Description!.EndsWith(" (no longer managed by Cadence)", StringComparison.Ordinal), after.RegistrationInfo.Description);
        // Checkpoint B: the untracked task must not keep claiming "Managed by Cadence - edit it in the Cadence app" or "except NYSE market
        // holidays" (only the wrapper skipped holidays, and it is gone).
        T.Eq("fixture: the managed Description", "Every weekday at 18:00, except NYSE market holidays" + TaskComposer.DescriptionSuffix, managed.Actual!.RegistrationInfo.Description);
        T.Eq("Description: Cadence's sentence and the holiday clause are gone", "Every weekday at 18:00 (no longer managed by Cadence)", after.RegistrationInfo.Description);
        string userText = "My note.";
        ClassifiedTask noted = Classify(Edit(Stock(), m => m with { RegistrationInfo = m.RegistrationInfo with { Description = userText } }));
        T.Eq("a Description the user rewrote is kept", userText + " (no longer managed by Cadence)", Read(Planner.ForUntrack(noted).NewXml!).RegistrationInfo.Description);
        ClassifiedTask empty = Classify(Edit(Stock(), m => m with { RegistrationInfo = m.RegistrationInfo with { Description = null } }));
        T.Eq("no Description: just the note", "(no longer managed by Cadence)", Read(Planner.ForUntrack(empty).NewXml!).RegistrationInfo.Description);

        RunSpec run = managed.Job!.Run;
        ExecActionModel action = after.ExecActions.Single();
        T.Eq("direct program", run.Exe, action.Command);
        T.Eq("direct arguments", run.Args, action.Arguments);
        T.Eq("direct working directory", run.WorkingDirectory, action.WorkingDirectory);
        T.True("the wrapper is gone", !p.NewXml!.Contains("$tp=", StringComparison.Ordinal) && !p.NewXml.Contains("conhost", StringComparison.Ordinal));

        T.Eq("triggers unchanged", managed.Actual!.Triggers, after.Triggers);
        T.Eq("settings unchanged", managed.Actual.Settings, after.Settings);
        T.Eq("principal unchanged", managed.Actual.Principal, after.Principal);

        ClassifiedTask back = Classify(p.NewXml!);
        T.Eq("re-classifies as Foreign", Ownership.Foreign, back.Ownership);
        T.Eq("Foreign has no sync status", SyncStatus.NotApplicable, back.Sync);
        T.Eq("changes: marker", "Managed by Cadence", p.Changes[0].Field);
        T.True("changes: program", p.Changes.Any(c => c.Field == "Program" && c.After == run.Exe));
    }

    public static void UntrackKeepsTheUsersEdits()
    {
        // Triggers and settings are taken from the LIVE definition, including what was edited in Task Scheduler.
        string edited = Stock().Replace("T18:00:00", "T19:00:00", StringComparison.Ordinal);
        ClassifiedTask drifted = Classify(Edit(edited, m => m with { Settings = m.Settings with { Priority = 4, Enabled = false } }));
        T.Eq("fixture is drifted", SyncStatus.Drifted, drifted.Sync);

        PlannedChange p = Planner.ForUntrack(drifted);
        TaskDefinitionModel after = Read(p.NewXml!);
        T.Eq("the edited time is kept", "19:00", after.Triggers[0].StartBoundary.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture));
        T.Eq("priority kept", 4, after.Settings.Priority);
        T.Eq("disabled kept", false, after.Settings.Enabled);
        T.Eq("still Foreign afterwards", Ownership.Foreign, Classify(p.NewXml!).Ownership);
    }

    public static void UntrackKeepsAnActionTheUserAlreadyReplaced()
    {
        string replaced = Edit(Stock(), m => m with { ExecActions = new ValueList<ExecActionModel>([new ExecActionModel("cmd.exe", "/c echo mine", @"C:\Temp")]) });
        ClassifiedTask drifted = Classify(replaced);
        T.Eq("fixture is drifted", SyncStatus.Drifted, drifted.Sync);

        PlannedChange p = Planner.ForUntrack(drifted);
        ExecActionModel action = Read(p.NewXml!).ExecActions.Single();
        T.Eq("command kept", "cmd.exe", action.Command);
        T.Eq("arguments kept", "/c echo mine", action.Arguments);
        T.Eq("working directory kept", @"C:\Temp", action.WorkingDirectory);
        T.True("no Program change is announced", p.Changes.All(c => c.Field != "Program"));
        T.True("summary says the action is kept", p.Summary.Contains("kept", StringComparison.Ordinal), p.Summary);
    }

    public static void UntrackOfJobsWithoutArgumentsOrWorkingDirectory()
    {
        JobDefinition bare = Job("Bare", "ffff0001", "0 9 * * *") with { Run = new RunSpec { Exe = @"C:\Tools\bare.exe", Hidden = false } };
        ClassifiedTask managed = Existing(bare, "Bare");
        ExecActionModel action = Read(Planner.ForUntrack(managed).NewXml!).ExecActions.Single();
        T.Eq("command", @"C:\Tools\bare.exe", action.Command);
        T.True("no arguments", string.IsNullOrEmpty(action.Arguments));
        T.True("no working directory", string.IsNullOrEmpty(action.WorkingDirectory));
    }

    public static void UntrackIsOfferedForOutdatedAndInSyncTasks()
    {
        T.Eq("in sync", ChangeKind.Untrack, Planner.ForUntrack(ExistingStock()).Kind);
        T.Eq("outdated", ChangeKind.Untrack, Planner.ForUntrack(Classify(EditMeta(Stock(), m => m with { Gen = 0 }))).Kind);
        T.Eq("renamed (drifted)", ChangeKind.Untrack, Planner.ForUntrack(Classify(Stock(), "Copy of Stock ingest")).Kind);
    }
}
