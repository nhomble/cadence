using Cadence.App.UI.Logic;
using Cadence.Core.Holidays;
using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.Core.TaskXml;
using Cadence.TestHarness;
using static Cadence.Tests.PlanSamples;

namespace Cadence.Tests;

/// <summary>
/// The notice above the editor (Phase 15): which buttons exist for every classification (InSync, Edited, Renamed, DifferentUser, Duplicate, Outdated,
/// NewerVersion, Corrupt, Foreign), the English wording, and the confirmation dialogs. Pure: real classifications of composed task XML, no UI.
/// </summary>
public static class DriftTextTests
{
    static BannerAction[] Buttons(BannerState s) => s.Actions.ToArray();

    static ClassifiedTask Edited() => Classify(Stock().Replace("T18:00:00", "T19:00:00", StringComparison.Ordinal));

    static ClassifiedTask Copy() =>
        DuplicateJobIds.Resolve([Classify(Stock()), Classify(Stock(), "Stock ingest (copy)")])[1];

    public static void AnInSyncOrForeignTaskHasNoNotice()
    {
        T.True("in sync: nothing to say", DriftText.For(Classify(Stock())) is null);
        T.True("foreign: nothing to say", DriftText.For(Classify(Plain())) is null);
    }

    public static void PlainDriftOffersExactlyReapplyAndStopManaging()
    {
        ClassifiedTask task = Edited();
        T.Eq("fixture is drifted", SyncStatus.Drifted, task.Sync);
        BannerState s = DriftText.For(task)!;
        T.Eq("kind", BannerKind.Edited, s.Kind);
        T.Eq("title", "This schedule was changed outside Cadence (in Task Scheduler).", s.Title);
        T.True("exactly two buttons, in order", Buttons(s).SequenceEqual([BannerAction.Reapply, BannerAction.Untrack]));
        T.True("the diff reads in plain English", s.Lines.SequenceEqual(["Time 18:00 \u2192 19:00"]));
        T.True("it is a warning that locks the form", !s.IsQuiet && s.LocksForm && !s.IsDamage);
        T.True("the note says what each button does", s.Note!.Contains("Re-apply puts it back", StringComparison.Ordinal) && s.Note.Contains("Stop managing leaves the task as it is", StringComparison.Ordinal)
            && s.Note.Contains("left side of each arrow", StringComparison.Ordinal));
        T.True("[CP-C] no jargon in the note", !s.Note.Contains("marker", StringComparison.OrdinalIgnoreCase), s.Note);
        T.True("has / has not", s.Has(BannerAction.Reapply) && !s.Has(BannerAction.Delete) && !s.Has(BannerAction.RefreshNow));
    }

    public static void ARenamedTaskOffersReapplyAndStopManaging()
    {
        ClassifiedTask task = Classify(Stock(), "Copy of Stock ingest");
        BannerState s = DriftText.For(task)!;
        T.Eq("kind", BannerKind.Renamed, s.Kind);
        T.Eq("title", "This schedule was renamed or moved outside Cadence.", s.Title);
        T.True("Re-apply registers it under the current name, so it is offered", Buttons(s).SequenceEqual([BannerAction.Reapply, BannerAction.Untrack]));
        T.True("the line names both paths", s.Lines.Single().StartsWith("Renamed: now \\Cadence\\Copy of Stock ingest", StringComparison.Ordinal) && s.Lines.Single().Contains("still points at \\Cadence\\Stock ingest (Dad)", StringComparison.Ordinal), s.Lines.Single());
    }

    public static void ADifferentUserNeverOffersReapply()
    {
        ClassifiedTask task = Classify(Edit(Stock(), m => m with { Principal = m.Principal with { UserId = "S-1-5-21-1-2-3-9999" } }));
        BannerState s = DriftText.For(task)!;
        T.Eq("kind", BannerKind.DifferentUser, s.Kind);
        T.True("Stop managing and Delete only", Buttons(s).SequenceEqual([BannerAction.Untrack, BannerAction.Delete]));
        T.True("no Re-apply", !s.Has(BannerAction.Reapply));
        T.True("the note explains why", s.Note!.Contains("would make the schedule run as you", StringComparison.Ordinal));
        T.True("the line says who it runs as", s.Lines.Any(l => l.StartsWith("Runs as S-1-5-21-1-2-3-9999", StringComparison.Ordinal)), string.Join("|", s.Lines));

        // Also when the user was changed together with something else.
        ClassifiedTask both = Classify(Edit(Stock().Replace("T18:00:00", "T19:00:00", StringComparison.Ordinal), m => m with { Principal = m.Principal with { UserId = "S-1-5-21-1-2-3-9999" } }));
        BannerState b = DriftText.For(both)!;
        T.True("user + time: still no Re-apply", b.Kind == BannerKind.DifferentUser && !b.Has(BannerAction.Reapply));
        T.True("user + time: both lines", b.Lines.Count == 2 && b.Lines.Any(l => l.StartsWith("Time 18:00", StringComparison.Ordinal)));
    }

    public static void ACopyNeverOffersReapply()
    {
        ClassifiedTask copy = Copy();
        T.Eq("fixture: the copy is flagged", "Stock ingest (Dad)", copy.DuplicateOf);
        BannerState s = DriftText.For(copy)!;
        T.Eq("kind", BannerKind.Duplicate, s.Kind);
        T.Eq("title", "This is a copy of \"Stock ingest (Dad)\" that was made outside Cadence.", s.Title);
        T.True("Stop managing and Delete only", Buttons(s).SequenceEqual([BannerAction.Untrack, BannerAction.Delete]));
        T.True("the reason", s.Lines.Single() == "Two schedules can't share one run history.");
        T.True("a copy that was also changed still only offers those two", DriftText.For(copy with { DriftDiffs = ValueList.Of(new FieldDiff("Time", "18:00", "19:00")) })!.Kind == BannerKind.Duplicate);
    }

    public static void AnOutdatedTaskGetsAQuietLineNotAWarning()
    {
        ClassifiedTask task = Classify(EditMeta(Stock(), m => m with { Gen = 0 }));
        T.Eq("fixture is outdated", SyncStatus.Outdated, task.Sync);
        BannerState s = DriftText.For(task)!;
        T.Eq("kind", BannerKind.Outdated, s.Kind);
        T.True("quiet and does not lock the form", s.IsQuiet && !s.LocksForm);
        T.True("only Refresh now", Buttons(s).SequenceEqual([BannerAction.RefreshNow]));
        T.True("the line", s.Title.StartsWith("This task needs a quick refresh (", StringComparison.Ordinal) && s.Title.EndsWith("It keeps running meanwhile.", StringComparison.Ordinal), s.Title);
        T.True("[CP-C] no promise about Save (Save is off until an edit; the link is the way)", !s.Title.Contains("save", StringComparison.OrdinalIgnoreCase), s.Title);
        T.True("no lines, no note", s.Lines.Count == 0 && s.Note is null);

        T.True("holiday list running out", DriftText.OutdatedLine("Cadence will update this task when you save (holiday list expires soon).").Contains("its holiday list is running out", StringComparison.Ordinal));
        T.True("holiday list changed", DriftText.OutdatedLine("Cadence will update this task when you save (holiday list).").Contains("the holiday list changed", StringComparison.Ordinal));
        T.True("format", DriftText.OutdatedLine("Cadence will update this task when you save (task definition, generator version).").Contains("writes tasks a little differently", StringComparison.Ordinal));
        T.True("unknown reason still reads", DriftText.OutdatedLine(null).Contains("newer format", StringComparison.Ordinal));
    }

    public static void ANewerVersionTaskIsReadOnlyWithNothingToChange()
    {
        ClassifiedTask task = Classify(EditMeta(Stock(), m => m with { Gen = m.Gen + 1 }));
        T.Eq("fixture", Ownership.NewerVersion, task.Ownership);
        BannerState s = DriftText.For(task)!;
        T.Eq("kind", BannerKind.NewerVersion, s.Kind);
        T.True("only Open in Task Scheduler", Buttons(s).SequenceEqual([BannerAction.OpenInTaskScheduler]));
        T.True("locks the form", s.LocksForm && !s.IsQuiet);
        T.True("says read-only and to update Cadence", s.Note!.Contains("read-only", StringComparison.Ordinal) && s.Note.Contains("Update Cadence", StringComparison.Ordinal));
    }

    public static void ACorruptTaskCanOnlyBeDeleted()
    {
        TaskDefinitionModel model = Read(Stock());
        string corrupt = TaskXmlWriter.Write(model with { RegistrationInfo = model.RegistrationInfo with { Documentation = "cadence/1\n{broken" } });
        ClassifiedTask task = Classify(corrupt);
        T.Eq("fixture", Ownership.CorruptMetadata, task.Ownership);
        BannerState s = DriftText.For(task)!;
        T.Eq("kind", BannerKind.Corrupt, s.Kind);
        T.True("Delete (and a way to look at it), never Re-apply or Stop managing", Buttons(s).SequenceEqual([BannerAction.Delete, BannerAction.OpenInTaskScheduler]));
        T.True("a Red outline", s.IsDamage);
        T.True("the row dialogs read as before", DriftText.CorruptMessage.StartsWith("This task was created by Cadence but its settings are damaged", StringComparison.Ordinal)
            && DriftText.NewerMessage.StartsWith("Created by a newer Cadence version; read-only.", StringComparison.Ordinal));
    }

    public static void EveryClassificationHasTheRightButtons()
    {
        // One table for the whole state machine: what a person can click in each state.
        (string Label, ClassifiedTask Task, BannerAction[]? Expected)[] table =
        [
            ("in sync", Classify(Stock()), null),
            ("edited", Edited(), [BannerAction.Reapply, BannerAction.Untrack]),
            ("renamed", Classify(Stock(), "Moved"), [BannerAction.Reapply, BannerAction.Untrack]),
            ("different user", Classify(Edit(Stock(), m => m with { Principal = m.Principal with { UserId = "S-1-5-21-1-2-3-9999" } })), [BannerAction.Untrack, BannerAction.Delete]),
            ("copy", Copy(), [BannerAction.Untrack, BannerAction.Delete]),
            ("outdated", Classify(EditMeta(Stock(), m => m with { Gen = 0 })), [BannerAction.RefreshNow]),
            ("newer", Classify(EditMeta(Stock(), m => m with { Gen = m.Gen + 1 })), [BannerAction.OpenInTaskScheduler]),
            ("foreign", Classify(Plain()), null),
        ];
        foreach ((string label, ClassifiedTask task, BannerAction[]? expected) in table)
        {
            BannerState? s = DriftText.For(task);
            if (expected is null) T.True(label + ": no notice", s is null);
            else T.True(label + ": buttons " + string.Join("+", expected), s is not null && Buttons(s).SequenceEqual(expected), s is null ? "null" : string.Join("+", s.Actions));
        }
    }

    public static void LongDiffListsAreCut()
    {
        ClassifiedTask task = Edited() with { DriftDiffs = new ValueList<FieldDiff>(Enumerable.Range(1, 9).Select(i => new FieldDiff("Setting " + i, "a", "b"))) };
        IReadOnlyList<string> lines = DriftText.DiffLines(task.DriftDiffs);
        T.Eq("six lines", DriftText.MaxLines, lines.Count);
        T.Eq("the last says how many more", "and 4 more changes", lines[^1]);
        T.Eq("an empty list stays empty", 0, DriftText.DiffLines(ValueList<FieldDiff>.Empty).Count);
    }

    public static void ReapplyConfirmationListsWhatIsOverwritten()
    {
        ConfirmText ask = DriftText.ReapplyConfirm(Edited());
        T.Eq("title", "Re-apply Cadence's version of \"Stock ingest (Dad)\"?", ask.Title);
        T.Eq("confirm label", "Re-apply", ask.Confirm);
        T.True("the change that will be overwritten", ask.Message.Contains("\u2022 Time: 19:00 in Task Scheduler is replaced by 18:00", StringComparison.Ordinal), ask.Message);
        T.True("history is kept", ask.Message.Contains("keeps its run history", StringComparison.Ordinal));

        ConfirmText renamed = DriftText.ReapplyConfirm(Classify(Stock(), "Moved"));
        T.True("a rename is re-registered under its own name", renamed.Message.Contains("registered again under its current name, \\Cadence\\Moved", StringComparison.Ordinal), renamed.Message);

        ClassifiedTask disabled = Classify(Edit(Stock(), m => m with { Settings = m.Settings with { Enabled = false } }));
        T.True("a disabled task is switched back on", DriftText.ReapplyConfirm(disabled).Message.Contains("switched back on", StringComparison.Ordinal));
    }

    public static void UntrackConfirmationListsTheConsequences()
    {
        ClassifiedTask hiddenWithHolidays = Edited();   // Stock ingest: hidden, NYSE holidays, an environment variable and a time limit
        ConfirmText ask = DriftText.UntrackConfirm(hiddenWithHolidays);
        T.Eq("title", "Stop managing \"Stock ingest (Dad)\"?", ask.Title);
        T.Eq("confirm label", "Stop managing", ask.Confirm);
        T.True("the task stays", ask.Message.Contains("The task stays in Task Scheduler with its triggers and settings exactly as they are now.", StringComparison.Ordinal));
        T.True("what it runs instead", ask.Message.Contains("It will run C:\\Windows\\py.exe -u", StringComparison.Ordinal), ask.Message);
        T.True("what stops", ask.Message.Contains("Output capture and run history stop.", StringComparison.Ordinal));
        T.True("the time limit", ask.Message.Contains("The time limit (30 min) no longer applies: a program that runs too long won't be stopped.", StringComparison.Ordinal), ask.Message);
        T.True("a hidden job: the console window", ask.Message.Contains("A console window will appear each time it runs.", StringComparison.Ordinal));
        T.True("skip holidays: it runs on holidays too", ask.Message.Contains("It will run on holidays too.", StringComparison.Ordinal));
        T.True("environment variables", ask.Message.Contains("The environment variable you set is no longer passed to it.", StringComparison.Ordinal));
        T.True("where to find it", ask.Message.Contains("\\Cadence\\Stock ingest (Dad)", StringComparison.Ordinal));
        T.True("bullets", ask.Message.Split('\n').All(l => l.Length == 0 || l.StartsWith("\u2022 ", StringComparison.Ordinal)));

        JobDefinition plain = ComposeSamples.StockIngest(HolidayCalendar.None);
        plain = plain with { Run = plain.Run with { Hidden = false, Env = ValueList<KeyValuePair<string, string>>.Empty } };
        ConfirmText quiet = DriftText.UntrackConfirm(Classify(Compose(plain, StockName)));
        T.True("a visible job: no console sentence", !quiet.Message.Contains("console window", StringComparison.Ordinal));
        T.True("no holiday skipping: no holiday sentence", !quiet.Message.Contains("holidays", StringComparison.Ordinal));
        T.True("no variables: not mentioned", !quiet.Message.Contains("environment variable", StringComparison.Ordinal));
        plain = plain with { Run = plain.Run with { Timeout = null } };
        T.True("no time limit: not mentioned", !DriftText.UntrackConfirm(Classify(Compose(plain, StockName))).Message.Contains("time limit", StringComparison.Ordinal));

        // The action was already replaced in Task Scheduler: it is kept, so only the bookkeeping stops.
        ClassifiedTask replaced = Classify(Edit(Stock(), m => m with { ExecActions = ValueList.Of(new ExecActionModel("notepad.exe", null, null)) }));
        ConfirmText kept = DriftText.UntrackConfirm(replaced);
        T.True("an already replaced action is kept", kept.Message.Contains("Its action is kept as it is.", StringComparison.Ordinal) && !kept.Message.Contains("It will run C:", StringComparison.Ordinal), kept.Message);
    }
}
