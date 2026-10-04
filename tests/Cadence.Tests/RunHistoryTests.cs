using Cadence.Core.History;
using Cadence.Core.TaskXml;
using Cadence.TestHarness;
using static Cadence.Tests.HistorySamples;

namespace Cadence.Tests;

public static class RunHistoryTests
{
    static readonly IReadOnlyList<RunRecord> None = [];
    static DateTimeOffset Ago(TimeSpan span) => T0 - span;

    public static void FixturesSummary()
    {
        List<RunRecord> recs = Fixtures();
        JobSummary s = RunHistory.Summarize(recs);
        T.Eq("last result is the newest NON-skipped run (the failure)", (RunStatus?)RunStatus.Failed, s.LastResult);
        T.Eq("last run at is the holiday skip", (DateTimeOffset?)recs[3].WrapperStart, s.LastRunAt);
        T.Eq("sample size excludes the skip", 3, s.SampleSize);
        T.Eq("success 0 of 3", (double?)0.0, s.SuccessRate);
        T.Eq("average = the failed run only (42.2182 ms); timeout and launch error excluded", (TimeSpan?)TimeSpan.FromTicks(422182), s.AverageRuntime);
    }

    public static void FixturesRecent()
    {
        List<RunRecord> recs = Fixtures();
        IReadOnlyList<RunView> v = RunHistory.Recent(recs, 10, false, null, null);
        T.Eq("four rows", 4, v.Count);
        T.Eq("newest first: skipped", RunStatus.Skipped, v[0].Status);
        T.Eq("then the failure", RunStatus.Failed, v[1].Status);
        T.Eq("then the launch error (…752 sorts after …722)", RunStatus.LaunchError, v[2].Status);
        T.Eq("then the timeout", RunStatus.TimedOut, v[3].Status);
        T.True("skipped has no duration", v[0].Duration is null);
        T.Eq("failed: exit 3", (int?)3, v[1].ExitCode);
        T.Eq("failed: 42.2182 ms", (TimeSpan?)TimeSpan.FromTicks(422182), v[1].Duration);
        T.True("launch error has no duration", v[2].Duration is null);
        T.Eq("timeout: 3.3384261 s", (TimeSpan?)TimeSpan.FromTicks(33384261), v[3].Duration);
        T.True("has output follows the character counts", v[1].HasOutput && !v[2].HasOutput && v[3].HasOutput && !v[0].HasOutput);
        T.Eq("when is the wrapper start", recs[0].WrapperStart, v[1].When);
        T.Eq("max cuts the oldest", 2, RunHistory.Recent(recs, 2, false, null, null).Count);
        T.Eq("max 0", 0, RunHistory.Recent(recs, 0, false, null, null).Count);
    }

    public static void NoRunsYet()
    {
        JobSummary s = RunHistory.Summarize(None);
        T.True("everything is null", s.LastResult is null && s.LastRunAt is null && s.AverageRuntime is null && s.SuccessRate is null);
        T.Eq("sample size 0", 0, s.SampleSize);
        T.Eq("no rows", 0, RunHistory.Recent(None, 5, false, null, null).Count);
        T.Eq("Task Scheduler's never-ran date (1999) is ignored", 0, RunHistory.Recent(None, 5, false, new DateTime(1999, 11, 30, 0, 0, 0, DateTimeKind.Utc), 0x41303).Count);
        IReadOnlyList<RunView> running = RunHistory.Recent(None, 5, true, Utc(T0), 0x41301);
        T.True("running with no record shows one Running row", running.Count == 1 && running[0].Status == RunStatus.Running && running[0].When == T0);
    }

    public static void OnlySkippedRuns()
    {
        var recs = new List<RunRecord>
        {
            Rec(Ago(TimeSpan.FromDays(2)), RunStatus.Skipped, msg: "holiday: Veterans Day"),
            Rec(Ago(TimeSpan.FromDays(1)), RunStatus.Skipped, msg: "holiday: Thanksgiving Day"),
        };
        JobSummary s = RunHistory.Summarize(recs);
        T.True("no last result, no rate, no average", s.LastResult is null && s.SuccessRate is null && s.AverageRuntime is null);
        T.Eq("sample size 0", 0, s.SampleSize);
        T.Eq("last run at is the newest skip", (DateTimeOffset?)recs[1].WrapperStart, s.LastRunAt);
        IReadOnlyList<RunView> v = RunHistory.Recent(recs, 10, false, null, null);
        T.True("both rows listed, neither is a failure", v.Count == 2 && v.All(x => x.Status == RunStatus.Skipped && x.MessageIsInformational));
    }

    public static void MixedStatuses()
    {
        var recs = new List<RunRecord>
        {
            Rec(Ago(TimeSpan.FromHours(10)), RunStatus.Ok, 2),
            Rec(Ago(TimeSpan.FromHours(9)), RunStatus.Ok, 4),
            Rec(Ago(TimeSpan.FromHours(8)), RunStatus.Failed, 6, exit: 3),
            Rec(Ago(TimeSpan.FromHours(7)), RunStatus.TimedOut, 600, exit: 1),
            Rec(Ago(TimeSpan.FromHours(6)), RunStatus.LaunchError, msg: "no such file"),
            Rec(Ago(TimeSpan.FromHours(5)), RunStatus.Skipped, msg: "holiday: X"),
            Rec(Ago(TimeSpan.FromHours(4)), RunStatus.Ok, 3),
        };
        JobSummary s = RunHistory.Summarize(recs);
        T.Eq("sample = 6 (skip excluded)", 6, s.SampleSize);
        T.Eq("success = 3 ok of 6", (double?)0.5, s.SuccessRate);
        T.Eq("average = (2 + 4 + 6 + 3) / 4 s; timeout and launch error excluded", (TimeSpan?)TimeSpan.FromSeconds(3.75), s.AverageRuntime);
        T.Eq("last result Ok", (RunStatus?)RunStatus.Ok, s.LastResult);
        T.Eq("last run at is the newest record", (DateTimeOffset?)recs[6].WrapperStart, s.LastRunAt);

        var skipNewest = new List<RunRecord>(recs) { Rec(Ago(TimeSpan.FromHours(1)), RunStatus.Skipped, msg: "holiday: Y") };
        JobSummary s2 = RunHistory.Summarize(skipNewest);
        T.Eq("a newest skip does not replace the last result", (RunStatus?)RunStatus.Ok, s2.LastResult);
        T.Eq("but it is the last run", (DateTimeOffset?)skipNewest[7].WrapperStart, s2.LastRunAt);
        T.Eq("rate unchanged", (double?)0.5, s2.SuccessRate);
        T.Eq("recent puts the skip on top", RunStatus.Skipped, RunHistory.Recent(skipNewest, 3, false, null, null)[0].Status);
    }

    public static void TimeoutAndAverageRules()
    {
        var onlyBad = new List<RunRecord> { Rec(Ago(TimeSpan.FromHours(2)), RunStatus.TimedOut, 30, exit: 1), Rec(Ago(TimeSpan.FromHours(1)), RunStatus.LaunchError, msg: "x") };
        JobSummary s = RunHistory.Summarize(onlyBad);
        T.True("no Ok or Failed run means no average", s.AverageRuntime is null);
        T.Eq("success 0 of 2", (double?)0.0, s.SuccessRate);
        T.Eq("last result is the launch error", (RunStatus?)RunStatus.LaunchError, s.LastResult);
        IReadOnlyList<RunView> v = RunHistory.Recent(onlyBad, 5, false, null, null);
        T.Eq("a timed-out run still shows its duration in the list", (TimeSpan?)TimeSpan.FromSeconds(30), v[1].Duration);
    }

    public static void WindowIsFiftyNonSkippedRuns()
    {
        var recs = new List<RunRecord>();
        for (int i = 0; i < 10; i++) recs.Add(Rec(Ago(TimeSpan.FromHours(200 - i)), RunStatus.Failed, 1, exit: 1));    // oldest: 10 failures
        for (int i = 0; i < 50; i++) recs.Add(Rec(Ago(TimeSpan.FromHours(100 - i)), RunStatus.Ok, 2));                // then 50 successes
        for (int i = 0; i < 5; i++) recs.Add(Rec(Ago(TimeSpan.FromMinutes(50 - i)), RunStatus.Skipped, msg: "holiday: Z")); // newest: 5 skips
        JobSummary s = RunHistory.Summarize(recs);
        T.Eq("window holds 50 non-skipped runs (skips take no slot)", 50, s.SampleSize);
        T.Eq("the old failures fell out of the window", (double?)1.0, s.SuccessRate);
        T.Eq("average over the window", (TimeSpan?)TimeSpan.FromSeconds(2), s.AverageRuntime);

        JobSummary w10 = RunHistory.Summarize(recs, 10);
        T.Eq("custom window 10", 10, w10.SampleSize);
        JobSummary all = RunHistory.Summarize(recs, 1000);
        T.Eq("window larger than the history", 60, all.SampleSize);
        T.Eq("then 50 of 60 succeeded", 50.0 / 60.0, all.SuccessRate!.Value);
    }

    public static void OrderingDuplicatesAndTies()
    {
        var a = Rec(T0, RunStatus.Ok, 1, runId: "20261001-120000-000");
        var b = Rec(T0.AddMinutes(1), RunStatus.Failed, 1, runId: "20261001-120100-000");
        var c = Rec(T0.AddMinutes(-1), RunStatus.Ok, 1, runId: "20261001-115900-000");
        IReadOnlyList<RunView> v = RunHistory.Recent([c, a, b, a], 10, false, null, null);
        T.Eq("three rows: the duplicate run id appears once", 3, v.Count);
        T.True("sorted newest first whatever the input order", v[0].RunId == b.RunId && v[1].RunId == a.RunId && v[2].RunId == c.RunId);

        var tieLow = Rec(T0, RunStatus.Ok, 1, runId: "20261001-120000-001");
        var tieHigh = Rec(T0, RunStatus.Ok, 1, runId: "20261001-120000-002");
        IReadOnlyList<RunView> t = RunHistory.Recent([tieLow, tieHigh], 10, false, null, null);
        T.Eq("equal start times order by run id, newest first", "20261001-120000-002", t[0].RunId);

        // wrapperStart carries each machine's offset at the time: order by instant, not by wall clock text
        var est = Rec(new DateTimeOffset(2026, 11, 1, 1, 30, 0, TimeSpan.FromHours(-4)), RunStatus.Ok, 1, runId: "20261101-013000-000");
        var est2 = Rec(new DateTimeOffset(2026, 11, 1, 1, 10, 0, TimeSpan.FromHours(-5)), RunStatus.Ok, 1, runId: "20261101-011000-000");
        T.Eq("the second 01:30 (EST) is later than the first (EDT)", est2.RunId, RunHistory.Recent([est, est2], 5, false, null, null)[0].RunId);
    }

    public static void NotReportedSynthesis()
    {
        RunRecord last = Rec(T0, RunStatus.Ok, 1);
        int fileNotFound = unchecked((int)0x80070002);

        IReadOnlyList<RunView> v = RunHistory.Recent([last], 10, false, Utc(T0.AddMinutes(3)), fileNotFound);
        T.Eq("record plus a NotReported row", 2, v.Count);
        T.Eq("it is first", RunStatus.NotReported, v[0].Status);
        T.Eq("message is the launch-failure sentence (0x80070002 means Windows could not start it)", TaskResultCodes.DescribeLaunchFailure(fileNotFound), v[0].Message);
        T.Eq("a code that is not a launch failure gets TaskResultCodes.Describe", TaskResultCodes.Describe(0x41306),
            RunHistory.Recent([last], 10, false, Utc(T0.AddMinutes(3)), 0x41306)[0].Message);
        T.Eq("its time is Task Scheduler's LastRunTime", T0.AddMinutes(3).ToUniversalTime(), v[0].When.ToUniversalTime());
        T.Eq("exit code carries the result", (int?)fileNotFound, v[0].ExitCode);
        T.True("no duration, no output", v[0].Duration is null && !v[0].HasOutput && v[0].IsSynthetic);

        T.Eq("exactly 2 min newer is not enough", 1, RunHistory.Recent([last], 10, false, Utc(T0.AddMinutes(2)), 0).Count);
        T.Eq("1 s over 2 min is", 2, RunHistory.Recent([last], 10, false, Utc(T0.AddMinutes(2).AddSeconds(1)), 0).Count);
        T.Eq("older than the newest record is not", 1, RunHistory.Recent([last], 10, false, Utc(T0.AddMinutes(-30)), 0).Count);
        T.Eq("same instant is not", 1, RunHistory.Recent([last], 10, false, Utc(T0), 0).Count);

        string zero = RunHistory.Recent([last], 10, false, Utc(T0.AddHours(1)), 0)[0].Message!;
        T.Eq("result 0 gets the explanation (hidden jobs always report 0)", RunHistory.NotReportedNoCode, zero);
        T.Eq("unknown result gets the explanation", RunHistory.NotReportedNoCode, RunHistory.Recent([last], 10, false, Utc(T0.AddHours(1)), null)[0].Message);

        T.Eq("no records at all but Task Scheduler ran it: NotReported", RunStatus.NotReported, RunHistory.Recent(None, 10, false, Utc(T0), 1)[0].Status);
        T.Eq("the NotReported row does not count against records beyond max", 1, RunHistory.Recent([last], 1, false, Utc(T0.AddHours(1)), 0).Count);
    }

    /// <summary>
    /// Checkpoint B, Finding A: Windows (antivirus) can refuse to start the wrapper. Task Scheduler then reports 0x80070005 and no record
    /// exists. That must read as a plain "Windows refused to start this job" row at once, even for a job that runs every minute.
    /// </summary>
    public static void LaunchBlockedByWindowsIsReportedAtOnce()
    {
        int denied = unchecked((int)0x80070005);
        RunRecord last = Rec(T0, RunStatus.Ok, 1);

        IReadOnlyList<RunView> v = RunHistory.Recent([last], 10, false, Utc(T0.AddMinutes(1)), denied);
        T.Eq("a blocked run one minute after the last record: two rows", 2, v.Count);
        T.Eq("the blocked run is on top as NotReported", RunStatus.NotReported, v[0].Status);
        T.Eq("plain wording that states the code and no cause", "Windows refused to start this job: access denied (0x80070005).", v[0].Message);
        T.Eq("the code is kept for the details view", (int?)denied, v[0].ExitCode);
        T.Eq("no records at all: still reported", RunStatus.NotReported, RunHistory.Recent(None, 10, false, Utc(T0), denied)[0].Status);
        T.Eq("it counts as a failure", (double?)0.5, RunHistory.Summarize(v).SuccessRate);

        T.Eq("a record of this very run (wrapper started 1 s after) means it was not blocked", 1,
            RunHistory.Recent([Rec(T0.AddMinutes(1).AddSeconds(1), RunStatus.Ok, 1)], 10, false, Utc(T0.AddMinutes(1)), denied).Count);
        T.Eq("3 s of slack is more than the 2 s tolerance: reported", 2,
            RunHistory.Recent([last], 10, false, Utc(T0.AddSeconds(3)), denied).Count);
        T.Eq("a plain exit code 5 (visible mode) keeps the 2-minute grace", 1,
            RunHistory.Recent([last], 10, false, Utc(T0.AddMinutes(1)), 5).Count);
        T.Eq("result 0 keeps the 2-minute grace", 1, RunHistory.Recent([last], 10, false, Utc(T0.AddMinutes(1)), 0).Count);
        T.True("a running task is never 'blocked'", RunHistory.Recent([last], 10, true, Utc(T0.AddMinutes(1)), denied).All(x => x.Status != RunStatus.NotReported));
    }

    public static void RunningSynthesis()
    {
        RunRecord last = Rec(T0, RunStatus.Ok, 1);
        DateTime started = Utc(T0.AddHours(1));

        IReadOnlyList<RunView> v = RunHistory.Recent([last], 10, true, started, 0x41301);
        T.Eq("record plus a Running row", 2, v.Count);
        T.True("Running first, no duration, synthetic", v[0].Status == RunStatus.Running && v[0].Duration is null && v[0].IsSynthetic);
        T.Eq("its time is the start Task Scheduler reports", T0.AddHours(1).ToUniversalTime(), v[0].When.ToUniversalTime());

        RunRecord same = Rec(T0.AddHours(1).AddSeconds(1), RunStatus.Ok, 1);
        T.Eq("a record from this very run (wrapper starts a moment after Task Scheduler) suppresses Running: just the 2 records", 2,RunHistory.Recent([last, same], 10, true, started, 0x41301).Count);
        RunRecord slightlyBefore = Rec(T0.AddHours(1).AddSeconds(-1), RunStatus.Ok, 1);
        T.Eq("within the 2 s tolerance counts as this run", 1, RunHistory.Recent([slightlyBefore], 10, true, started, 0x41301).Count);
        RunRecord wayBefore = Rec(T0.AddHours(1).AddSeconds(-10), RunStatus.Ok, 1);
        T.Eq("10 s before is the previous run, so Running shows", 2, RunHistory.Recent([wayBefore], 10, true, started, 0x41301).Count);

        T.True("Running wins over NotReported: a running task is never 'not reported'", RunHistory.Recent([last], 10, true, started, 0x41301).All(x => x.Status != RunStatus.NotReported));
        T.Eq("not running, no gap: nothing synthesized", 1, RunHistory.Recent([same], 10, false, started, 0).Count);
    }

    public static void MaxCountsSyntheticRows()
    {
        var recs = new List<RunRecord> { Rec(Ago(TimeSpan.FromHours(3)), RunStatus.Ok, 1), Rec(Ago(TimeSpan.FromHours(2)), RunStatus.Ok, 1), Rec(Ago(TimeSpan.FromHours(1)), RunStatus.Ok, 1) };
        IReadOnlyList<RunView> v = RunHistory.Recent(recs, 2, true, Utc(T0), 0x41301);
        T.Eq("two rows in all", 2, v.Count);
        T.True("Running plus the newest record", v[0].Status == RunStatus.Running && v[1].RunId == recs[2].RunId);
    }

    public static void NotReportedCountsAsFailure()
    {
        var recs = new List<RunRecord> { Rec(Ago(TimeSpan.FromHours(4)), RunStatus.Ok, 2), Rec(Ago(TimeSpan.FromHours(3)), RunStatus.Ok, 2), Rec(Ago(TimeSpan.FromHours(2)), RunStatus.Ok, 2) };
        IReadOnlyList<RunView> views = RunHistory.Recent(recs, 50, false, Utc(T0), 0);
        T.Eq("NotReported on top", RunStatus.NotReported, views[0].Status);
        JobSummary s = RunHistory.Summarize(views);
        T.Eq("4 counted runs", 4, s.SampleSize);
        T.Eq("3 of 4 succeeded", (double?)0.75, s.SuccessRate);
        T.Eq("last result is the not-reported run", (RunStatus?)RunStatus.NotReported, s.LastResult);
        T.Eq("average ignores it", (TimeSpan?)TimeSpan.FromSeconds(2), s.AverageRuntime);

        var running = RunHistory.Recent(recs, 50, true, Utc(T0), 0x41301);
        JobSummary r = RunHistory.Summarize(running);
        T.Eq("a Running row is not a sample", 3, r.SampleSize);
        T.Eq("and not the last result", (RunStatus?)RunStatus.Ok, r.LastResult);

        // a NotReported record handed to the record overload counts the same way
        var withStatus = new List<RunRecord> { recs[0], recs[1], Rec(T0, RunStatus.NotReported) };
        T.Eq("record overload: NotReported is a failure", (double?)(2.0 / 3.0), RunHistory.Summarize(withStatus).SuccessRate);
    }

    public static void RetentionWindow()
    {
        var recs = new List<RunRecord>
        {
            Rec(Ago(TimeSpan.FromDays(40)), RunStatus.Failed, 9, exit: 1),
            Rec(Ago(TimeSpan.FromDays(35)), RunStatus.Failed, 9, exit: 1),
            Rec(Ago(TimeSpan.FromDays(2)), RunStatus.Ok, 1),
            Rec(Ago(TimeSpan.FromDays(1)), RunStatus.Ok, 3),
        };
        DateTimeOffset cutoff = Ago(TimeSpan.FromDays(30));
        JobSummary all = RunHistory.Summarize(recs);
        T.Eq("without a cut-off all 4 count", 4, all.SampleSize);
        JobSummary cut = RunHistory.Summarize(recs, 50, cutoff);
        T.Eq("old runs left out", 2, cut.SampleSize);
        T.Eq("rate over what is left", (double?)1.0, cut.SuccessRate);
        T.Eq("average over what is left", (TimeSpan?)TimeSpan.FromSeconds(2), cut.AverageRuntime);
        T.Eq("recent drops them too", 2, RunHistory.Recent(recs, 10, false, null, null, cutoff).Count);
        T.Eq("a record exactly at the cut-off stays", 1, RunHistory.Recent([Rec(cutoff, RunStatus.Ok, 1)], 10, false, null, null, cutoff).Count);

        JobSummary none = RunHistory.Summarize(recs, 50, T0);
        T.True("everything older than the cut-off gives the empty summary", none.SampleSize == 0 && none.LastRunAt is null && none.SuccessRate is null);
    }

    public static void InFlightRunWithoutAnEndRecord()
    {
        // The wrapper writes its record in `finally`, so a run in progress has NO record at all; a half-written line is skipped by the parser.
        string halfWritten = HistorySamples.Fail[..140];
        List<RunRecord> parsed = RunRecordParser.ParseText(HistorySamples.Timeout + "\n" + halfWritten, "job00001");
        T.Eq("the half-written line is ignored", 1, parsed.Count);
        IReadOnlyList<RunView> v = RunHistory.Recent(parsed, 10, true, new DateTime(2026, 10, 1, 3, 0, 0, DateTimeKind.Utc), 0x41301);
        T.Eq("Running on top of the previous run", RunStatus.Running, v[0].Status);
        T.Eq("the previous record is below", RunStatus.TimedOut, v[1].Status);

        // a record that has a start but no end (should not happen, but never crash): no duration, still counted by status
        RunRecord noEnd = RunRecordParser.ParseLine("""{"runId":"a","wrapperStart":"2026-10-01T12:00:00-04:00","start":"2026-10-01T12:00:01-04:00","status":"ok"}""", "job00001")!;
        JobSummary s = RunHistory.Summarize([noEnd]);
        T.True("counted as ok, no average", s.SuccessRate == 1.0 && s.AverageRuntime is null && s.SampleSize == 1);
    }

    public static void UnicodeOutputPreviewsAndMessages()
    {
        string msg = "Fehler: größe überschritten – 日本語 😀 é";
        string json = "{\"runId\":\"20261001-120000-000\",\"wrapperStart\":\"2026-10-01T12:00:00.0000000-04:00\",\"status\":\"launchError\",\"msg\":\"" + msg + "\"}";
        RunRecord r = RunRecordParser.ParseLine(json, "job00001")!;
        RunView v = RunHistory.Recent([r], 5, false, null, null)[0];
        T.Eq("message passes through unchanged", msg, v.Message);
    }

    public static void ManyRecordsIsFast()
    {
        var recs = new List<RunRecord>(50_000);
        for (int i = 0; i < 50_000; i++)
            recs.Add(Rec(T0.AddMinutes(-i), i % 7 == 0 ? RunStatus.Failed : i % 11 == 0 ? RunStatus.Skipped : RunStatus.Ok, 1.5, runId: "r" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        JobSummary s = RunHistory.Summarize(recs);
        IReadOnlyList<RunView> v = RunHistory.Recent(recs, 20, false, null, null);
        sw.Stop();
        T.Eq("window still 50", 50, s.SampleSize);
        T.Eq("20 rows", 20, v.Count);
        T.True("50,000 records summarized in under 1 s (took " + sw.ElapsedMilliseconds + " ms)", sw.ElapsedMilliseconds < 1000);
    }
}
