using Cadence.App.Demo;
using Cadence.App.UI.Services;
using Cadence.App.UI.ViewModels;
using Cadence.Core.Cron;
using Cadence.Core.History;
using Cadence.Core.Planning;
using Cadence.Cron;
using Cadence.TestHarness;

namespace Cadence.Tests;

/// <summary>
/// Checkpoint C (UX review) fixes that are not covered by the drop-flow tests in <see cref="DropWiringTests"/>: one Delete per screen, one ⋯ per
/// screen, and demo data that is believable (every demo run falls on its own schedule, so the renders never show a Saturday job running daily).
/// </summary>
public static class CheckpointCTests
{
    sealed class NoDialogs : IDialogs, IShell
    {
        public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool danger) => Task.FromResult(true);
        public Task<bool> ConfirmTypedAsync(string title, string message, string word, string confirmText) => Task.FromResult(true);
        public Task InfoAsync(string title, string message) => Task.CompletedTask;
        public void ShowAbout() { }
        public string? PickProgram() => null;
        public void OpenTaskScheduler() { }
        public bool OpenFolder(string path) => false;
        public void CopyText(string text) { }
    }

    static EditorViewModel Open(DemoServices services, string taskName)
    {
        ManagedEntry entry = services.LoadManagedAsync().GetAwaiter().GetResult().Entries.Single(e => e.Task.Observed.Name == taskName);
        return EditorViewModel.ForEntry(services, new NoDialogs(), EditorHost.None, entry);
    }

    public static void OneDeleteButtonPerScreen()
    {
        var services = new DemoServices(DemoSet.Showcase);
        foreach (string name in new[] { "Month-end export", "Stock ingest (Dad) (copy)" })
        {
            EditorViewModel vm = Open(services, name);
            T.True(name + ": the banner offers Delete", vm.Notice.ShowBannerDelete);
            T.True(name + ": so the footer does not", !vm.ShowFooterDelete);
        }
        foreach (string name in new[] { "Stock ingest (Dad)", "Weekly report", "Archive photos" })
        {
            EditorViewModel vm = Open(services, name);
            T.True(name + ": no banner Delete", !vm.Notice.ShowBannerDelete);
            T.True(name + ": the footer has Delete", vm.ShowFooterDelete);
        }
        EditorViewModel fresh = EditorViewModel.ForTemplate(services, new NoDialogs(), EditorHost.None, Cadence.Core.Text.Templates.All[0]);
        T.True("a new job has no Delete at all", !fresh.ShowFooterDelete && !fresh.Notice.ShowBannerDelete);
        T.True("without a window there is no preset sheet to open", !fresh.Schedule.HasPresets);
    }

    public static void TheAppMenuIsOnlyOnTheList()
    {
        // The header's ⋯ is bound to IsListShowing (MainView.xaml); the editor brings its own ⋯ for the schedule.
        var vm = new MainViewModel(new DemoServices(DemoSet.Normal), new NoDialogs(), new NoDialogs());
        vm.Start();
        vm.List.InitialLoad.GetAwaiter().GetResult();
        T.True("list: the app menu shows", vm.IsListShowing);
        vm.PickTemplateById("blank");
        T.True("editor: it does not", !vm.IsListShowing);
        T.True("the editor's own sheet hook is wired", ((EditorViewModel)vm.CurrentPage).Schedule.HasPresets);
    }

    public static void EveryDemoRunFallsOnItsSchedule()
    {
        foreach (DemoSet set in new[] { DemoSet.Normal, DemoSet.Showcase, DemoSet.RunsShowcase })
        {
            var services = new DemoServices(set);
            foreach (ManagedEntry e in services.LoadManagedAsync().GetAwaiter().GetResult().Entries)
            {
                if (e.Task.Job is not { } job) continue;
                var diags = new List<CronNote>();
                var lines = job.Schedule.CronLines.Select((l, i) => CronParser.Parse(l, i + 1, diags)).OfType<CronExpression>().ToList();
                T.True(set + " " + job.Name + ": its cron parses", lines.Count == job.Schedule.CronLines.Count);
                foreach (RunView run in e.History.Recent.Where(r => r.Status != RunStatus.Running))
                {
                    DateTime local = TimeZoneInfo.ConvertTime(run.When, services.Zone).DateTime;
                    local = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0);
                    T.True(set + " " + job.Name + ": the run at " + local.ToString("ddd d MMM H:mm", System.Globalization.CultureInfo.InvariantCulture) + " is on its schedule",
                        CronMatcher.Matches(lines, local));
                }
            }
        }
    }
}
