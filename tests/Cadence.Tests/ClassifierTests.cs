using Cadence.Core.Execution;
using Cadence.Core.Holidays;
using Cadence.Core.Metadata;
using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.Core.TaskXml;
using Cadence.TestHarness;
using static Cadence.Tests.PlanSamples;

namespace Cadence.Tests;

/// <summary>PLAN 3.12 with the [CP-A] rules 3a-3e: ownership, drift and outdated detection. Pure: no Task Scheduler.</summary>
public static class ClassifierTests
{
    static TaskDefinitionModel MapTriggers(TaskDefinitionModel m, Func<int, TaskTrigger, TaskTrigger> f) =>
        m with { Triggers = new ValueList<TaskTrigger>(m.Triggers.Select((t, i) => f(i, t))) };

    static ClassifiedTask Check(string label, string xml, Ownership own, SyncStatus sync, string? name = null, DateOnly? today = null, Func<string?, bool>? isUser = null)
    {
        ClassifiedTask c = Classify(xml, name ?? StockName, today, isUser);
        T.Eq(label + ": ownership", own, c.Ownership);
        T.Eq(label + ": sync", sync, c.Sync);
        return c;
    }

    public static void ForeignTasks()
    {
        ClassifiedTask plain = Check("plain task", Plain(), Ownership.Foreign, SyncStatus.NotApplicable);
        T.True("foreign: actual model parsed", plain.Actual is not null);
        T.True("foreign: no metadata, no job", plain.Metadata is null && plain.Job is null);
        T.Eq("foreign: no diffs", 0, plain.DriftDiffs.Count);

        TaskDefinitionModel model = Read(Plain());
        string[] docs = ["just some notes", "cadence", "Cadence documentation", "{\"schema\":1}", "cad/1\n{}"];
        foreach (string doc in docs)
        {
            string xml = TaskXmlWriter.Write(model with { RegistrationInfo = model.RegistrationInfo with { Documentation = doc } });
            Check("documentation '" + doc.Replace("\n", "\\n", StringComparison.Ordinal) + "'", xml, Ownership.Foreign, SyncStatus.NotApplicable);
        }

        // A foreign task that merely mentions Cadence, or carries Source = something else.
        string other = TaskXmlWriter.Write(model with { RegistrationInfo = model.RegistrationInfo with { Source = "Contoso", Description = "uses cadence" } });
        Check("other source", other, Ownership.Foreign, SyncStatus.NotApplicable);
    }

    public static void UnparseableXml()
    {
        Check("not xml, no marker", "this is not xml", Ownership.Foreign, SyncStatus.NotApplicable);
        Check("empty xml", "", Ownership.Foreign, SyncStatus.NotApplicable);
        Check("wrong root, no marker", "<Foo/>", Ownership.Foreign, SyncStatus.NotApplicable);
        ClassifiedTask c = Check("broken xml that mentions cadence/", "<Task><Documentation>cadence/1\n{</Task", Ownership.CorruptMetadata, SyncStatus.NotApplicable);
        T.True("corrupt: message says it can't be read", c.Problem is not null && c.Problem.Contains("can't be read", StringComparison.Ordinal), c.Problem);
    }

    public static void CorruptMetadata()
    {
        TaskDefinitionModel model = Read(Stock());

        string malformed = TaskXmlWriter.Write(model with { RegistrationInfo = model.RegistrationInfo with { Documentation = "cadence/1\n{\"schema\":1,\"oops\"" } });
        ClassifiedTask c = Check("marker with broken json", malformed, Ownership.CorruptMetadata, SyncStatus.NotApplicable);
        T.True("corrupt: no job", c.Job is null && c.Metadata is null);
        T.True("corrupt: problem mentions delete", c.Problem?.Contains("deleted", StringComparison.Ordinal) == true, c.Problem);

        string missing = TaskXmlWriter.Write(model with { RegistrationInfo = model.RegistrationInfo with { Documentation = null } });
        Check("Source = Cadence but no metadata", missing, Ownership.CorruptMetadata, SyncStatus.NotApplicable);

        string notCadence = TaskXmlWriter.Write(model with { RegistrationInfo = model.RegistrationInfo with { Documentation = "hand written" } });
        Check("Source = Cadence but another documentation", notCadence, Ownership.CorruptMetadata, SyncStatus.NotApplicable);

        // The metadata is what makes a task ours, whatever Source says.
        string noSource = TaskXmlWriter.Write(model with { RegistrationInfo = model.RegistrationInfo with { Source = null, Documentation = "cadence/1\n{broken" } });
        Check("broken metadata without Source", noSource, Ownership.CorruptMetadata, SyncStatus.NotApplicable);
    }

    public static void NewerVersions()
    {
        string stock = Stock();
        string schema2 = "cadence/2\n{\"schema\":2}";
        TaskDefinitionModel model = Read(stock);
        ClassifiedTask s2 = Check("schema 2", TaskXmlWriter.Write(model with { RegistrationInfo = model.RegistrationInfo with { Documentation = schema2 } }),
            Ownership.NewerVersion, SyncStatus.NotApplicable);
        T.True("schema 2: read-only message", s2.Problem?.Contains("newer version", StringComparison.Ordinal) == true, s2.Problem);

        // [CP-A] 3a: same schema, newer generator or wrapper.
        ClassifiedTask gen = Check("gen + 1", EditMeta(stock, m => m with { Gen = m.Gen + 1 }), Ownership.NewerVersion, SyncStatus.NotApplicable);
        T.True("gen + 1: job still decoded", gen.Job?.Id == "d4dst0ck" && gen.Metadata is not null);
        Check("wv + 1", EditMeta(stock, m => m with { Wv = m.Wv + 1 }), Ownership.NewerVersion, SyncStatus.NotApplicable);

        // 3a precedes every drift rule: a newer task that was also edited stays read-only.
        string edited = Edit(EditMeta(stock, m => m with { Gen = m.Gen + 1 }), m => MapTriggers(m, (_, t) => t with { StartBoundary = t.StartBoundary.Date.AddHours(19) }));
        ClassifiedTask both = Check("newer and edited", edited, Ownership.NewerVersion, SyncStatus.NotApplicable);
        T.Eq("newer and edited: no diffs offered", 0, both.DriftDiffs.Count);
    }

    public static void InSyncTable()
    {
        (string label, JobDefinition job)[] jobs =
        [
            ("stock (Nyse)", ComposeSamples.StockIngest()),
            ("stock (no holidays)", ComposeSamples.StockIngest(HolidayCalendar.None)),
            ("stock (US federal)", ComposeSamples.StockIngest(HolidayCalendar.UsFederal)),
            ("every 15 minutes", Job("Fifteen", "aaaa0001", "*/15 9-17 * * 1-5")),
            ("two lines", Job("Two lines", "aaaa0002", "0 7 * * *", "30 19 * * 1,3,5")),
            ("at logon", Job("Logon", "aaaa0003", "@logon")),
            ("last day of month", Job("Month end", "aaaa0004", "0 23 L * *")),
            ("last friday", Job("Last Friday", "aaaa0005", "0 17 * * 5L")),
        ];
        foreach ((string label, JobDefinition job) in jobs)
        {
            string xml = Compose(job, job.Name);
            ClassifiedTask c = Check(label, xml, Ownership.Managed, SyncStatus.InSync, job.Name);
            T.True(label + ": no problem, no diffs", c.Problem is null && c.DriftDiffs.Count == 0);
            T.Eq(label + ": job decoded", job, c.Job);
        }
    }

    public static void DriftedTimeDaysAndEnabled()
    {
        string stock = Stock();

        ClassifiedTask time = Check("time 18:00 -> 19:00", stock.Replace("T18:00:00", "T19:00:00", StringComparison.Ordinal), Ownership.Managed, SyncStatus.Drifted);
        T.Eq("time: one diff", 1, time.DriftDiffs.Count);
        T.Eq("time: field", "Time", time.DriftDiffs[0].Field);
        T.Eq("time: before", "18:00", time.DriftDiffs[0].Before);
        T.Eq("time: after", "19:00", time.DriftDiffs[0].After);
        T.Eq("time: described", "Time 18:00 \u2192 19:00", time.DriftDiffs[0].Describe());
        T.True("time: problem says what changed", time.Problem == "Changed in Task Scheduler: Time 18:00 \u2192 19:00", time.Problem);

        string sat = Edit(stock, m => MapTriggers(m, (_, t) => t is WeeklyTrigger w ? w with { Days = WeekDays.Weekdays | WeekDays.Saturday } : t));
        ClassifiedTask days = Check("days Mon-Fri -> Mon-Sat", sat, Ownership.Managed, SyncStatus.Drifted);
        T.Eq("days: described", "Days Mon-Fri \u2192 Mon-Sat", days.DriftDiffs.Single().Describe());

        string off = Edit(stock, m => m with { Settings = m.Settings with { Enabled = false } });
        ClassifiedTask disabled = Check("disabled", off, Ownership.Managed, SyncStatus.Drifted);
        T.Eq("disabled: described", "Disabled", disabled.DriftDiffs.Single().Describe());
        T.Eq("disabled: label", "Enabled", disabled.DriftDiffs.Single().Field);

        string ml = Edit(stock, m => MapTriggers(m, (_, t) => t with { Repetition = new Repetition(TimeSpan.FromMinutes(15), TimeSpan.FromHours(2), false) }));
        ClassifiedTask rep = Check("repetition added", ml, Ownership.Managed, SyncStatus.Drifted);
        T.True("repetition: an Interval diff", rep.DriftDiffs.Any(d => d.Field == "Interval" && d.Before == "none" && d.After == "every 15 min"),
            string.Join(" | ", rep.DriftDiffs.Select(d => d.Describe())));
    }

    public static void DriftedSettingsAndActions()
    {
        string stock = Stock();

        ClassifiedTask prio = Check("priority 7 -> 4", Edit(stock, m => m with { Settings = m.Settings with { Priority = 4 } }), Ownership.Managed, SyncStatus.Drifted);
        T.Eq("priority: described", "Setting: Priority 7 \u2192 4", prio.DriftDiffs.Single().Describe());

        ClassifiedTask limit = Check("time limit", Edit(stock, m => m with { Settings = m.Settings with { ExecutionTimeLimit = null } }), Ownership.Managed, SyncStatus.Drifted);
        T.Eq("time limit: described", "Setting: Time limit 35 min \u2192 unlimited", limit.DriftDiffs.Single().Describe());

        // The action replaced by an unrelated program: Drifted with the diff "Program".
        string replaced = Edit(stock, m => m with { ExecActions = new ValueList<ExecActionModel>([new ExecActionModel("cmd.exe", "/c exit 0", null)]) });
        ClassifiedTask prog = Check("action replaced", replaced, Ownership.Managed, SyncStatus.Drifted);
        T.True("action replaced: a Program diff", prog.DriftDiffs.Any(d => d.Field == "Program"), string.Join(" | ", prog.DriftDiffs.Select(d => d.Describe())));

        // Only the arguments of the wrapper were edited.
        string args = Edit(stock, m => m with { ExecActions = new ValueList<ExecActionModel>(m.ExecActions.Select(a => a with { Arguments = a.Arguments + " # edited" })) });
        ClassifiedTask edited = Check("wrapper arguments edited", args, Ownership.Managed, SyncStatus.Drifted);
        T.True("wrapper arguments: an Arguments diff", edited.DriftDiffs.Any(d => d.Field == "Arguments"));

        string extra = Edit(stock, m => m with { ExecActions = new ValueList<ExecActionModel>(m.ExecActions.Append(new ExecActionModel("cmd.exe", null, null))) });
        ClassifiedTask two = Check("second action added", extra, Ownership.Managed, SyncStatus.Drifted);
        T.True("second action: a Program diff", two.DriftDiffs.Any(d => d.Field == "Program"));
    }

    public static void DriftedMultipleTriggersArePrefixed()
    {
        JobDefinition job = Job("Two", "bbbb0001", "0 7 * * *", "30 19 * * *");
        string xml = Compose(job, "Two");
        Check("composed two-trigger job", xml, Ownership.Managed, SyncStatus.InSync, "Two");

        string edited = Edit(xml, m => MapTriggers(m, (i, t) => i == 1 ? t with { StartBoundary = t.StartBoundary.Date.AddHours(20) } : t));
        ClassifiedTask c = Check("second trigger edited", edited, Ownership.Managed, SyncStatus.Drifted, "Two");
        T.Eq("prefixed label", "Trigger 2: Time", c.DriftDiffs.Single().Field);
        T.Eq("prefixed description", "Trigger 2: Time 19:30 \u2192 20:00", c.DriftDiffs.Single().Describe());

        string fewer = Edit(xml, m => m with { Triggers = new ValueList<TaskTrigger>(m.Triggers.Take(1)) });
        ClassifiedTask c2 = Check("a trigger removed", fewer, Ownership.Managed, SyncStatus.Drifted, "Two");
        T.Eq("trigger count diff", "Triggers 2 triggers \u2192 1 trigger", c2.DriftDiffs.Single().Describe());
    }

    public static void RenamedOrMovedOutsideCadence()
    {
        string stock = Stock();   // its wrapper says $tp='\Cadence\Stock ingest (Dad)'

        ClassifiedTask renamed = Check("renamed", stock, Ownership.Managed, SyncStatus.Drifted, "Copy of Stock ingest");
        T.True("renamed: problem", renamed.Problem?.Contains("Renamed or moved", StringComparison.Ordinal) == true, renamed.Problem);
        T.True("renamed: Task path diff", renamed.DriftDiffs.Any(d => d.Field == "Task path" && d.Before == "\\Cadence\\Stock ingest (Dad)" && d.After == "\\Cadence\\Copy of Stock ingest"),
            string.Join(" | ", renamed.DriftDiffs.Select(d => d.Describe())));

        ClassifiedTask moved = Classify(stock, StockName, null, null, "Elsewhere");
        T.Eq("moved to another folder: sync", SyncStatus.Drifted, moved.Sync);
        T.Eq("moved to another folder: ownership", Ownership.Managed, moved.Ownership);

        // A different case is the same task (Task Scheduler names are case-insensitive): not "renamed". Cadence would write the name's
        // case into the wrapper, so the task reads as Outdated, never as Drifted.
        ClassifiedTask cased = Check("same name, other case", stock, Ownership.Managed, SyncStatus.Outdated, "STOCK INGEST (DAD)");
        T.True("same name, other case: not reported as renamed", cased.Problem?.Contains("Renamed", StringComparison.Ordinal) == false, cased.Problem);

        // Rename of a task exported from another one: the fingerprint is unchanged (so only rule 3b notices).
        T.Eq("fingerprint unchanged by the rename", Meta(stock).Fp, Fingerprint.Of(Read(stock)));
    }

    public static void RunsAsADifferentUser()
    {
        string stock = Stock();
        string other = Edit(stock, m => m with { Principal = m.Principal with { UserId = "S-1-5-21-1-2-3-9999" } });

        ClassifiedTask c = Check("other principal", other, Ownership.Managed, SyncStatus.Drifted);
        T.True("other principal: problem", c.Problem?.Contains("different user", StringComparison.Ordinal) == true, c.Problem);
        T.True("other principal: User diff", c.DriftDiffs.Any(d => d.Field == "User" && d.After == "S-1-5-21-1-2-3-9999"));

        // The resolver decides (Core never compares names): a SID that resolves to the connected user is fine.
        ClassifiedTask sid = Check("SID that resolves to the user", other, Ownership.Managed, SyncStatus.InSync, null, null, id => id == "S-1-5-21-1-2-3-9999");
        T.True("SID: no problem", sid.Problem is null);

        string none = Edit(stock, m => m with { Principal = m.Principal with { UserId = null } });
        Check("no principal user", none, Ownership.Managed, SyncStatus.Drifted);

        // Both a rename and another user: both reasons are reported.
        ClassifiedTask both = Check("renamed and other user", other, Ownership.Managed, SyncStatus.Drifted, "Renamed copy");
        T.True("both: two reasons", both.Problem?.Contains("Renamed or moved", StringComparison.Ordinal) == true && both.Problem.Contains("different user", StringComparison.Ordinal), both.Problem);
    }

    public static void OutdatedRules()
    {
        string stock = Stock();

        ClassifiedTask gen0 = Check("gen 0 with an unchanged fingerprint", EditMeta(stock, m => m with { Gen = 0 }), Ownership.Managed, SyncStatus.Outdated);
        T.True("gen 0: message", gen0.Problem?.StartsWith("Cadence will update this task when you save", StringComparison.Ordinal) == true, gen0.Problem);
        T.Eq("gen 0: no drift diffs", 0, gen0.DriftDiffs.Count);
        Check("wv 0", EditMeta(stock, m => m with { Wv = 0 }), Ownership.Managed, SyncStatus.Outdated);

        // The holiday list differs from the current rules (one date edited): metadata-only, every fingerprint unchanged.
        string oneDate = EditMeta(stock, m => m with
        {
            Skip = m.Skip! with
            {
                Dates = new ValueList<Holiday>(m.Skip!.Dates.Select((h, i) => i == 5 ? h with { Date = h.Date.AddDays(1) } : h)),
            },
        });
        Check("one holiday date edited", oneDate, Ownership.Managed, SyncStatus.Outdated);

        string dropped = EditMeta(stock, m => m with { Skip = m.Skip! with { Dates = new ValueList<Holiday>(m.Skip!.Dates.Skip(1)) } });
        Check("one holiday missing", dropped, Ownership.Managed, SyncStatus.Outdated);

        string renamedHoliday = EditMeta(stock, m => m with
        {
            Skip = m.Skip! with { Dates = new ValueList<Holiday>(m.Skip!.Dates.Select((h, i) => i == 0 ? h with { Name = "Something else" } : h)) },
        });
        Check("one holiday renamed", renamedHoliday, Ownership.Managed, SyncStatus.Outdated);

        // until within 365 days of today (the date is a parameter, never the clock).
        Check("until is far away", stock, Ownership.Managed, SyncStatus.InSync, null, new DateOnly(2030, 1, 1));
        Check("until within 365 days", stock, Ownership.Managed, SyncStatus.Outdated, null, new DateOnly(2036, 6, 1));
        Check("until exactly 365 days away", stock, Ownership.Managed, SyncStatus.InSync, null, new DateOnly(2036, 12, 31).AddDays(-365));
        Check("until 364 days away", stock, Ownership.Managed, SyncStatus.Outdated, null, new DateOnly(2036, 12, 31).AddDays(-364));

        // skipHolidays set but run.skip missing, and the reverse.
        Check("skipHolidays set, run.skip missing", EditMeta(stock, m => m with { Skip = null }), Ownership.Managed, SyncStatus.Outdated);
        string plain = Compose(ComposeSamples.StockIngest(HolidayCalendar.None), StockName);
        Check("no skipHolidays, run.skip present",
            EditMeta(plain, m => m with { Skip = new SkipList(HolidayCalendar.Nyse, new DateOnly(2036, 12, 31), HolidayRules.Range(HolidayCalendar.Nyse, 2026, 2036)) }),
            Ownership.Managed, SyncStatus.Outdated);

        // A holiday list for another calendar than the job's.
        Check("run.skip for another calendar",
            EditMeta(stock, m => m with { Skip = new SkipList(HolidayCalendar.UsFederal, m.Skip!.Until, HolidayRules.Range(HolidayCalendar.UsFederal, 2026, 2036)) }),
            Ownership.Managed, SyncStatus.Outdated);
    }

    public static void MetadataOnlyEditsAreInSync()
    {
        string stock = Stock();

        ClassifiedTask name = Check("display name only", EditMeta(stock, m => m with { Job = m.Job with { Name = "Renamed in the metadata" } }), Ownership.Managed, SyncStatus.InSync);
        T.Eq("display name: decoded", "Renamed in the metadata", name.Job?.Name);

        Check("command and arguments edited in the metadata",
            EditMeta(stock, m => m with { Job = m.Job with { Run = m.Job.Run with { Exe = @"C:\Other\tool.exe", Args = "--new" } } }),
            Ownership.Managed, SyncStatus.InSync);
        Check("environment edited in the metadata",
            EditMeta(stock, m => m with { Job = m.Job with { Run = m.Job.Run with { Env = ValueList<KeyValuePair<string, string>>.Empty } } }),
            Ownership.Managed, SyncStatus.InSync);
    }

    public static void DriftBeatsOutdated()
    {
        string edited = Edit(EditMeta(Stock(), m => m with { Gen = 0 }), m => MapTriggers(m, (_, t) => t with { StartBoundary = t.StartBoundary.Date.AddHours(19) }));
        ClassifiedTask c = Check("edited and gen 0", edited, Ownership.Managed, SyncStatus.Drifted);
        T.Eq("diff is the edit", "Time 18:00 \u2192 19:00", c.DriftDiffs.Single().Describe());
    }

    public static void JobThatCanNoLongerBeComposedIsOutdated()
    {
        // Metadata decodes (the decoder does not compile cron) but the composer refuses: the task must not read as InSync.
        string xml = EditMeta(Stock(), m => m with { Job = m.Job with { Schedule = m.Job.Schedule with { CronLines = ValueList.Of("this is not cron") } } });
        ClassifiedTask c = Check("uncomposable job", xml, Ownership.Managed, SyncStatus.Outdated);
        T.True("uncomposable: the reason is shown", c.Problem?.Contains("can't rebuild", StringComparison.Ordinal) == true, c.Problem);
    }

    public static void ClassificationUsesTheContextFactory()
    {
        // The factory receives the decoded metadata and the CURRENT task name.
        CadenceMetadata? seenMeta = null;
        string? seenName = null;
        var task = Observe(Stock(), "Some name");
        Classifier.Classify(task, (m, n) => { seenMeta = m; seenName = n; return FactoryContext(m, n); }, ComposeSamples.Anchor, IsUser);
        T.Eq("factory gets the task name", "Some name", seenName);
        T.Eq("factory gets the metadata", "d4dst0ck", seenMeta?.Job.Id);

        // Foreign and corrupt tasks never reach the factory (nothing to compose).
        bool called = false;
        Classifier.Classify(Observe(Plain()), (m, n) => { called = true; return FactoryContext(m, n); }, ComposeSamples.Anchor, IsUser);
        T.True("foreign: factory not called", !called);
        Classifier.Classify(Observe("not xml"), (m, n) => { called = true; return FactoryContext(m, n); }, ComposeSamples.Anchor, IsUser);
        T.True("unparseable: factory not called", !called);
        T.Throws<ArgumentNullException>("null task", () => Classifier.Classify(null!, (m, n) => FactoryContext(m, n), ComposeSamples.Anchor, IsUser));
    }
}
