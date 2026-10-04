using Cadence.App.UI.Logic;
using Cadence.App.UI.Services;
using Cadence.App.UI.ViewModels;
using Cadence.Core.History;
using Cadence.Core.Metadata;
using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.Core.TaskXml;
using Cadence.TestHarness;
using static Cadence.Tests.PlanSamples;
using static Cadence.Tests.ScriptedServices;

namespace Cadence.Tests;

/// <summary>
/// The editor's notice and its decisions (Phase 15): the banner state machine as the editor shows it, Re-apply and Stop managing with their
/// confirmations (declined, done, failed), the Outdated line, the copy and different-user restrictions, the ⋯ menu (Duplicate, Stop managing, Open in
/// Task Scheduler) and read-only forms. A scripted services object plays Task Scheduler; real classifications of composed XML are the data.
/// </summary>
public static class EditorNoticeTests
{
    static readonly DateTimeOffset Now = Cadence.App.Demo.DemoServices.FixedNow;

    static (EditorViewModel Vm, ScriptedServices Svc, RecordingDialogs Dlg, RecordingHost Host) Open(ClassifiedTask task, params RunView[] runs)
    {
        var svc = new ScriptedServices();
        svc.Entries.Add(Entry(task, runs));
        var dlg = new RecordingDialogs();
        var host = new RecordingHost();
        return (EditorViewModel.ForEntry(svc, dlg, host.Value, svc.Entries[0]), svc, dlg, host);
    }

    static ClassifiedTask Edited() => Classify(Stock().Replace("T18:00:00", "T19:00:00", StringComparison.Ordinal));

    static ClassifiedTask DifferentUser() => Classify(Edit(Stock(), m => m with { Principal = m.Principal with { UserId = "S-1-5-21-1-2-3-9999" } }));

    static ClassifiedTask Copy() => DuplicateJobIds.Resolve([Classify(Stock()), Classify(Stock(), "Stock ingest (copy)")])[1];

    static void Replace(ScriptedServices svc, string taskName, ClassifiedTask task, params RunView[] runs)
    {
        int i = svc.Entries.FindIndex(e => e.Task.Observed.Name == taskName);
        svc.Entries[i] = Entry(task, runs);
    }

    // ---- the banner ----

    // ---- Phase 19: the editor passes the revision it opened and carries the agent lock through every save ----

    public static void SaveSendsTheOpenedRevisionAndKeepsTheAgentLock()
    {
        ClassifiedTask locked = Classify(EditMeta(Stock(), m => m with { Job = m.Job with { AgentLock = true } }));
        T.Eq("fixture: a locked job is still in sync", SyncStatus.InSync, locked.Sync);
        var (vm, svc, _, _) = Open(locked);
        vm.Name = "Stock ingest renamed";
        vm.SaveAsync().GetAwaiter().GetResult();
        T.True("saved", svc.Calls.Contains("save Stock ingest (Dad)"));
        T.Eq("the revision of the task as the editor opened it", JobRevision.Of(locked), svc.LastExpectedRevision);
        T.True("the lock is carried through the save (the editor has no control for it yet)", svc.LastSaved?.AgentLock == true);
        T.Eq("the rest of the job is as edited", "Stock ingest renamed", svc.LastSaved?.Name);

        var (vm2, svc2, _, _) = Open(Classify(Stock()));
        vm2.Name = "Another name";
        vm2.SaveAsync().GetAwaiter().GetResult();
        T.True("an unlocked job stays unlocked", svc2.LastSaved?.AgentLock == false);
    }

    public static void ABackgroundRefreshDoesNotHideAChangeMadeByAnotherWriter()
    {
        ClassifiedTask opened = Classify(Stock());
        var (vm, svc, _, _) = Open(opened);

        // Another writer (the command line) changes the job while the editor is open; the live timer reloads the entry.
        ClassifiedTask theirs = Classify(Compose(ComposeSamples.StockIngest() with { Run = ComposeSamples.StockIngest().Run with { Args = "theirs" } }, StockName, ComposeSamples.Now.AddMinutes(5)));
        Replace(svc, StockName, theirs);
        vm.Runs.RefreshRunsAsync().GetAwaiter().GetResult();

        vm.Name = "Mine";
        vm.SaveAsync().GetAwaiter().GetResult();
        T.Eq("the save still carries the revision of what the person was looking at, so the Applier can refuse it", JobRevision.Of(opened), svc.LastExpectedRevision);
        T.True("which differs from the live one", JobRevision.Of(opened) != JobRevision.Of(theirs));

        // "Refresh now" saves the current live job as it is, so it carries the current revision instead.
        var (vm3, svc3, _, _) = Open(Classify(EditMeta(Stock(), m => m with { Gen = 0 })));
        vm3.Notice.RefreshNowAsync().GetAwaiter().GetResult();
        T.Eq("refresh now: the revision of the entry it refreshes", JobRevision.Of(svc3.Entries[0].Task), svc3.LastExpectedRevision);
    }

    public static void ADriftedEditorShowsTheBannerAndIsReadOnly()
    {
        var (vm, _, _, _) = Open(Edited());
        T.True("banner", vm.Notice.HasBanner && vm.Notice.BannerTitle == "This schedule was changed outside Cadence (in Task Scheduler).");
        T.True("the diff", vm.Notice.BannerLines.SequenceEqual(["Time 18:00 \u2192 19:00"]) && vm.Notice.HasBannerLines);
        T.True("Re-apply and Stop managing, not Delete", vm.Notice.ShowReapply && vm.Notice.ShowUntrack && !vm.Notice.ShowBannerDelete && !vm.Notice.ShowBannerOpen);
        T.True("locked and read-only", vm.Notice.IsLocked && !vm.Notice.FormEnabled && vm.Notice.LockedNote == vm.Notice.BannerTitle);
        vm.Name = "Another name";
        T.True("Save stays off", !vm.CanSave);
        T.True("Run now is off until the decision is made", !vm.Runs.CanRunNow && !vm.Runs.RunNowCommand.CanExecute(null));
        T.True("no Outdated line", !vm.Notice.HasOutdated && vm.Notice.OutdatedLine is null);
        T.True("the menu exists for a saved schedule", vm.HasMenu);
    }

    public static void AnInSyncEditorHasNoBannerAndIsEditable()
    {
        var (vm, _, _, _) = Open(Classify(Stock()));
        T.True("no banner, no line", !vm.Notice.HasBanner && !vm.Notice.HasOutdated && vm.Notice.BannerTitle is null);
        T.True("editable", !vm.Notice.IsLocked && vm.Notice.FormEnabled);
        T.True("run now is on", vm.Runs.CanRunNow);
    }

    // ---- Re-apply ----

    public static void ReapplyAsksFirstThenRestoresCadencesVersion()
    {
        var (vm, svc, dlg, host) = Open(Edited());
        svc.AfterReapply = name => Replace(svc, name, Classify(Stock()));

        dlg.Answer = false;
        vm.Notice.ReapplyAsync().GetAwaiter().GetResult();
        T.Eq("asked once", 1, dlg.Asked.Count);
        T.True("declined: nothing was written", !svc.Calls.Any(c => c.StartsWith("reapply", StringComparison.Ordinal)) && vm.Notice.HasBanner);
        T.Eq("title", "Re-apply Cadence's version of \"Stock ingest (Dad)\"?", dlg.Asked[0].Title);
        T.True("not a danger button", !dlg.Asked[0].Danger && dlg.Asked[0].Confirm == "Re-apply");
        T.True("the dialog lists what is overwritten", dlg.Asked[0].Message.Contains("Time: 19:00 in Task Scheduler is replaced by 18:00", StringComparison.Ordinal));

        dlg.Answer = true;
        vm.Notice.ReapplyCommand.Execute(null);
        for (int i = 0; i < 400 && vm.Notice.HasBanner; i++) Thread.Sleep(5);
        T.True("reapply was called with the task name", svc.Calls.Contains("reapply Stock ingest (Dad)"));
        T.True("the banner is gone and the form is editable again", !vm.Notice.HasBanner && !vm.Notice.IsLocked && vm.Notice.FormEnabled);
        T.Eq("status", "Re-applied. The schedule is back to what Cadence saved.", vm.Status);
        T.True("not an error", !vm.StatusIsError);
        T.True("the list was refreshed", host.Refreshed >= 1);
        T.True("the editor stays open", host.Closed == 0);
        vm.Name = "Stock ingest (Dad) 2";
        T.True("Save works again", vm.CanSave);
    }

    public static void AFailedReapplyKeepsTheBannerAndSaysWhy()
    {
        var (vm, svc, dlg, host) = Open(Edited());
        svc.ReapplyResult = Fail("Task Scheduler: access denied");
        dlg.Answer = true;
        vm.Notice.ReapplyAsync().GetAwaiter().GetResult();
        T.Eq("the sentence is shown", "Task Scheduler: access denied", vm.Status);
        T.True("as an error", vm.StatusIsError);
        T.True("still drifted and locked", vm.Notice.HasBanner && vm.Notice.IsLocked);
        T.True("nothing else happened", host.Closed == 0 && host.Refreshed == 0);
        T.True("the editor is not stuck busy", !vm.IsBusy && vm.Notice.ReapplyCommand.CanExecute(null));
    }

    public static void ReapplyIsRefusedForADifferentUserAndForACopy()
    {
        foreach ((string label, ClassifiedTask task) in new[] { ("different user", DifferentUser()), ("copy", Copy()) })
        {
            var (vm, svc, dlg, _) = Open(task);
            dlg.Answer = true;
            T.True(label + ": no Re-apply button", !vm.Notice.ShowReapply);
            T.True(label + ": the command can't run", !vm.Notice.ReapplyCommand.CanExecute(null));
            vm.Notice.ReapplyAsync().GetAwaiter().GetResult();
            T.True(label + ": nothing asked, nothing written", dlg.Asked.Count == 0 && svc.Calls.Count(c => c.StartsWith("reapply", StringComparison.Ordinal)) == 0);
            T.True(label + ": Stop managing and Delete are there", vm.Notice.ShowUntrack && vm.Notice.ShowBannerDelete);
        }
    }

    // ---- Stop managing ----

    public static void StopManagingAsksThenLeavesTheEditor()
    {
        var (vm, svc, dlg, host) = Open(Edited());
        svc.AfterUntrack = name => svc.Entries.RemoveAll(e => e.Task.Observed.Name == name);

        dlg.Answer = false;
        vm.Notice.UntrackAsync().GetAwaiter().GetResult();
        T.True("declined: nothing written, still open", !svc.Calls.Any(c => c.StartsWith("untrack", StringComparison.Ordinal)) && host.Closed == 0);
        (string title, string message, string confirm, bool danger, _) = dlg.Asked[0];
        T.Eq("title", "Stop managing \"Stock ingest (Dad)\"?", title);
        T.True("the button", confirm == "Stop managing" && danger);
        T.True("the consequences", message.Contains("A console window will appear each time it runs.", StringComparison.Ordinal) && message.Contains("It will run on holidays too.", StringComparison.Ordinal));

        dlg.Answer = true;
        vm.Notice.UntrackAsync().GetAwaiter().GetResult();
        T.True("untrack was called", svc.Calls.Contains("untrack Stock ingest (Dad)"));
        T.Eq("the editor closed", 1, host.Closed);
        T.True("a toast says the task stays", host.Toasts.Any(t => t.Contains("no longer manages", StringComparison.Ordinal) && t.Contains("stays in Task Scheduler", StringComparison.Ordinal)));
        T.True("the list was refreshed first", host.Refreshed >= 1);
    }

    public static void AFailedStopManagingStaysOpenWithTheError()
    {
        var (vm, svc, dlg, host) = Open(Edited());
        svc.UntrackResult = Fail("The task is still managed by Cadence after the change.");
        dlg.Answer = true;
        vm.Notice.UntrackAsync().GetAwaiter().GetResult();
        T.True("error shown", vm.StatusIsError && vm.Status!.Contains("still managed", StringComparison.Ordinal));
        T.True("not closed", host.Closed == 0 && vm.Notice.HasBanner);
    }

    public static void StopManagingIsAlsoOfferedForAnInSyncJobButNotForANewerOrDamagedOne()
    {
        var (vm, _, _, _) = Open(Classify(Stock()));
        T.True("in sync: yes (the ⋯ menu)", vm.Notice.CanUntrack && vm.Notice.UntrackCommand.CanExecute(null));

        var (newer, _, _, _) = Open(Classify(EditMeta(Stock(), m => m with { Gen = m.Gen + 1 })));
        T.True("newer version: no", !newer.Notice.CanUntrack && !newer.Notice.CanDuplicate && !newer.Notice.CanReapply);
    }

    // ---- different user, copy, newer version ----

    public static void ADifferentUserOffersStopManagingAndDelete()
    {
        var (vm, svc, dlg, host) = Open(DifferentUser());
        T.Eq("title", "This schedule now runs as a different user.", vm.Notice.BannerTitle);
        T.True("the note explains the missing button", vm.Notice.BannerNote!.Contains("would make the schedule run as you", StringComparison.Ordinal));
        dlg.Answer = true;
        vm.DeleteAsync().GetAwaiter().GetResult();
        T.True("Delete works", svc.Calls.Contains("delete Stock ingest (Dad)") && host.Closed == 1);
    }

    public static void ACopyExplainsItselfAndDeletingItKeepsTheOriginal()
    {
        var svc = new ScriptedServices();
        ClassifiedTask original = Classify(Stock());
        ClassifiedTask copy = Copy();
        svc.Entries.Add(Entry(original, Run(Now.AddHours(-2), RunStatus.Ok, 12)));
        svc.Entries.Add(Entry(copy));
        var dlg = new RecordingDialogs { Answer = true };
        var host = new RecordingHost();
        EditorViewModel vm = EditorViewModel.ForEntry(svc, dlg, host.Value, svc.Entries[1]);

        T.Eq("title", "This is a copy of \"Stock ingest (Dad)\" that was made outside Cadence.", vm.Notice.BannerTitle);
        T.True("locked, and no runs of its own", vm.Notice.IsLocked && vm.Runs.RunRows.Count == 0);
        vm.Runs.RefreshRunsAsync().GetAwaiter().GetResult();
        T.True("a refresh keeps looking at the copy, not at the original that shares its id", vm.Notice.BannerTitle!.Contains("copy", StringComparison.Ordinal) && vm.Runs.RunRows.Count == 0);

        vm.DeleteAsync().GetAwaiter().GetResult();
        T.True("the confirmation says the original is untouched", dlg.Asked[0].Message.Contains("original schedule and its run history are not touched", StringComparison.Ordinal), dlg.Asked[0].Message);
        T.True("only the copy was deleted", svc.Calls.Contains("delete Stock ingest (copy)") && svc.Entries.Count == 1 && svc.Entries[0].Task.Observed.Name == StockName);
    }

    public static void ANewerVersionJobIsReadOnlyWithAWayToLookAtIt()
    {
        var (vm, _, _, host) = Open(Classify(EditMeta(Stock(), m => m with { Gen = m.Gen + 1 })));
        T.True("banner", vm.Notice.HasBanner && vm.Notice.BannerTitle == "This schedule was made by a newer version of Cadence.");
        T.True("only Open in Task Scheduler", vm.Notice.ShowBannerOpen && !vm.Notice.ShowReapply && !vm.Notice.ShowUntrack && !vm.Notice.ShowBannerDelete);
        T.True("locked", vm.Notice.IsLocked && !vm.CanSave && !vm.Notice.FormEnabled);
        vm.OpenInTaskSchedulerCommand.Execute(null);
        T.Eq("the host is told the path", "\\Cadence\\Stock ingest (Dad)", host.TaskSchedulerPaths.Single());
    }

    // ---- Outdated ----

    public static void OutdatedIsAQuietLineAndRefreshNowRewritesTheStoredJob()
    {
        var (vm, svc, _, host) = Open(Classify(EditMeta(Stock(), m => m with { Gen = 0 })));
        T.True("a quiet line, not a banner", vm.Notice.HasOutdated && !vm.Notice.HasBanner);
        T.True("the form stays editable", !vm.Notice.IsLocked && vm.Notice.FormEnabled);
        T.True("the wording", vm.Notice.OutdatedLine!.StartsWith("This task needs a quick refresh (", StringComparison.Ordinal));
        T.True("the link is available", vm.Notice.CanRefreshNow);

        vm.Name = "Edited but not saved";
        svc.AfterSave = name => Replace(svc, name, Classify(Stock()));
        vm.Notice.RefreshNowAsync().GetAwaiter().GetResult();
        T.True("saved under the same task name", svc.Calls.Contains("save Stock ingest (Dad)"));
        T.Eq("what was written is the stored job, not the unsaved edit", "Stock ingest (Dad)", svc.LastSaved!.Name);
        T.True("the line is gone", !vm.Notice.HasOutdated && vm.Status == "Refreshed. The task is up to date.");
        T.True("list refreshed", host.Refreshed >= 1);
    }

    // ---- the ⋯ menu ----

    public static void TheMenuOpensClosesAndEscapeClosesIt()
    {
        var (vm, _, _, _) = Open(Classify(Stock()));
        T.True("closed at first", !vm.IsMenuOpen);
        vm.ToggleMenuCommand.Execute(null);
        T.True("open", vm.IsMenuOpen);
        T.True("Escape closes it first", vm.HandleEscape() && !vm.IsMenuOpen);
        T.True("a second Escape has nothing to close", !vm.HandleEscape());
        vm.ToggleMenuCommand.Execute(null);
        vm.CloseMenuCommand.Execute(null);
        T.True("closed by the click-away", !vm.IsMenuOpen);

        var svc = new ScriptedServices();
        var fresh = EditorViewModel.ForTemplate(svc, new RecordingDialogs(), new RecordingHost().Value, Cadence.Core.Text.Templates.All.First(t => t.Id == "blank"));
        T.True("a schedule that isn't saved has no menu", !fresh.HasMenu);
    }

    public static void DuplicateOpensAnUnsavedCopyWithANewIdAndName()
    {
        var (vm, svc, dlg, host) = Open(Classify(Stock()));
        vm.IsMenuOpen = true;
        vm.Notice.DuplicateAsync().GetAwaiter().GetResult();
        JobDefinition copy = host.Drafts.Single();
        T.True("menu closed", !vm.IsMenuOpen);
        T.True("a new, valid id", copy.Id != "d4dst0ck" && JobId.IsValid(copy.Id));
        T.Eq("name", "Stock ingest (Dad) (copy)", copy.Name);
        T.Eq("same schedule", ComposeSamples.StockIngest().Schedule, copy.Schedule);
        T.Eq("same program", ComposeSamples.StockIngest().Run, copy.Run);
        T.Eq("nothing asked (the form was clean)", 0, dlg.Asked.Count);
        T.True("nothing written", !svc.Calls.Any(c => c.StartsWith("save", StringComparison.Ordinal)));

        // A form with unsaved edits asks first, and the copy comes from the saved version.
        vm.Name = "Edited only here";
        dlg.Answer = false;
        vm.Notice.DuplicateAsync().GetAwaiter().GetResult();
        T.True("declined: no second copy", host.Drafts.Count == 1 && dlg.Asked.Count == 1 && dlg.Asked[0].Title == "Duplicate the saved version?");
        dlg.Answer = true;
        vm.Notice.DuplicateAsync().GetAwaiter().GetResult();
        T.True("accepted: a copy of the SAVED version", host.Drafts.Count == 2 && host.Drafts[1].Name == "Stock ingest (Dad) (copy)");
        T.True("each copy has its own id", host.Drafts[1].Id != host.Drafts[0].Id);
    }

    public static void OpenInTaskSchedulerPassesThePathAndNeverChangesAnything()
    {
        var (vm, svc, _, host) = Open(Classify(Stock()));
        vm.IsMenuOpen = true;
        vm.OpenInTaskSchedulerCommand.Execute(null);
        T.True("menu closed", !vm.IsMenuOpen);
        T.Eq("path", "\\Cadence\\Stock ingest (Dad)", host.TaskSchedulerPaths.Single());
        T.True("no task was written or run", svc.Calls.All(c => c == "load" || c.StartsWith("history", StringComparison.Ordinal)));
    }

    public static void StopManagingFromTheMenuUsesTheSameConfirmation()
    {
        var (vm, svc, dlg, host) = Open(Classify(Stock()));
        svc.AfterUntrack = name => svc.Entries.RemoveAll(e => e.Task.Observed.Name == name);
        vm.IsMenuOpen = true;
        dlg.Answer = true;
        vm.Notice.UntrackCommand.Execute(null);
        for (int i = 0; i < 400 && host.Closed == 0; i++) Thread.Sleep(5);
        T.True("menu closed, confirmation shown, untracked, editor closed", !vm.IsMenuOpen && dlg.Asked.Count == 1 && svc.Calls.Contains("untrack Stock ingest (Dad)") && host.Closed == 1);
    }

    public static void TheLockedProgramMessageMatchesTheState()
    {
        var (edited, _, _, _) = Open(Edited());
        T.True("edited: asks for the decision", !edited.Program.UseFile(@"C:\Tools\other.exe") && edited.Program.ProgramMessage!.Contains("changed outside Cadence", StringComparison.Ordinal));
        var (copy, _, _, _) = Open(Copy());
        T.True("copy: generic read-only message", !copy.Program.UseFile(@"C:\Tools\other.exe") && copy.Program.ProgramMessage!.Contains("read-only", StringComparison.Ordinal));
    }
}
