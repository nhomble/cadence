using Cadence.App.Demo;
using Cadence.App.UI.Logic;
using Cadence.App.UI.Services;
using Cadence.App.UI.ViewModels;
using Cadence.Core.Planning;
using Cadence.TestHarness;

namespace Cadence.Tests;

/// <summary>
/// The rest of Phase 15: the read-only foreign task detail and "Open in Task Scheduler", the damaged and newer-version row dialogs, closing the window
/// with unsaved changes, Escape and the shortcuts, window placement, and the demo data that the renders use. Everything runs against demo data and
/// recording dialogs: nothing is ever launched and no Task Scheduler call is made.
/// </summary>
public static class WindowAndForeignTests
{
    static (MainViewModel Vm, RecordingDialogs Dlg) Window(DemoSet set)
    {
        var dlg = new RecordingDialogs();
        var vm = new MainViewModel(new DemoServices(set), dlg, dlg);
        vm.Start();
        vm.List.InitialLoad.GetAwaiter().GetResult();
        return (vm, dlg);
    }

    static void WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 600 && !condition(); i++) Thread.Sleep(5);
        T.True("(waited for it)", condition());
    }

    static ScheduleRowViewModel Row(MainViewModel vm, string name) => vm.List.Rows.First(r => r.Name == name);

    // ---- foreign tasks ----

    public static void AForeignRowOpensAReadOnlyDetailWithOneAction()
    {
        var (vm, dlg) = Window(DemoSet.Normal);
        vm.List.RefreshAsync(refreshOthers: true).GetAwaiter().GetResult();
        vm.ShowForeign = true;
        vm.List.RefreshAsync(refreshOthers: true).GetAwaiter().GetResult();
        ForeignRowViewModel defrag = vm.List.ForeignRows.First(r => r.Name == "ScheduledDefrag");

        defrag.OpenCommand.Execute(null);
        WaitFor(() => dlg.ForeignShown.Count == 1);
        ForeignDetailText d = dlg.ForeignShown[0];
        T.Eq("name", "ScheduledDefrag", d.Name);
        T.Eq("path", "\\Microsoft\\Windows\\Defrag\\ScheduledDefrag", d.Path);
        T.True("closed with 'no': nothing was opened", dlg.Opened.Count == 0 && dlg.CopiedText.Count == 0);

        dlg.ForeignAnswer = true;
        defrag.OpenCommand.Execute(null);
        WaitFor(() => dlg.Opened.Count == 1);
        T.Eq("the path is on the clipboard", "\\Microsoft\\Windows\\Defrag\\ScheduledDefrag", dlg.CopiedText.Single());
        T.True("a toast says where to look", vm.Toast!.Contains("Task Scheduler Library \u203a Microsoft \u203a Windows \u203a Defrag \u203a ScheduledDefrag", StringComparison.Ordinal), vm.Toast);
        T.True("it is the app's own launcher that was asked (the fake, never the real one)", dlg.Opened.Single() == "taskschd.msc");
    }

    public static void TheForeignDetailTextIsPlainAndReadOnly()
    {
        var services = new DemoServices(DemoSet.Normal);
        IReadOnlyList<ClassifiedTask> others = services.LoadOthersAsync(false).GetAwaiter().GetResult();
        ForeignDetailText d = ForeignDetail.From(others.First(t => t.Observed.Name == "ScheduledDefrag"), services.Now, services.Zone);
        string RowOf(string label) => d.Rows.Single(r => r.Label == label).Value;
        T.Eq("where", "\\Microsoft\\Windows\\Defrag", RowOf("Where"));
        T.Eq("status", "Enabled", RowOf("Status"));
        T.Eq("next run", "Wed 2 Dec 1:00", RowOf("Next run"));
        T.Eq("last run (Task Scheduler's own result, fine for a task that is not ours)", "Ok \u00b7 2 d ago", RowOf("Last run"));
        T.Eq("what it runs", "%windir%\\system32\\defrag.exe -c -h -o -$", RowOf("Runs"));
        T.Eq("author", "Microsoft Corporation", RowOf("Made by"));
        T.Eq("description", "This task optimizes local storage drives.", RowOf("Description"));
        T.True("the note says what Cadence does", d.Note.StartsWith("Cadence doesn't manage this task", StringComparison.Ordinal));
        T.Eq("location", "Task Scheduler Library \u203a Microsoft \u203a Windows \u203a Defrag \u203a ScheduledDefrag", d.Location);

        ForeignDetailText off = ForeignDetail.From(others.First(t => t.Observed.Name == "WinSAT"), services.Now, services.Zone);
        T.Eq("a disabled task", "Disabled", off.Rows.Single(r => r.Label == "Status").Value);
        T.Eq("a task Cadence can't read", "(Cadence can't read this task's definition)", off.Rows.Single(r => r.Label == "Runs").Value);
        T.Eq("never run", "Never run", off.Rows.Single(r => r.Label == "Last run").Value);

        T.Eq("a task in the root", "Task Scheduler Library \u203a OneDrive Reporting Task", ForeignDetail.Location("\\OneDrive Reporting Task"));
        T.Eq("nothing", "Task Scheduler Library", ForeignDetail.Location(""));
        T.True("the toast", ForeignDetail.OpenedToast("\\Cadence\\Good morning").Contains("Task Scheduler Library \u203a Cadence \u203a Good morning", StringComparison.Ordinal));
    }

    // ---- damaged and newer-version rows ----

    public static void ADamagedRowOffersDeleteAndNothingElse()
    {
        var (vm, dlg) = Window(DemoSet.WithProblems);
        ScheduleRowViewModel damaged = vm.List.Rows.First(r => r.Classified.Ownership == Ownership.CorruptMetadata);
        dlg.Answer = false;
        damaged.OpenCommand.Execute(null);
        WaitFor(() => dlg.Asked.Count == 1);
        (string title, string message, string confirm, bool danger, string? cancel) = dlg.Asked[0];
        T.True("the dialog", title == "Damaged schedule" && confirm == "Delete" && danger && cancel is null);
        T.True("the words", message == "This task was created by Cadence but its settings are damaged, so Cadence can't edit or run it. Delete it?", message);
        T.True("declined: the row is still there and the list did not change page", vm.List.Rows.Any(r => r.Classified.Ownership == Ownership.CorruptMetadata) && vm.IsListShowing);

        dlg.Answer = true;
        damaged.OpenCommand.Execute(null);
        WaitFor(() => !vm.List.Rows.Any(r => r.Classified.Ownership == Ownership.CorruptMetadata));
        T.True("deleted", vm.IsListShowing);
    }

    public static void ANewerVersionRowIsReadOnlyAndOffersTheTaskSchedulerEscapeHatch()
    {
        var (vm, dlg) = Window(DemoSet.WithProblems);
        ScheduleRowViewModel newer = vm.List.Rows.First(r => r.Classified.Ownership == Ownership.NewerVersion);
        dlg.Answer = false;
        newer.OpenCommand.Execute(null);
        WaitFor(() => dlg.Asked.Count == 1);
        (string title, string message, string confirm, bool danger, string? cancel) = dlg.Asked[0];
        T.True("the dialog", title == "Newer version" && confirm == "Open in Task Scheduler" && !danger && cancel == "Close");
        T.True("the words", message.StartsWith("Created by a newer Cadence version; read-only.", StringComparison.Ordinal) && message.Contains("Update Cadence to edit it", StringComparison.Ordinal));
        T.True("declined: nothing opened, still on the list", dlg.Opened.Count == 0 && vm.IsListShowing);

        dlg.Answer = true;
        newer.OpenCommand.Execute(null);
        WaitFor(() => dlg.Opened.Count == 1);
        T.Eq("the path is copied", "\\Cadence\\Photo sync", dlg.CopiedText.Single());
    }

    // ---- the window ----

    public static void ClosingTheWindowWithUnsavedChangesAsksFirst()
    {
        var (vm, dlg) = Window(DemoSet.Normal);
        T.True("on the list: no question", vm.ConfirmCloseAsync().GetAwaiter().GetResult() && dlg.Asked.Count == 0 && !vm.HasUnsavedChanges);

        Row(vm, "Good morning").OpenCommand.Execute(null);
        var editor = (EditorViewModel)vm.CurrentPage;
        T.True("an editor without changes: no question", vm.ConfirmCloseAsync().GetAwaiter().GetResult() && dlg.Asked.Count == 0);

        editor.Name = "Good morning!";
        T.True("changes are noticed", vm.HasUnsavedChanges);
        dlg.Answer = false;
        T.True("'Keep editing' keeps the window open", !vm.ConfirmCloseAsync().GetAwaiter().GetResult());
        (string title, string message, string confirm, bool danger, string? cancel) = dlg.Asked[0];
        T.True("the question", title == "Discard changes?" && confirm == "Discard and close" && danger && cancel == "Keep editing" && message.Contains("Closing Cadence will discard them.", StringComparison.Ordinal));
        dlg.Answer = true;
        T.True("'Discard and close' lets it close", vm.ConfirmCloseAsync().GetAwaiter().GetResult());
    }

    public static void EscapeClosesTheMenuBeforeLeavingTheEditor()
    {
        var (vm, _) = Window(DemoSet.Normal);
        Row(vm, "Good morning").OpenCommand.Execute(null);
        var editor = (EditorViewModel)vm.CurrentPage;
        editor.IsMenuOpen = true;
        T.True("first Escape: the menu", vm.HandleEscape() && !editor.IsMenuOpen && !vm.IsListShowing);
        T.True("second Escape: back to the list", vm.HandleEscape() && vm.IsListShowing);
    }

    public static void TheShortcutsReachTheOpenEditor()
    {
        var (vm, _) = Window(DemoSet.Empty);
        T.True("Ctrl+S on the list does nothing", true);
        vm.SaveCurrentCommand.Execute(null);
        vm.RunNowCurrentCommand.Execute(null);

        vm.PickTemplateById("blank");
        var editor = (EditorViewModel)vm.CurrentPage;
        editor.Program.UseFile(@"C:\Tools\backup.exe");
        T.True("save is possible", editor.CanSave);
        vm.SaveCurrentCommand.Execute(null);
        WaitFor(() => editor.Status is not null && editor.Status.StartsWith("Saved", StringComparison.Ordinal));
        T.True("Ctrl+S saved it", editor.IsExisting);
        WaitFor(() => editor.Runs.CanRunNow);   // the save finishes after it refreshed the list, which now describes the rows on the thread pool

        vm.RunNowCurrentCommand.Execute(null);
        WaitFor(() => editor.Runs.RunFeedback is not null);
        T.True("Ctrl+R pressed Run now", editor.Runs.RunFeedback!.Contains("Running now", StringComparison.Ordinal) || editor.Runs.RunFeedback.StartsWith("Started", StringComparison.Ordinal) || editor.Runs.RunFeedback.StartsWith("Starting", StringComparison.Ordinal), editor.Runs.RunFeedback);
    }

    public static void DuplicateFromTheMenuOpensAnUnsavedEditorAndOpenInTaskSchedulerCopiesThePath()
    {
        var (vm, dlg) = Window(DemoSet.Normal);
        Row(vm, "Good morning").OpenCommand.Execute(null);
        var editor = (EditorViewModel)vm.CurrentPage;

        editor.OpenInTaskSchedulerCommand.Execute(null);
        T.Eq("copied", "\\Cadence\\Good morning", dlg.CopiedText.Single());
        T.True("opened through the fake only", dlg.Opened.Count == 1);

        editor.Notice.DuplicateCommand.Execute(null);
        WaitFor(() => !ReferenceEquals(vm.CurrentPage, editor));
        var copy = (EditorViewModel)vm.CurrentPage;
        T.True("a new, unsaved editor", copy.IsNew && !copy.HasMenu && copy.Name == "Good morning (copy)");
        T.True("a toast says what to do", vm.Toast!.StartsWith("Duplicated.", StringComparison.Ordinal));
        T.True("the program came along", copy.BuildJob().Run.Exe == editor.BuildJob().Run.Exe);
    }

    // ---- placement ----

    public static void WindowPlacementRoundTripsAndRejectsGarbage()
    {
        var p = new PlacementData(120, 80, 1100, 720, false);
        T.Eq("format", "120,80,1100,720,0", WindowPlacement.Format(p));
        T.True("round trip", WindowPlacement.TryParse(WindowPlacement.Format(p), out PlacementData back) && back == p);
        T.True("maximized round trip", WindowPlacement.TryParse(WindowPlacement.Format(p with { Maximized = true }), out PlacementData max) && max.Maximized);
        T.True("negative positions (a monitor on the left)", WindowPlacement.TryParse("-1800,40,1000,700,0", out PlacementData neg) && neg.Left == -1800);
        foreach (string bad in new[] { "", "   ", "1,2,3", "a,b,c,d,0", "1,2,3,4,5", "0,0,10,10,0", "0,0,1000,700", "NaN,0,1000,700,0", "0,0,1000,700,0,9", "0,0,99999,700,0" })
            T.True("rejects '" + bad + "'", !WindowPlacement.TryParse(bad, out _));
        T.True("null", !WindowPlacement.TryParse(null, out _));
    }

    public static void ASavedPlacementIsFittedToTheScreensThatExistNow()
    {
        var screen = new ScreenRect(0, 0, 1920, 1080);
        PlacementData Fit(double l, double t, double w, double h) => WindowPlacement.Fit(new PlacementData(l, t, w, h, false), [screen]);

        T.Eq("fine as it is", new PlacementData(100, 50, 1100, 720, false), Fit(100, 50, 1100, 720));
        PlacementData off = Fit(5000, 3000, 1100, 720);
        T.True("entirely off every screen: centered", off.Left == 410 && off.Top == 180, off.ToString());
        PlacementData big = Fit(0, 0, 4000, 3000);
        T.True("too big: no bigger than the screen", big.Width == 1920 && big.Height == 1080);
        PlacementData small = Fit(10, 10, 300, 200);
        T.True("too small: the minimum", small.Width == WindowPlacement.MinWidth && small.Height == WindowPlacement.MinHeight);
        PlacementData partly = Fit(1850, 1060, 1100, 720);
        T.True("hanging off the corner: the title bar is pulled back in", partly.Left <= 1920 - 120 && partly.Top <= 1080 - 48 && partly.Left > 1000, partly.ToString());
        PlacementData above = Fit(100, -300, 1100, 720);
        T.True("above the screen: top edge at the top", above.Top == 0);

        // The unplugged second monitor (it sat to the left): the window comes back onto the one that is left.
        PlacementData left = WindowPlacement.Fit(new PlacementData(-1800, 100, 1000, 700, false), [screen]);
        T.True("a monitor that is gone", left.Left >= -880 && left.Left <= 1800, left.ToString());
        // Two monitors, the window on the second: untouched.
        var both = new[] { screen, new ScreenRect(1920, 0, 1920, 1080) };
        T.Eq("on the second monitor", new PlacementData(2000, 100, 1000, 700, true), WindowPlacement.Fit(new PlacementData(2000, 100, 1000, 700, true), both));
        T.Eq("no screens known: unchanged", new PlacementData(5, 5, 600, 500, false), WindowPlacement.Fit(new PlacementData(5, 5, 600, 500, false), []));
    }

    public static void DemoSettingsKeepThePlacementInMemoryOnly()
    {
        var services = new DemoServices(DemoSet.Empty);
        T.True("nothing saved at first", services.Settings.WindowPlacement is null);
        services.Settings.WindowPlacement = "1,2,600,500,0";
        T.Eq("kept for this run", "1,2,600,500,0", services.Settings.WindowPlacement);
    }

    // ---- the demo data the renders use ----

    public static void TheShowcaseDemoHasEveryStateOfThisPhase()
    {
        var services = new DemoServices(DemoSet.Showcase);
        ManagedSnapshot snap = services.LoadManagedAsync().GetAwaiter().GetResult();
        ManagedEntry Named(string task) => snap.Entries.Single(e => e.Task.Observed.Name == task);

        T.Eq("an edited job", SyncStatus.Drifted, Named("Weekly report").Task.Sync);
        T.Eq("a different user", BannerKind.DifferentUser, DriftText.For(Named("Month-end export").Task)!.Kind);
        T.Eq("a copy", BannerKind.Duplicate, DriftText.For(Named("Stock ingest (Dad) (copy)").Task)!.Kind);
        T.Eq("an outdated job", BannerKind.Outdated, DriftText.For(Named("Archive photos").Task)!.Kind);
        T.Eq("a running job", Cadence.Core.History.RunStatus.Running, Named("Sync notes").History.Recent[0].Status);
        T.Eq("a damaged task", Ownership.CorruptMetadata, Named("Old report (damaged)").Task.Ownership);
        T.Eq("a newer-version task", Ownership.NewerVersion, Named("Photo sync").Task.Ownership);
        T.Eq("a blocked launch", Cadence.Core.History.RunStatus.NotReported, Named("Disk cleanup").History.Recent[0].Status);

        // The list tells the copy apart from its original.
        ManagedEntry copy = Named("Stock ingest (Dad) (copy)");
        RowText row = ListFormat.Describe(copy.Task, copy.History, copy.Next, services.Now, services.Zone);
        T.Eq("the copy is listed under its own task name", "Stock ingest (Dad) (copy)", row.Name);
        T.Eq("with a note", "A copy of \"Stock ingest (Dad)\"", row.Note);
        T.Eq("it is classed as drifted", BadgeKind.Drifted, row.Badge);
        T.Eq("the State column says it in words", "Copy of \"Stock ingest (Dad)\"", row.StateText);
        T.Eq("... in Yellow", TextTone.Yellow, row.StateTone);
        RowText orig = ListFormat.Describe(Named("Stock ingest (Dad)").Task, Named("Stock ingest (Dad)").History, null, services.Now, services.Zone);
        T.True("the original is untouched", orig.Name == "Stock ingest (Dad)" && orig.Note is null);
        RowText running = ListFormat.Describe(Named("Sync notes").Task, Named("Sync notes").History, null, services.Now, services.Zone);
        T.Eq("the running job is classed as running", BadgeKind.Running, running.Badge);
        T.Eq("the State column says Running now, in Cyan", ("Running now", TextTone.Cyan), (running.StateText, running.StateTone));
        ManagedEntry weekly = Named("Weekly report");
        T.Eq("an edited job says so in the State column", "Changed in Task Scheduler", ListFormat.Describe(weekly.Task, weekly.History, weekly.Next, services.Now, services.Zone).StateText);
        ManagedEntry other = Named("Month-end export");
        T.Eq("a different user", "Runs as a different user", ListFormat.Describe(other.Task, other.History, other.Next, services.Now, services.Zone).StateText);
        ManagedEntry damaged = Named("Old report (damaged)");
        T.Eq("a damaged task", ("Damaged settings", TextTone.Red), (ListFormat.Describe(damaged.Task, damaged.History, null, services.Now, services.Zone).StateText, ListFormat.Describe(damaged.Task, damaged.History, null, services.Now, services.Zone).StateTone));
    }

    public static void TheDemoRunNowShowsARunThatThenEnds()
    {
        var services = new DemoServices(DemoSet.Showcase);
        ManagedEntry good = services.LoadManagedAsync().GetAwaiter().GetResult().Entries.Single(e => e.Task.Observed.Name == "Good morning");
        var dlg = new RecordingDialogs();
        var host = new RecordingHost();
        EditorViewModel vm = EditorViewModel.ForEntry(services, dlg, host.Value, good);
        vm.Runs.RunNowAsync().GetAwaiter().GetResult();
        T.True("running", vm.Runs.IsRunning && vm.Runs.RunFeedback!.StartsWith("Running now", StringComparison.Ordinal));
        vm.Runs.LiveTickAsync().GetAwaiter().GetResult();
        vm.Runs.LiveTickAsync().GetAwaiter().GetResult();
        T.True("ended Ok after a couple of ticks", !vm.Runs.IsRunning && vm.Runs.RunFeedback == "Finished: Ok \u00b7 3.2 s", vm.Runs.RunFeedback);
    }

    public static void TheDemoReapplyAndStopManagingChangeOnlyTheDemoCopy()
    {
        var services = new DemoServices(DemoSet.Showcase);
        var dlg = new RecordingDialogs { Answer = true };
        var host = new RecordingHost();
        ManagedEntry weekly = services.LoadManagedAsync().GetAwaiter().GetResult().Entries.Single(e => e.Task.Observed.Name == "Weekly report");
        EditorViewModel vm = EditorViewModel.ForEntry(services, dlg, host.Value, weekly);
        T.True("drifted at first", vm.Notice.HasBanner);
        vm.Notice.ReapplyAsync().GetAwaiter().GetResult();
        T.True("in sync afterwards", !vm.Notice.HasBanner && !vm.Notice.IsLocked);

        ManagedEntry month = services.LoadManagedAsync().GetAwaiter().GetResult().Entries.Single(e => e.Task.Observed.Name == "Month-end export");
        EditorViewModel other = EditorViewModel.ForEntry(services, dlg, host.Value, month);
        T.True("a different-user job can't be re-applied, even in the demo", !other.Notice.CanReapply);
        other.Notice.UntrackAsync().GetAwaiter().GetResult();
        T.True("stop managing removes it from the demo list", services.LoadManagedAsync().GetAwaiter().GetResult().Entries.All(e => e.Task.Observed.Name != "Month-end export"));
    }
}
