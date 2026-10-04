using Cadence.App.UI.ViewModels;
using Cadence.Core.History;
using Cadence.Core.Planning;
using Cadence.Core.TaskXml;
using Cadence.TestHarness;
using static Cadence.Tests.PlanSamples;
using static Cadence.Tests.ScriptedServices;

namespace Cadence.Tests;

/// <summary>
/// Run now and the live refresh (Phase 15): the 3 s timer's logic without a timer. The view calls <c>LiveTickAsync()</c> every 3 seconds; these tests
/// call it directly with a scripted Task Scheduler and a fixed clock. Also the could-not-start notice ("Windows refused to start this job: access denied (0x80070005).").
/// </summary>
public static class LiveRunTests
{
    static readonly DateTimeOffset Now = Cadence.App.Demo.DemoServices.FixedNow;

    static (EditorViewModel Vm, ScriptedServices Svc, RecordingHost Host) OpenGoodJob(params RunView[] runs)
    {
        var svc = new ScriptedServices();
        svc.Entries.Add(Entry(Classify(Stock()), runs.Length > 0 ? runs : [Run(Now.AddHours(-20), RunStatus.Ok, 12)]));
        var host = new RecordingHost();
        return (EditorViewModel.ForEntry(svc, new RecordingDialogs(), host.Value, svc.Entries[0]), svc, host);
    }

    static void SetRuns(ScriptedServices svc, params RunView[] runs) => svc.Entries[0] = Entry(svc.Entries[0].Task, runs);

    static RunView OldOk => Run(Now.AddHours(-20), RunStatus.Ok, 12);

    static RunView Running(double secondsAgo = 2) => Run(Now.AddSeconds(-secondsAgo), RunStatus.Running);

    static void Tick(EditorViewModel vm) => vm.Runs.LiveTickAsync().GetAwaiter().GetResult();

    public static void NothingHappensWhileNothingRuns()
    {
        var (vm, svc, _) = OpenGoodJob();
        T.True("no live polling is needed", !vm.Runs.NeedsLivePoll && !vm.Runs.IsRunning);
        int loads = svc.LoadCount;
        Tick(vm);
        Tick(vm);
        T.Eq("a tick does not even look", loads, svc.LoadCount);
    }

    public static void ARunningRowTurnsOnTheLiveIndicatorAndSwitchesRunNowOff()
    {
        var (vm, svc, _) = OpenGoodJob(Running(), OldOk);
        T.True("running", vm.Runs.IsRunning && vm.Runs.NeedsLivePoll);
        T.True("Run now is off (Task Scheduler would ignore a second start)", !vm.Runs.CanRunNow && !vm.Runs.RunNowCommand.CanExecute(null));
        int before = svc.Calls.Count(c => c.StartsWith("run ", StringComparison.Ordinal));
        vm.Runs.RunNowAsync().GetAwaiter().GetResult();
        T.Eq("pressing it anyway does nothing", before, svc.Calls.Count(c => c.StartsWith("run ", StringComparison.Ordinal)));
        T.Eq("the newest row", "Running…", vm.Runs.RunRows[0].Text.Title);
    }

    public static void RunNowThenTicksFollowTheRunToItsEnd()
    {
        var (vm, svc, host) = OpenGoodJob();
        svc.AfterRunNow = _ => SetRuns(svc, Running(), OldOk);

        vm.Runs.RunNowAsync().GetAwaiter().GetResult();
        T.True("run was requested", svc.Calls.Contains("run Stock ingest (Dad)"));
        T.True("toast", host.Toasts.Any(t => t.StartsWith("Started", StringComparison.Ordinal)));
        T.True("the first look found the new run", vm.Runs.IsRunning && vm.Runs.NeedsLivePoll);
        T.Eq("feedback while running", "Running now. This list updates by itself.", vm.Runs.RunFeedback);
        T.True("not an error", !vm.Runs.RunFeedbackIsError && vm.Runs.HasRunFeedback);

        Tick(vm);
        T.True("still running after a tick", vm.Runs.IsRunning && vm.Runs.RunFeedback!.StartsWith("Running now", StringComparison.Ordinal));

        SetRuns(svc, Run(Now.AddSeconds(-2), RunStatus.Ok, 12.4), OldOk);
        Tick(vm);
        T.True("finished", !vm.Runs.IsRunning && !vm.Runs.NeedsLivePoll);
        T.Eq("feedback", "Finished: Ok · 12 s", vm.Runs.RunFeedback);
        T.True("Run now is back", vm.Runs.CanRunNow);
        T.Eq("the row says Ok", "Ok", vm.Runs.RunRows[0].Text.Title);

        int loads = svc.LoadCount;
        Tick(vm);
        T.Eq("polling stopped: a further tick does nothing", loads, svc.LoadCount);
    }

    public static void AFailedRunIsReportedInRed()
    {
        var (vm, svc, _) = OpenGoodJob();
        svc.AfterRunNow = _ => SetRuns(svc, Running(), OldOk);
        vm.Runs.RunNowAsync().GetAwaiter().GetResult();
        SetRuns(svc, Run(Now.AddSeconds(-2), RunStatus.Failed, 2.1, exit: 1, message: "HTTPError: 429"), OldOk);
        Tick(vm);
        T.True("finished with the exit code", vm.Runs.RunFeedback!.StartsWith("Finished: Failed · exit 1", StringComparison.Ordinal), vm.Runs.RunFeedback);
        T.True("flagged as an error", vm.Runs.RunFeedbackIsError);
    }

    public static void AHolidaySkipIsExplained()
    {
        var (vm, svc, _) = OpenGoodJob();
        svc.AfterRunNow = _ => SetRuns(svc, Run(Now.AddSeconds(-1), RunStatus.Skipped, message: "Thanksgiving Day"), OldOk);
        vm.Runs.RunNowAsync().GetAwaiter().GetResult();
        T.True("said plainly", vm.Runs.RunFeedback == "Skipped · Thanksgiving Day. Run now follows the holiday list too.", vm.Runs.RunFeedback);
        T.True("not an error", !vm.Runs.RunFeedbackIsError && !vm.Runs.NeedsLivePoll);
    }

    public static void IfTaskSchedulerNeverReportsAnythingThePollingGivesUp()
    {
        var (vm, svc, _) = OpenGoodJob();
        vm.Runs.RunNowAsync().GetAwaiter().GetResult();
        T.True("waiting", vm.Runs.NeedsLivePoll && vm.Runs.RunFeedback!.Contains("Waiting for Task Scheduler", StringComparison.Ordinal));
        int ticks = 0;
        while (vm.Runs.NeedsLivePoll && ticks < 50) { Tick(vm); ticks++; }
        T.Eq("it watched for exactly WatchTicks ticks", RunsSectionViewModel.WatchTicks, ticks);
        T.True("then it says so", vm.Runs.RunFeedback!.StartsWith("Started, but Task Scheduler hasn't reported a run yet", StringComparison.Ordinal), vm.Runs.RunFeedback);
        T.True("and stopped", !vm.Runs.NeedsLivePoll);
        T.True("Run now works again", vm.Runs.CanRunNow);
    }

    public static void ARefusedLaunchIsExplainedAndFlaggedWhenItAppears()
    {
        var (vm, svc, _) = OpenGoodJob();
        vm.Runs.RunNowAsync().GetAwaiter().GetResult();
        Tick(vm);
        T.True("nothing yet", vm.Runs.NeedsLivePoll && !vm.Runs.HasBlockedNotice);

        SetRuns(svc, Run(Now.AddSeconds(1), RunStatus.NotReported, message: TaskResultCodes.AccessDeniedLaunch), OldOk);
        Tick(vm);
        T.Eq("feedback", "Windows refused to start this job: access denied (0x80070005). The job did not run (details below).", vm.Runs.RunFeedback);
        T.True("an error, and polling stopped", vm.Runs.RunFeedbackIsError && !vm.Runs.NeedsLivePoll);
        T.True("the notice shows", vm.Runs.HasBlockedNotice);
    }

    public static void ARunNowTaskSchedulerRefusesIsShownAsAnError()
    {
        var (vm, svc, host) = OpenGoodJob();
        svc.RunNowError = "Task Scheduler: the task is disabled";
        vm.Runs.RunNowAsync().GetAwaiter().GetResult();
        T.True("status is the sentence", vm.Status == "Task Scheduler: the task is disabled" && vm.StatusIsError);
        T.True("no feedback line, no polling, no toast", !vm.Runs.HasRunFeedback && !vm.Runs.NeedsLivePoll && host.Toasts.Count == 0);
    }

    public static void OverlappingTicksAreMergedIntoOne()
    {
        var (vm, svc, _) = OpenGoodJob(Running(), OldOk);
        int before = svc.LoadCount;
        svc.LoadGate = new TaskCompletionSource();
        Task first = vm.Runs.LiveTickAsync();
        T.True("the first tick is waiting for Task Scheduler", !first.IsCompleted && svc.LoadCount == before + 1);
        Task second = vm.Runs.LiveTickAsync();
        T.True("the second returns at once without a second read", second.IsCompleted && svc.LoadCount == before + 1);
        svc.LoadGate.SetResult();
        first.GetAwaiter().GetResult();
        T.True("after the first finished a new tick reads again", vm.Runs.IsRunning);
        svc.LoadGate = null;
        Tick(vm);
        T.Eq("one more read", before + 2, svc.LoadCount);
    }

    public static void AFailedLookDuringPollingIsShownNotThrown()
    {
        var (vm, svc, _) = OpenGoodJob(Running(), OldOk);
        svc.LoadFails = new IOException("the disk went away");
        Tick(vm);
        T.True("a sentence in red", vm.Runs.RunFeedbackIsError && vm.Runs.RunFeedback == "Couldn't refresh the runs: the disk went away");
        svc.LoadFails = null;
        Tick(vm);
        T.True("the next tick works again (it is still running)", vm.Runs.IsRunning);
    }

    public static void TheLiveIntervalIsThreeSeconds() =>
        T.Eq("interval", TimeSpan.FromSeconds(3), RunsSectionViewModel.LivePollInterval);

    // ---- the blocked-launch notice ----

    public static void ABlockedNewestRunShowsTheNoticeAboveTheNumbers()
    {
        RunView blocked = Run(Now.AddDays(-5).AddHours(-6), RunStatus.NotReported, message: TaskResultCodes.AccessDeniedLaunch);
        var (vm, _, host) = OpenGoodJob(blocked, OldOk);
        T.True("notice", vm.Runs.HasBlockedNotice);
        T.Eq("title states what Windows said", "Windows refused to start this job: access denied (0x80070005).", vm.Runs.BlockedTitle);
        T.True("the body lists possibilities (not a diagnosis) and the way out", vm.Runs.BlockedBody.Contains(TaskResultCodes.AccessDeniedPossibilities, StringComparison.Ordinal)
            && vm.Runs.BlockedBody.Contains("Run now", StringComparison.Ordinal) && !vm.Runs.BlockedBody.Contains("Protection history", StringComparison.Ordinal));
        T.True("the shortcut shows for access denied", vm.Runs.ShowSecurityLink);
        T.True("when it was last tried", vm.Runs.BlockedWhen.StartsWith("Last tried ", StringComparison.Ordinal), vm.Runs.BlockedWhen);
        T.True("the row reads the same", vm.Runs.RunRows[0].Text.Title == "Couldn't start (access denied)" && vm.Runs.RunRows[0].IsBlocked);

        vm.Runs.OpenSecurityCommand.Execute(null);
        vm.Runs.RunRows[0].OpenSecurityCommand.Execute(null);
        T.True("both links only open the Windows Security app", host.Links.SequenceEqual(["windowsdefender://", "windowsdefender://"]));
        T.Eq("the link text", "Open Windows Security", RunsSectionViewModel.BlockedLinkText);
        T.True("the expanded row keeps the code in hex", vm.Runs.RunRows[0].Detail.Message!.Contains("0x80070005", StringComparison.Ordinal));
    }

    public static void TheNoticeGoesAwayOnceALaterRunCounted()
    {
        RunView blocked = Run(Now.AddDays(-5), RunStatus.NotReported, message: TaskResultCodes.AccessDeniedLaunch);
        var (vm, svc, _) = OpenGoodJob(Run(Now.AddHours(-3), RunStatus.Ok, 9), blocked);
        T.True("an Ok run is newer: no notice", !vm.Runs.HasBlockedNotice);

        var (skipped, _, _) = OpenGoodJob(Run(Now.AddHours(-3), RunStatus.Skipped, message: "Thanksgiving Day"), blocked);
        T.True("a holiday skip does not hide it", skipped.Runs.HasBlockedNotice);

        var (running, _, _) = OpenGoodJob(Running(), blocked);
        T.True("a run in progress does not hide it either (nothing is known about it yet)", running.Runs.HasBlockedNotice);

        var (ordinary, _, _) = OpenGoodJob(Run(Now.AddHours(-3), RunStatus.NotReported, message: "Task Scheduler started it but no run record exists."));
        T.True("a different 'Didn't report' is not the blocked notice", !ordinary.Runs.HasBlockedNotice && ordinary.Runs.RunRows[0].Text.Title == "Didn't report" && !ordinary.Runs.RunRows[0].IsBlocked);
        T.True("svc unused", svc.Entries.Count == 1);
    }

    public static void ABlockedRunNeverDecidesTheBadgeThroughTaskSchedulersCode()
    {
        // The row comes from run records and Task Scheduler's state (a synthesized NotReported row); the editor shows it, the list badge uses it (CP-B).
        RunView blocked = Run(Now.AddHours(-1), RunStatus.NotReported, message: TaskResultCodes.AccessDeniedLaunch);
        var (vm, _, _) = OpenGoodJob(blocked);
        T.Eq("badge tone of the row", Cadence.App.UI.Logic.BadgeKind.NotReported, vm.Runs.RunRows[0].Tone);
    }
}
