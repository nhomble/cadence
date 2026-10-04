using Cadence.App.Demo;
using Cadence.App.UI.Logic;
using Cadence.App.UI.ViewModels;
using Cadence.TestHarness;

namespace Cadence.Tests;

/// <summary>[P25] The view-model side of the home keyboard map: what Enter, Del, Ctrl+F and Ctrl+N call, and the window title and status text the window shows.</summary>
public static class HomeKeyboardTests
{
    static (MainViewModel Vm, RecordingDialogs Dlg) Window(DemoSet set = DemoSet.Normal)
    {
        var dlg = new RecordingDialogs();
        var vm = new MainViewModel(new DemoServices(set), dlg, dlg);
        vm.Start();
        vm.InitialLoad.GetAwaiter().GetResult();
        return (vm, dlg);
    }

    static void WaitFor(string what, Func<bool> condition)
    {
        // The rows are rebuilt on a thread-pool continuation here (no window, no dispatcher), so a read may land in the middle of it: look again.
        bool Holds()
        {
            try { return condition(); }
            catch (InvalidOperationException) { return false; }
        }
        for (int i = 0; i < 600 && !Holds(); i++) Thread.Sleep(5);
        T.True(what, Holds());
    }

    public static void EnterOpensTheSelectedScheduleOnlyWhenExactlyOneIsSelected()
    {
        var (vm, _) = Window();
        ListPageViewModel list = vm.List;
        list.SetSelection(list.Rows.Take(2));
        T.True("two selected: Enter does nothing", !list.OpenSelectedCommand.CanExecute(null));
        list.OpenSelectedCommand.Execute(null);
        T.True("still on the list", vm.IsListShowing);

        list.SetSelection(list.Rows.Where(r => r.Name == "Good morning"));
        T.True("one selected: Enter opens", list.OpenSelectedCommand.CanExecute(null));
        list.OpenSelectedCommand.Execute(null);
        WaitFor("the editor shows", () => vm.CurrentPage is EditorViewModel);
        T.Eq("it is that schedule", "Good morning", ((EditorViewModel)vm.CurrentPage).Name);
    }

    public static void ADamagedRowStillOpensItsQuestionFromTheKeyboard()
    {
        var (vm, dlg) = Window(DemoSet.WithProblems);
        vm.List.SetSelection(vm.List.Rows.Where(r => r.IsProblem && r.Name.StartsWith("Old", StringComparison.Ordinal)));
        dlg.Answer = false;
        vm.List.OpenSelectedCommand.Execute(null);
        WaitFor("asked", () => dlg.Asked.Count == 1);
        T.True("it is the damaged-settings question", dlg.Asked[0].Title.Length > 0 && vm.IsListShowing);
    }

    public static void DelDeletesTheSelectionAfterOneQuestion()
    {
        var (vm, dlg) = Window();
        vm.List.SetSelection(vm.List.Rows.Where(r => r.Name == "Disk cleanup"));
        dlg.Answer = true;
        vm.List.DeleteSelectedCommand.Execute(null);
        WaitFor("deleted", () => vm.List.Rows.All(r => r.Name != "Disk cleanup"));
        T.Eq("asked with the name", "Delete \"Disk cleanup\"?", dlg.Asked.Single().Title);
        T.Eq("7 left", 7, vm.List.Rows.Count);
    }

    public static void CtrlFAsksTheViewToFocusTheFilterOnlyOnTheList()
    {
        var (vm, _) = Window();
        int asked = 0;
        vm.List.FocusFilterRequested += (_, _) => asked++;
        vm.FocusFilterCommand.Execute(null);
        T.Eq("the list asks the view", 1, asked);
        vm.List.SetSelection(vm.List.Rows.Take(1));
        vm.List.OpenSelectedCommand.Execute(null);
        WaitFor("editor", () => vm.CurrentPage is EditorViewModel);
        vm.FocusFilterCommand.Execute(null);
        T.Eq("an open editor has no filter to focus", 1, asked);
    }

    public static void CtrlNOnTheListStillOpensTheNewScheduleFlow()
    {
        var (vm, _) = Window();
        vm.NewCommand.Execute(null);
        T.True("the + sheet (until the split editor of Phase 25.4)", vm.IsSheetOpen);
        T.Eq("nine starting points", 9, vm.Cards.Count);
        var (empty, _) = Window(DemoSet.Empty);
        T.True("an empty folder shows the table, not cards", empty.List.IsEmpty && empty.IsListShowing);
        empty.NewCommand.Execute(null);
        T.True("and New still works", empty.IsSheetOpen);
    }

    public static void TheWindowTitleAndStatusTextFollowTheCountsAndTheEditor()
    {
        var (vm, _) = Window();
        T.Eq("list title (plain)", "Cadence - 8 schedules, 4 problems", vm.WindowTitle);
        T.Eq("status text", "8 schedules · 4 problems", vm.StatusText);

        vm.List.SetSelection(vm.List.Rows.Where(r => r.Name == "Stock ingest (Dad)"));
        vm.List.OpenSelectedCommand.Execute(null);
        WaitFor("editor", () => vm.CurrentPage is EditorViewModel);
        var editor = (EditorViewModel)vm.CurrentPage;
        T.Eq("editor title", "Cadence - Stock ingest (Dad)", vm.WindowTitle);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.WindowTitle)) changed.Add(e.PropertyName); };
        editor.Name = "Stock ingest (Mum)";
        T.Eq("an edit makes it unsaved", "Cadence - Stock ingest (Mum) (unsaved)", vm.WindowTitle);
        T.True("and the window is told", changed.Count > 0);
        T.Eq("the status bar stays on the editor", "8 schedules · 4 problems", vm.StatusText);
        vm.BackCommand.Execute(null);

        var (empty, _) = Window(DemoSet.Empty);
        T.Eq("empty: plain title", "Cadence", empty.WindowTitle);
        T.Eq("empty: status", "No schedules", empty.StatusText);
        empty.PickTemplateById("blank");
        T.Eq("a new schedule", "Cadence - New schedule", empty.WindowTitle);
    }

    public static void TheTitleIsPlainWhileTheFirstLoadRuns()
    {
        var dlg = new RecordingDialogs();
        var vm = new MainViewModel(new DemoServices(DemoSet.Normal), dlg, dlg);
        T.Eq("not started", "Cadence", vm.WindowTitle);
        T.Eq("not started: reading", "Reading Task Scheduler…", vm.StatusText);
    }
}
