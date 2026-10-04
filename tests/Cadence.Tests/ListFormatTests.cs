using Cadence.App.Demo;
using Cadence.App.UI.Logic;
using Cadence.App.UI.Services;
using Cadence.Core.History;
using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.Core.TaskXml;
using Cadence.Core.Text;
using Cadence.History;
using Cadence.TestHarness;

namespace Cadence.Tests;

/// <summary>The pure list logic of the UI (PLAN Phase 12): badge rule, result text, next-run text, sorting and the demo data.</summary>
public static class ListFormatTests
{
    // A fixed zone and instant: nothing here depends on today's date or the machine's time zone.
    static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("T", TimeSpan.FromHours(-5), "T", "T");
    static readonly DateTimeOffset Now = new(2026, 11, 27, 9, 0, 0, TimeSpan.FromHours(-5));

    static DateTimeOffset At(int month, int day, int hour, int minute) => new(2026, month, day, hour, minute, 0, TimeSpan.FromHours(-5));

    static ClassifiedTask Task(Ownership ownership = Ownership.Managed, SyncStatus sync = SyncStatus.InSync, bool enabled = true, string cron = "0 18 * * 1-5",
        HolidayCalendar skip = HolidayCalendar.None, int? lastTaskResult = null, string name = "Backup")
    {
        JobDefinition? job = ownership == Ownership.Managed
            ? new JobDefinition
            {
                Id = "abcd1234", Name = name, Enabled = enabled,
                Schedule = new ScheduleSpec(ValueList.Of(cron), skip, new DateOnly(2026, 9, 1)),
                Run = new RunSpec { Exe = "cmd.exe" },
            }
            : null;
        return new ClassifiedTask(new ObservedTask(name, @"\Cadence\" + name, "", 3, null, lastTaskResult, null), ownership, sync, null, null, job,
            ValueList<FieldDiff>.Empty, ownership == Ownership.Managed ? null : "problem text");
    }

    static RunView Run(DateTimeOffset when, RunStatus status, double seconds = 12, int? exit = null, string? message = null) =>
        new(when.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture), when,
            status is RunStatus.Skipped or RunStatus.NotReported or RunStatus.LaunchError ? null : TimeSpan.FromSeconds(seconds), status, exit, message, false);

    static JobHistory History(params RunView[] newestFirst) => new(newestFirst, RunHistory.Summarize(newestFirst));

    public static void AgoWords()
    {
        (TimeSpan Age, string Text)[] cases =
        [
            (TimeSpan.FromSeconds(-30), "just now"),
            (TimeSpan.Zero, "just now"),
            (TimeSpan.FromSeconds(59), "just now"),
            (TimeSpan.FromMinutes(1), "1 min ago"),
            (TimeSpan.FromMinutes(59), "59 min ago"),
            (TimeSpan.FromMinutes(60), "1 h ago"),
            (TimeSpan.FromHours(2) + TimeSpan.FromMinutes(40), "2 h ago"),
            (TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59), "23 h ago"),
            (TimeSpan.FromHours(24), "yesterday"),
            (TimeSpan.FromHours(30), "yesterday"),
            (TimeSpan.FromDays(2), "2 d ago"),
            (TimeSpan.FromDays(6), "6 d ago"),
            (TimeSpan.FromDays(7), "20 Nov"),
            (TimeSpan.FromDays(40), "18 Oct"),
            (TimeSpan.FromDays(400), "23 Oct 2025"),
        ];
        foreach ((TimeSpan age, string text) in cases)
            T.Eq("ago " + age, text, ListFormat.Ago(Now - age, Now, Zone));
    }

    public static void WhenWords()
    {
        T.Eq("later today", "Today 18:00", ListFormat.When(At(11, 27, 18, 0), Now, Zone));
        T.Eq("today, early", "Today 9:15", ListFormat.When(At(11, 27, 9, 15), Now, Zone));
        T.Eq("tomorrow", "Tomorrow 8:00", ListFormat.When(At(11, 28, 8, 0), Now, Zone));
        T.Eq("later", "Mon 30 Nov 8:00", ListFormat.When(At(11, 30, 8, 0), Now, Zone));
        T.Eq("next year shows the year", "Fri 1 Jan 2027 0:00", ListFormat.When(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.FromHours(-5)), Now, Zone));
        T.Eq("a different offset is converted to the zone", "Tomorrow 8:00", ListFormat.When(new DateTimeOffset(2026, 11, 28, 14, 0, 0, TimeSpan.FromHours(1)), Now, Zone));
    }

    public static void NextTexts()
    {
        T.Eq("value", "Next: Today 18:00", ListFormat.NextText(At(11, 27, 18, 0), true, Now, Zone));
        T.Eq("disabled hides the time", "Disabled", ListFormat.NextText(At(11, 27, 18, 0), false, Now, Zone));
        T.Eq("no upcoming run", "No upcoming run", ListFormat.NextText(null, true, Now, Zone));
    }

    public static void NextRunSkipsHolidaysAndDisabled()
    {
        var spec = new ScheduleSpec(ValueList.Of("0 18 * * 1-5"), HolidayCalendar.Nyse, new DateOnly(2026, 9, 1));
        DateTimeOffset beforeThanksgiving = At(11, 25, 19, 0);
        T.Eq("Thanksgiving (Thu 26 Nov) is skipped", At(11, 27, 18, 0), ListFormat.NextRun(spec, true, beforeThanksgiving, Zone));
        T.Eq("the same day, before 18:00", At(11, 27, 18, 0), ListFormat.NextRun(spec, true, Now, Zone));
        T.True("disabled has none", ListFormat.NextRun(spec, false, Now, Zone) is null);
        var noSkip = spec with { SkipHolidays = HolidayCalendar.None };
        T.Eq("without the calendar the holiday counts", At(11, 26, 18, 0), ListFormat.NextRun(noSkip, true, beforeThanksgiving, Zone));
        var logon = new ScheduleSpec(ValueList.Of("@logon"), HolidayCalendar.None, new DateOnly(2026, 9, 1));
        T.True("at sign-in has no clock time", ListFormat.NextRun(logon, true, Now, Zone) is null);
        var broken = new ScheduleSpec(ValueList.Of("not cron"), HolidayCalendar.None, new DateOnly(2026, 9, 1));
        T.True("an invalid schedule has none", ListFormat.NextRun(broken, true, Now, Zone) is null);
    }

    public static void BadgeRule()
    {
        RunView ok = Run(At(11, 27, 8, 0), RunStatus.Ok), failed = Run(At(11, 27, 8, 0), RunStatus.Failed, exit: 3);
        RunView timedOut = Run(At(11, 27, 8, 0), RunStatus.TimedOut), launch = Run(At(11, 27, 8, 0), RunStatus.LaunchError);
        RunView notReported = Run(At(11, 27, 8, 0), RunStatus.NotReported, message: "m");
        RunView skipped = Run(At(11, 27, 8, 0), RunStatus.Skipped, message: "Thanksgiving Day");
        RunView running = Run(At(11, 27, 8, 59), RunStatus.Running);
        RunView olderOk = Run(At(11, 26, 8, 0), RunStatus.Ok);

        T.Eq("ok", BadgeKind.Ok, ListFormat.Badge(Task(), History(ok), true));
        T.Eq("failed", BadgeKind.Failed, ListFormat.Badge(Task(), History(failed), true));
        T.Eq("launch error is a failure", BadgeKind.Failed, ListFormat.Badge(Task(), History(launch), true));
        T.Eq("timed out", BadgeKind.TimedOut, ListFormat.Badge(Task(), History(timedOut), true));
        T.Eq("did not report", BadgeKind.NotReported, ListFormat.Badge(Task(), History(notReported), true));
        T.Eq("never ran", BadgeKind.None, ListFormat.Badge(Task(), History(), true));
        T.Eq("a newer skip is neutral, not the older ok", BadgeKind.Skipped, ListFormat.Badge(Task(), History(skipped, olderOk), true));
        T.Eq("a skip after a failure is still neutral", BadgeKind.Skipped, ListFormat.Badge(Task(), History(skipped, failed), true));
        T.Eq("running wins over the previous result", BadgeKind.Running, ListFormat.Badge(Task(), History(running, failed), true));
        T.Eq("disabled is dim even after a failure", BadgeKind.Disabled, ListFormat.Badge(Task(), History(failed), false));
        T.Eq("drifted is yellow", BadgeKind.Drifted, ListFormat.Badge(Task(sync: SyncStatus.Drifted), History(ok), true));
        T.Eq("drifted beats disabled", BadgeKind.Drifted, ListFormat.Badge(Task(sync: SyncStatus.Drifted), History(failed), false));
        T.Eq("outdated is not a badge", BadgeKind.Ok, ListFormat.Badge(Task(sync: SyncStatus.Outdated), History(ok), true));
        T.Eq("corrupt", BadgeKind.Warning, ListFormat.Badge(Task(Ownership.CorruptMetadata), History(ok), true));
        T.Eq("newer version", BadgeKind.Warning, ListFormat.Badge(Task(Ownership.NewerVersion), History(ok), true));
    }

    public static void BadgeNeverComesFromTaskSchedulersResultCode()
    {
        // Hidden jobs always report 0, and a launch failure code must not paint a job red unless a run row says so.
        const int blocked = unchecked((int)0x80070005);
        RunView ok = Run(At(11, 27, 8, 0), RunStatus.Ok);
        T.Eq("ok record, error code", BadgeKind.Ok, ListFormat.Badge(Task(lastTaskResult: blocked), History(ok), true));
        T.Eq("failed record, zero code", BadgeKind.Failed, ListFormat.Badge(Task(lastTaskResult: 0), History(Run(At(11, 27, 8, 0), RunStatus.Failed, exit: 1)), true));
        T.Eq("no record, error code", BadgeKind.None, ListFormat.Badge(Task(lastTaskResult: blocked), History(), true));
    }

    public static void ResultText()
    {
        var r = ListFormat.Result(History(Run(At(11, 27, 7, 0), RunStatus.Ok, 12.4)), Now, Zone);
        T.Eq("ok title", "Ok", r.Title);
        T.Eq("ok detail", "2 h ago · 12 s", r.Detail);

        r = ListFormat.Result(History(Run(At(11, 26, 8, 0), RunStatus.Failed, 8, 3, "boom")), Now, Zone);
        T.Eq("failed title", "Failed (exit 3)", r.Title);
        T.Eq("failed detail", "yesterday · 8.0 s", r.Detail);
        T.Eq("failed tooltip is the message", "boom", r.Tooltip);

        r = ListFormat.Result(History(Run(At(11, 27, 2, 0), RunStatus.Failed, 8)), Now, Zone);
        T.Eq("failed without an exit code", "Failed", r.Title);

        r = ListFormat.Result(History(Run(At(11, 27, 2, 0), RunStatus.TimedOut, 3600)), Now, Zone);
        T.Eq("timed out title", "Timed out", r.Title);
        T.Eq("timed out detail has no duration", "7 h ago", r.Detail);

        r = ListFormat.Result(History(Run(At(11, 27, 8, 50), RunStatus.LaunchError, message: "no such file")), Now, Zone);
        T.Eq("launch error title", "Couldn't start", r.Title);
        T.Eq("launch error tooltip", "no such file", r.Tooltip);

        r = ListFormat.Result(History(Run(At(11, 22, 3, 0), RunStatus.NotReported, message: TaskResultCodes.AccessDeniedLaunch)), Now, Zone);
        T.Eq("access denied title (short and neutral in the list)", "Couldn't start (access denied)", r.Title);
        T.Eq("blocked detail", "5 d ago", r.Detail);
        T.Eq("access denied tooltip is the full message and when", TaskResultCodes.AccessDeniedLaunch + "\nLast tried 5 d ago.", r.Tooltip);

        r = ListFormat.Result(History(Run(At(11, 27, 8, 0), RunStatus.NotReported, message: RunHistory.NotReportedNoCode)), Now, Zone);
        T.Eq("other no-report title", "Didn't report", r.Title);

        r = ListFormat.Result(History(Run(At(11, 27, 8, 57), RunStatus.Running), Run(At(11, 26, 8, 0), RunStatus.Ok)), Now, Zone);
        T.Eq("running title", "Running…", r.Title);
        T.Eq("running detail", "started 3 min ago", r.Detail);

        r = ListFormat.Result(History(Run(At(11, 26, 18, 0), RunStatus.Skipped, message: "Thanksgiving Day"), Run(At(11, 25, 18, 0), RunStatus.Ok)), Now, Zone);
        T.Eq("skipped title", "Skipped", r.Title);
        T.Eq("skipped detail names the holiday", "Thanksgiving Day · 15 h ago", r.Detail);

        r = ListFormat.Result(History(), Now, Zone);
        T.Eq("no runs", "No runs yet", r.Title);
        T.Eq("no runs detail", "", r.Detail);
    }

    public static void DescribeRows()
    {
        RunView ok = Run(At(11, 27, 8, 45), RunStatus.Ok, 1.4);
        RowText row = ListFormat.Describe(Task(name: "Sync notes", cron: "*/15 * * * *"), History(ok), At(11, 27, 9, 15), Now, Zone);
        T.Eq("name", "Sync notes", row.Name);
        T.Eq("schedule", "Every 15 minutes", row.Schedule);
        T.Eq("next", "Next: Today 9:15", row.Next);
        T.Eq("next cell has no label", "Today 9:15", row.NextCell);
        T.Eq("rate", "every 15 min", row.RateText);
        T.Eq("result cell is the result and when", "Ok · 15 min ago", row.ResultCell);
        T.Eq("avg", "1.4 s", row.AvgText);
        T.Eq("success", "100%", row.SuccessText);
        T.Eq("no state", "", row.StateText);
        T.Eq("title", "Ok", row.ResultTitle);
        T.Eq("detail", "15 min ago · 1.4 s", row.ResultDetail);
        T.Eq("badge", BadgeKind.Ok, row.Badge);
        T.True("no note", row.Note is null);
        T.True("not a problem", !row.IsProblem && row.IsEnabled);

        RowText drift = ListFormat.Describe(Task(sync: SyncStatus.Drifted), History(ok), At(11, 27, 18, 0), Now, Zone);
        T.Eq("drift badge", BadgeKind.Drifted, drift.Badge);
        T.Eq("drift keeps the result tone", BadgeKind.Ok, drift.Tone);
        T.Eq("drift note", "Changed in Task Scheduler", drift.Note);
        T.True("drift tooltip", drift.BadgeTooltip?.Contains("outside Cadence", StringComparison.Ordinal) == true);
        T.Eq("drift state words", "Changed in Task Scheduler", drift.StateText);
        T.Eq("drift state color", TextTone.Yellow, drift.StateTone);
        T.True("a changed row counts as a problem", drift.HasProblem);

        RowText off = ListFormat.Describe(Task(enabled: false), History(ok), null, Now, Zone);
        T.Eq("disabled next", "Disabled", off.Next);
        T.Eq("disabled badge", BadgeKind.Disabled, off.Badge);
        T.Eq("disabled next cell", "-", off.NextCell);
        T.Eq("disabled state", "Disabled", off.StateText);
        T.True("disabled tooltip says what it would be", off.RateTooltip!.StartsWith("Disabled. When enabled: ", StringComparison.Ordinal));
        T.True("disabled is not a problem", !off.HasProblem);
        T.True("disabled is not enabled", !off.IsEnabled);

        RowText stock = ListFormat.Describe(Task(name: "Stock ingest (Dad)", skip: HolidayCalendar.Nyse), History(), At(11, 27, 18, 0), Now, Zone);
        T.Eq("holiday sentence", "Every weekday at 18:00, except NYSE market holidays", stock.Schedule);

        RowText corrupt = ListFormat.Describe(Task(Ownership.CorruptMetadata, name: "Old"), JobHistory.Empty, null, Now, Zone);
        T.True("corrupt is a problem row", corrupt.IsProblem && corrupt.Badge == BadgeKind.Warning);
        T.Eq("corrupt state", "Damaged settings", corrupt.StateText);
        T.Eq("corrupt sorts last in Rate", 4, corrupt.Keys.RateGroup);
        T.Eq("corrupt name is the task name", "Old", corrupt.Name);
        T.True("corrupt text says deleted", corrupt.Schedule.Contains("deleted", StringComparison.Ordinal));
        RowText newer = ListFormat.Describe(Task(Ownership.NewerVersion, name: "Future"), JobHistory.Empty, null, Now, Zone);
        T.True("newer is a problem row, read-only", newer.IsProblem && newer.Schedule.Contains("read-only", StringComparison.Ordinal));
    }

    public static void SortingIsAlphabeticalWithProblemsLast()
    {
        RowText Row(string name, bool problem) =>
            new(name, "", "", "", "", BadgeKind.None, BadgeKind.None, null, null, null, problem, true);
        string[] names = ["sync notes", "Zeta", "alpha", "Beta", "Old (damaged)", "Alpha"];
        var rows = names.Select(n => Row(n, n.StartsWith("Old", StringComparison.Ordinal))).ToList();
        IReadOnlyList<RowText> ordered = ListFormat.Order(rows, r => r);
        T.Eq("order", "Alpha, alpha, Beta, sync notes, Zeta, Old (damaged)", string.Join(", ", ordered.Select(r => r.Name)));
        T.Eq("stable for equal input", string.Join(",", ordered.Select(r => r.Name)), string.Join(",", ListFormat.Order(rows.AsEnumerable().Reverse(), r => r).Select(r => r.Name)));
    }

    public static void ForeignRows()
    {
        var task = new ClassifiedTask(
            new ObservedTask("ScheduledDefrag", @"\Microsoft\Windows\Defrag\ScheduledDefrag", "", 3, new DateTime(2026, 11, 25, 1, 0, 0), 0, new DateTime(2026, 12, 2, 1, 0, 0)),
            Ownership.Foreign, SyncStatus.NotApplicable, null, null, null, ValueList<FieldDiff>.Empty, null);
        ForeignText f = ListFormat.DescribeForeign(task, Now, Zone);
        T.Eq("name", "ScheduledDefrag", f.Name);
        T.Eq("folder", @"\Microsoft\Windows\Defrag", f.Folder);
        T.Eq("next", "Next: Wed 2 Dec 1:00", f.Next);
        T.Eq("result", "Ok · 2 d ago", f.Result);

        var never = new ClassifiedTask(new ObservedTask("Old", @"\Old", "", 1, null, null, null), Ownership.Foreign, SyncStatus.NotApplicable, null, null, null, ValueList<FieldDiff>.Empty, null);
        ForeignText g = ListFormat.DescribeForeign(never, Now, Zone);
        T.Eq("disabled", "Disabled", g.Next);
        T.Eq("never run", "Never run", g.Result);
        T.Eq("root folder", "\\", g.Folder);
        T.True("disabled flag", !g.IsEnabled);
    }

    public static void TemplateCardNextText()
    {
        Template Get(string id) => Templates.All.Single(t => t.Id == id);
        T.Eq("good morning (Friday 9:00 now, next weekday 8:00 is Monday)", "Next: Mon 30 Nov 8:00", ListFormat.TemplateNext(Get("good-morning"), Now, Zone));
        T.Eq("every 15 minutes", "Next: Today 9:15", ListFormat.TemplateNext(Get("every-15-minutes"), Now, Zone));
        T.Eq("nightly", "Next: Tomorrow 2:00", ListFormat.TemplateNext(Get("nightly-2am"), Now, Zone));
        T.Eq("login", "Runs when you sign in", ListFormat.TemplateNext(Get("at-login"), Now, Zone));
        foreach (Template t in Templates.All)
            T.True(t.Id + " has a next-run line", ListFormat.TemplateNext(t, Now, Zone).Length > 0);
    }

    // ---- the demo data is the render input: pin it ----

    static async Task<IReadOnlyList<(ManagedEntry Entry, RowText Text)>> DemoRows(DemoSet set)
    {
        var demo = new DemoServices(set);
        ManagedSnapshot snap = await demo.LoadManagedAsync();
        var described = snap.Entries.Select(e => (Entry: e, Text: ListFormat.Describe(e.Task, e.History, e.Next, demo.Now, demo.Zone))).ToList();
        IComparer<RowText> byDefaultSort = GridLogic.Compare(SortSpec.Default.Column, SortSpec.Default.Descending);
        return described.OrderBy(d => d.Text, byDefaultSort).ToList();
    }

    public static void DemoListIsDeterministicAndMatchesThePlan()
    {
        var rows = DemoRows(DemoSet.Normal).GetAwaiter().GetResult();
        // The default sort is Rate, most frequent first (design 3.1): every 15 min; daily by next run; weekdays by next run; weekly by next run.
        T.Eq("names in order", "Sync notes, Photo backup, Nightly backup, YouTube feed download, Stock ingest (Dad), Good morning, Disk cleanup, Weekly report",
            string.Join(", ", rows.Select(r => r.Text.Name)));
        T.Eq("rates in order", "every 15 min, daily, daily, daily, weekdays, weekdays, weekly, weekly", string.Join(", ", rows.Select(r => r.Text.RateText)));
        RowText Get(string name) => rows.Single(r => r.Text.Name == name).Text;

        T.Eq("stock sentence", "Every weekday at 18:00, except NYSE market holidays", Get("Stock ingest (Dad)").Schedule);
        T.Eq("stock: the newest row is the Thanksgiving skip", "Skipped", Get("Stock ingest (Dad)").ResultTitle);
        T.Eq("stock detail", "Thanksgiving Day · 15 h ago", Get("Stock ingest (Dad)").ResultDetail);
        T.Eq("stock next", "Next: Today 18:00", Get("Stock ingest (Dad)").Next);
        T.Eq("youtube sentence", "Every day at 7:00", Get("YouTube feed download").Schedule);
        T.Eq("youtube badge", BadgeKind.Ok, Get("YouTube feed download").Badge);
        T.Eq("weekly report is drifted", BadgeKind.Drifted, Get("Weekly report").Badge);
        T.Eq("weekly report note", "Changed in Task Scheduler", Get("Weekly report").Note);
        T.Eq("failing job", "Failed (exit 3)", Get("Photo backup").ResultTitle);
        T.Eq("failing badge", BadgeKind.Failed, Get("Photo backup").Badge);
        T.Eq("timed out job", BadgeKind.TimedOut, Get("Nightly backup").Badge);
        T.Eq("access denied job title", "Couldn't start (access denied)", Get("Disk cleanup").ResultTitle);
        T.Eq("blocked job badge", BadgeKind.NotReported, Get("Disk cleanup").Badge);
        T.Eq("sync notes next", "Next: Today 9:15", Get("Sync notes").Next);
        T.Eq("good morning result", "Ok", Get("Good morning").ResultTitle);
        T.Eq("weekly report state", "Changed in Task Scheduler", Get("Weekly report").StateText);
        T.Eq("a healthy row has no state", "", Get("Sync notes").StateText);
        T.Eq("status counts", (8, 4, 0), GridLogic.Counts(rows.Select(r => r.Text)));

        var again = DemoRows(DemoSet.Normal).GetAwaiter().GetResult();
        T.True("a second build is identical", rows.Select(r => r.Text).SequenceEqual(again.Select(r => r.Text)));
    }

    public static void DemoSetsAndSettings()
    {
        T.Eq("empty set", 0, DemoRows(DemoSet.Empty).GetAwaiter().GetResult().Count);
        var problems = DemoRows(DemoSet.WithProblems).GetAwaiter().GetResult();
        T.Eq("problem set adds two", 10, problems.Count);
        T.Eq("problems sort last", 2, problems.Skip(8).Count(r => r.Text.IsProblem));

        var demo = new DemoServices(DemoSet.WithProblems);
        T.True("the foreign toggle starts off", !demo.Settings.ShowForeign);
        T.True("the demo clock is fixed", demo.Now == new DateTimeOffset(2026, 11, 27, 9, 0, 0, TimeSpan.FromHours(-5)));
        T.Eq("foreign demo tasks", 5, demo.LoadOthersAsync(false).GetAwaiter().GetResult().Count);

        var task = demo.LoadManagedAsync().GetAwaiter().GetResult().Entries.First(e => e.Task.Job?.Name == "Good morning").Task;
        demo.SetEnabledAsync(task, false).GetAwaiter().GetResult();
        var after = demo.LoadManagedAsync().GetAwaiter().GetResult().Entries.First(e => e.Task.Job?.Name == "Good morning");
        T.True("disabling in the demo keeps the row and clears the next run", after.Task.Job?.Enabled == false && after.Next is null);
        demo.DeleteAsync(task).GetAwaiter().GetResult();
        T.Eq("deleting removes it", 9, demo.LoadManagedAsync().GetAwaiter().GetResult().Entries.Count);
    }
}
