using Cadence.App.Demo;
using Cadence.App.UI.Logic;
using Cadence.App.UI.ViewModels;
using Cadence.Mvvm;
using Cadence.TestHarness;

namespace Cadence.Tests;

/// <summary>[P25] The list view model behind the home table: sorting by header, the persisted sort, the filter, the selection commands and the selection surviving a refresh.</summary>
public static class ListPageGridTests
{
    static (MainViewModel Vm, RecordingDialogs Dlg, DemoServices Services) Window(DemoSet set = DemoSet.Normal, DemoServices? services = null)
    {
        var dlg = new RecordingDialogs();
        services ??= new DemoServices(set);
        var vm = new MainViewModel(services, dlg, dlg);
        vm.Start();
        vm.List.InitialLoad.GetAwaiter().GetResult();
        return (vm, dlg, services);
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

    static string Names(ListPageViewModel list) => string.Join(", ", list.Rows.Select(r => r.Name));

    static void Select(ListPageViewModel list, params string[] names) =>
        list.SetSelection(list.Rows.Where(r => names.Contains(r.Name)));

    public static void TheTableStartsSortedByRateAndAHeaderClickSorts()
    {
        var (vm, _, _) = Window();
        ListPageViewModel list = vm.List;
        T.Eq("default order", "Sync notes, Photo backup, Nightly backup, YouTube feed download, Stock ingest (Dad), Good morning, Disk cleanup, Weekly report", Names(list));
        T.Eq("default sort", "rate:desc", list.Sort.Format());

        list.SortCommand.Execute(GridColumn.Name);
        T.Eq("Name: A-Z", "Disk cleanup, Good morning, Nightly backup, Photo backup, Stock ingest (Dad), Sync notes, Weekly report, YouTube feed download", Names(list));
        list.SortCommand.Execute(GridColumn.Name);
        T.Eq("Name again: Z-A", "YouTube feed download, Weekly report, Sync notes, Stock ingest (Dad), Photo backup, Nightly backup, Good morning, Disk cleanup", Names(list));
        list.SortCommand.Execute(GridColumn.Rate);
        T.Eq("Rate: back to most frequent first", "Sync notes", list.Rows[0].Name);
        list.SortCommand.Execute(GridColumn.Rate);
        T.Eq("Rate again: least frequent first, the weekly ones lead", "Disk cleanup, Weekly report", string.Join(", ", list.Rows.Take(2).Select(r => r.Name)));
        list.SortCommand.Execute(GridColumn.LastResult);
        T.Eq("Last result: problems first (failed and blocked, then timed out)", "Photo backup, Disk cleanup, Nightly backup", string.Join(", ", list.Rows.Take(3).Select(r => r.Name)));
    }

    public static void TheSortIsRememberedForTheNextStart()
    {
        var services = new DemoServices(DemoSet.Normal);
        var (vm, _, _) = Window(services: services);
        vm.List.SortCommand.Execute(GridColumn.Success);
        T.Eq("stored", "success:asc", services.Settings.ListSort);
        var (again, _, _) = Window(services: services);
        T.Eq("the next window starts with it", "success:asc", again.List.Sort.Format());
        T.Eq("worst first", "Disk cleanup", again.List.Rows[0].Name);

        services.Settings.ListSort = "garbage";
        var (reset, _, _) = Window(services: services);
        T.Eq("garbage means the default", "rate:desc", reset.List.Sort.Format());
    }

    public static void TheFilterShowsMatchesAndTheStatusBarSaysSo()
    {
        var (vm, _, _) = Window();
        ListPageViewModel list = vm.List;
        T.Eq("no filter", "8 schedules · 4 problems", list.StatusText);
        list.FilterText = "backup";
        T.Eq("the matches, in the sort order", "Photo backup, Nightly backup", Names(list));
        T.Eq("the count stays the whole folder's, plus showing", "8 schedules · 4 problems · showing 2", list.StatusText);
        T.True("not empty and not 'no matches'", !list.IsEmpty && !list.NoMatches);
        list.FilterText = "nothing at all";
        T.True("no matches says so", list.NoMatches && list.NoMatchText == "No schedules match \"nothing at all\".");
        T.True("but the table is not 'empty' (that is for a folder without schedules)", !list.IsEmpty);
        list.ClearFilterCommand.Execute(null);
        T.Eq("cleared", 8, list.Rows.Count);
        list.FilterText = "dad stocks";
        T.Eq("two words find the program's path", "Stock ingest (Dad)", Names(list));
    }

    public static void TheToolbarCommandsFollowTheSelection()
    {
        var (vm, _, _) = Window();
        ListPageViewModel list = vm.List;
        T.True("nothing selected: nothing enabled", !list.RunSelectedCommand.CanExecute(null) && !list.DeleteSelectedCommand.CanExecute(null) && !list.OpenSelectedCommand.CanExecute(null));

        Select(list, "Good morning");
        T.True("an enabled schedule: run, disable, delete, open", list.RunSelectedCommand.CanExecute(null) && list.DisableSelectedCommand.CanExecute(null)
            && list.DeleteSelectedCommand.CanExecute(null) && list.OpenSelectedCommand.CanExecute(null));
        T.True("... but not enable", !list.EnableSelectedCommand.CanExecute(null));

        Select(list, "Weekly report");
        T.True("a row changed outside Cadence can only be opened or deleted", !list.RunSelectedCommand.CanExecute(null) && !list.EnableSelectedCommand.CanExecute(null)
            && !list.DisableSelectedCommand.CanExecute(null) && list.DeleteSelectedCommand.CanExecute(null) && list.OpenSelectedCommand.CanExecute(null));

        Select(list, "Good morning", "Weekly report");
        T.True("a mixed selection runs the runnable part", list.RunSelectedCommand.CanExecute(null) && list.DisableSelectedCommand.CanExecute(null));
        T.True("open needs exactly one", !list.OpenSelectedCommand.CanExecute(null));
        T.Eq("two selected", 2, list.SelectedCount);

        // A disabled schedule can be enabled, not run.
        var task = list.Rows.First(r => r.Name == "Good morning").Classified;
        vm.Services.SetEnabledAsync(task, false).GetAwaiter().GetResult();
        list.RefreshAsync().GetAwaiter().GetResult();
        Select(list, "Good morning");
        T.True("disabled: enable yes", list.EnableSelectedCommand.CanExecute(null));
        T.True("disabled: run and disable no", !list.RunSelectedCommand.CanExecute(null) && !list.DisableSelectedCommand.CanExecute(null));
        Select(list);
        T.True("selection cleared: disabled again", !list.EnableSelectedCommand.CanExecute(null));
    }

    public static void EnableAndDisableActOnTheWholeSelectionWithOneToast()
    {
        var (vm, _, _) = Window();
        ListPageViewModel list = vm.List;
        Select(list, "Good morning", "Sync notes", "Weekly report");
        list.DisableSelectedCommand.Execute(null);
        WaitFor("two disabled", () => list.Rows.Count(r => !r.IsEnabled) == 2);
        T.Eq("one toast for two", "Disabled 2 schedules.", vm.Toast);
        T.True("the changed-outside row was left alone", list.Rows.First(r => r.Name == "Weekly report").IsEnabled);
        T.Eq("the selection is still the three", 3, list.SelectedCount);
        T.Eq("the state column says Disabled", "Disabled", list.Rows.First(r => r.Name == "Sync notes").StateText);
        T.Eq("... and the counts follow", "8 schedules · 4 problems · 2 disabled", list.StatusText);

        list.EnableSelectedCommand.Execute(null);
        WaitFor("both enabled again", () => list.Rows.All(r => r.IsEnabled));
        T.Eq("one toast", "Enabled 2 schedules.", vm.Toast);
    }

    public static void DeleteAsksOnceForTheWholeSelectionAndDeletesEach()
    {
        var (vm, dlg, _) = Window();
        ListPageViewModel list = vm.List;
        Select(list, "Good morning", "Sync notes", "Disk cleanup");

        dlg.Answer = false;
        list.DeleteSelectedCommand.Execute(null);
        WaitFor("asked", () => dlg.Asked.Count == 1);
        T.Eq("title", "Delete 3 schedules?", dlg.Asked[0].Title);
        T.True("the names are listed", dlg.Asked[0].Message.Contains("\"Disk cleanup\"", StringComparison.Ordinal) && dlg.Asked[0].Message.Contains("\"Sync notes\"", StringComparison.Ordinal));
        T.True("and it says what is removed", dlg.Asked[0].Message.Contains("deletes their run history", StringComparison.Ordinal));
        T.True("a declined question deletes nothing", list.Rows.Count == 8);

        dlg.Answer = true;
        list.DeleteSelectedCommand.Execute(null);
        WaitFor("deleted", () => list.Rows.Count == 5);
        T.Eq("asked once more, not once per row", 2, dlg.Asked.Count);
        T.Eq("one toast", "Deleted 3 schedules.", vm.Toast);
        T.Eq("the selection is gone with the rows", 0, list.SelectedCount);
    }

    public static void ManyNamesInTheQuestionAreCutAtFive()
    {
        var (vm, _, _) = Window();
        string text = ListPageViewModel.DeleteMessage(vm.List.Rows.Take(8).ToList());
        T.True("five names", text.StartsWith("\"Sync notes\", \"Photo backup\", \"Nightly backup\", \"YouTube feed download\", \"Stock ingest (Dad)\" and 3 more.", StringComparison.Ordinal), text);
        T.True("a single row keeps today's sentence", ListPageViewModel.DeleteMessage([vm.List.Rows[0]]).StartsWith("This removes the schedule from Task Scheduler", StringComparison.Ordinal));
    }

    public static void RunSelectedStartsEachRunnableRowAndToastsOnce()
    {
        var (vm, _, _) = Window();
        ListPageViewModel list = vm.List;
        Select(list, "Good morning", "Sync notes", "Weekly report");
        list.RunSelectedCommand.Execute(null);
        WaitFor("toast", () => vm.Toast is not null && vm.Toast.StartsWith("Started", StringComparison.Ordinal));
        T.Eq("two were runnable", "Started 2 schedules.", vm.Toast);
        WaitFor("the first run finished", () => !((AsyncRelayCommand)list.RunSelectedCommand).IsRunning);
        Select(list, "Sync notes");
        vm.ShowToast("x");
        list.RunSelectedCommand.Execute(null);
        WaitFor("single toast", () => vm.Toast == "Started \"Sync notes\".");
    }

    public static void TheSelectionSurvivesARefreshThatRebuildsTheRows()
    {
        var (vm, _, services) = Window();
        ListPageViewModel list = vm.List;
        Select(list, "Photo backup", "Stock ingest (Dad)");
        ScheduleRowViewModel before = list.Rows.First(r => r.Name == "Photo backup");

        list.RefreshAsync().GetAwaiter().GetResult();
        T.True("a refresh with nothing new keeps the very same rows", ReferenceEquals(before, list.Rows.First(r => r.Name == "Photo backup")));
        T.Eq("... and the selection", "Photo backup, Stock ingest (Dad)", string.Join(", ", list.SelectedRows.Select(r => r.Name)));

        // Something else changed: every row is rebuilt, and the selection is found again by task name.
        var other = list.Rows.First(r => r.Name == "Good morning").Classified;
        services.SetEnabledAsync(other, false).GetAwaiter().GetResult();
        int restored = 0;
        list.SelectionRestoreRequested += (_, _) => restored++;
        list.RefreshAsync().GetAwaiter().GetResult();
        T.True("the rows were rebuilt", !ReferenceEquals(before, list.Rows.First(r => r.Name == "Photo backup")));
        T.Eq("the selection is still those two", "Photo backup, Stock ingest (Dad)", string.Join(", ", list.SelectedRows.Select(r => r.Name)));
        T.True("the view was asked to select them again", restored >= 1);
        T.True("and they are rows of the new table", list.SelectedRows.All(r => list.Rows.Contains(r)));

        // The view's own selection changes while it rebuilds are not the user's: they must not clear the selection.
        list.SetSelection([]);
        T.Eq("an explicit clear does clear", 0, list.SelectedCount);
    }

    public static void ARefreshWithNothingNewCountsNoRates()
    {
        var (vm, _, _) = Window();
        int counted = vm.List.RateComputations;
        T.True("the first load counted every schedule once", counted >= 6, counted.ToString(System.Globalization.CultureInfo.InvariantCulture));
        vm.List.RefreshAsync().GetAwaiter().GetResult();
        vm.List.RefreshAsync().GetAwaiter().GetResult();
        T.Eq("two more refreshes count nothing", counted, vm.List.RateComputations);
    }

    public static void TheColumnMenuHidesAColumnAndRemembersIt()
    {
        var services = new DemoServices(DemoSet.Normal);
        var (vm, _, _) = Window(services: services);
        int changed = 0;
        vm.List.ColumnsChanged += (_, _) => changed++;
        vm.List.ToggleColumnCommand.Execute(GridColumn.Updated);
        T.True("hidden", vm.List.HiddenColumns.Contains(GridColumn.Updated) && changed == 1);
        T.Eq("stored", "updated", services.Settings.ListHiddenColumns);
        vm.List.ToggleColumnCommand.Execute(GridColumn.Name);
        T.Eq("Name can not be hidden", 1, vm.List.HiddenColumns.Count);
        var (again, _, _) = Window(services: services);
        T.True("the next window hides it too", again.List.HiddenColumns.Contains(GridColumn.Updated));
        vm.List.ToggleColumnCommand.Execute(GridColumn.Updated);
        T.True("shown again", vm.List.HiddenColumns.Count == 0);
    }

    public static void TheOtherTasksTableSortsFiltersAndCountsRatesInTheBackground()
    {
        var (vm, _, _) = Window(DemoSet.WithProblems);
        vm.ShowForeign = true;
        vm.List.RefreshAsync(refreshOthers: true).GetAwaiter().GetResult();
        WaitFor("rates arrive", () => !vm.List.ForeignRatesPending);
        T.Eq("five rows", 5, vm.List.ForeignRows.Count);
        T.Eq("the demo task runs every Wednesday", "weekly", vm.List.ForeignRows.First(r => r.Name == "ScheduledDefrag").RateText);
        T.Eq("a task without a definition keeps a dash", "-", vm.List.ForeignRows.First(r => r.Name == "GoogleUpdateTaskMachineCore").RateText);
        vm.List.FilterText = "defrag";
        T.Eq("the filter applies to them too", "ScheduledDefrag", string.Join(",", vm.List.ForeignRows.Select(r => r.Name)));
        vm.List.FilterText = "";
        vm.List.SortBy(GridColumn.Name);
        T.Eq("Name sort", "GoogleUpdateTaskMachineCore", vm.List.ForeignRows[0].Name);
    }

    public static void SeveralDisabledOtherTasksSortByRateBeforeTheirRatesAreCounted()
    {
        // [Found live] two or more disabled other tasks (the real machine has many) made the default Rate sort throw before any rate was counted, and the home screen showed
        // "Couldn't read your schedules: Failed to compare two elements in the array." next to rows that had loaded fine.
        var services = new DemoServices(DemoSet.WithProblems);
        services.AddForeign(@"\Microsoft\Windows\Disabled\One", 1);
        services.AddForeign(@"\Microsoft\Windows\Disabled\Two", 1);
        var (vm, _, _) = Window(DemoSet.WithProblems, services);
        vm.ShowForeign = true;
        vm.List.RefreshAsync(refreshOthers: true).GetAwaiter().GetResult();
        T.Eq("no error banner", null, vm.List.Error);
        WaitFor("rates arrive", () => !vm.List.ForeignRatesPending);
        T.Eq("seven rows", 7, vm.List.ForeignRows.Count);
        foreach (GridColumn column in Enum.GetValues<GridColumn>())
            for (int i = 0; i < 2; i++)
            {
                vm.List.SortBy(column);
                T.Eq("still no error banner after sorting by " + column, null, vm.List.Error);
                T.Eq("rows kept (" + column + ")", 7, vm.List.ForeignRows.Count);
            }
    }

    public static void AWrappedFailureShowsTheRealCauseInTheBanner()
    {
        var wrapped = new InvalidOperationException("Failed to compare two elements in the array.", new NullReferenceException("Object reference not set to an instance of an object."));
        T.Eq("wrapper plus the inner type and message",
            "Failed to compare two elements in the array. (NullReferenceException: Object reference not set to an instance of an object.)", ListPageViewModel.Describe(wrapped));
        T.Eq("a plain one stays as it was", "disk full", ListPageViewModel.Describe(new IOException("disk full")));
        T.Eq("the innermost wins", "outer (IOException: root)", ListPageViewModel.Describe(new InvalidOperationException("outer", new ArgumentException("mid", new IOException("root")))));
    }
}
