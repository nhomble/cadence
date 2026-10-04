using System.Globalization;
using Cadence.Core.TaskXml;

namespace Cadence.Core.History;

/// <summary>
/// Turns run records plus Task Scheduler's own state into what the editor shows (PLAN 3.13). Pure: the caller passes the records, Task
/// Scheduler's state and (through <c>notBefore</c>) the retention cut-off, so nothing here reads a clock or the disk.
/// </summary>
public static class RunHistory
{
    /// <summary>NotReported: Task Scheduler's LastRunTime must be MORE than this much newer than the newest record's wrapperStart.</summary>
    public static readonly TimeSpan NotReportedGrace = TimeSpan.FromMinutes(2);

    /// <summary>Running: a record whose wrapperStart is within this much before Task Scheduler's LastRunTime still counts as that run's record.</summary>
    public static readonly TimeSpan RunningTolerance = TimeSpan.FromSeconds(2);

    public const int DefaultWindow = 50;

    /// <summary>The note on a NotReported row when Task Scheduler's result is 0 (hidden jobs always show 0, because conhost swallows the real code) or unknown.</summary>
    public const string NotReportedNoCode = "Task Scheduler started this job, but it left no run record. The program may not have started (for example PowerShell is blocked) or the task was edited outside Cadence.";

    /// <summary>
    /// Newest first, at most <paramref name="max"/> rows. Synthesized rows come first: <b>Running</b> when Task Scheduler says the task is
    /// running and no record is newer than its LastRunTime; otherwise <b>NotReported</b> when LastRunTime is more than
    /// <see cref="NotReportedGrace"/> newer than the newest record's wrapperStart (only <see cref="RunningTolerance"/> when the result is a
    /// launch failure code; the message is <see cref="TaskResultCodes.DescribeLaunchFailure"/>, else <see cref="TaskResultCodes.Describe"/>). Records older than <paramref name="notBefore"/> are dropped first, and a duplicate
    /// runId appears once. <paramref name="tsLastRunTime"/> should be Utc or Local kind (Unspecified is read as local); a value before the year
    /// 2000 (Task Scheduler's "never ran" date) is ignored.
    /// </summary>
    public static IReadOnlyList<RunView> Recent(IReadOnlyList<RunRecord> records, int max, bool tsRunningNow, DateTime? tsLastRunTime, int? tsLastResult,
        DateTimeOffset? notBefore = null)
    {
        if (max <= 0) return [];
        List<RunRecord> sorted = Ordered(records, notBefore);
        RunRecord? newest = sorted.Count > 0 ? sorted[0] : null;

        DateTimeOffset? ts = tsLastRunTime is { Year: >= 2000 } t ? new DateTimeOffset(t) : null;
        var views = new List<RunView>(Math.Min(max, sorted.Count + 1));

        if (tsRunningNow)
        {
            bool recorded = ts is { } r && newest is not null && newest.WrapperStart >= r - RunningTolerance;
            if (!recorded)
            {
                DateTimeOffset when = ts ?? newest?.WrapperStart ?? DateTimeOffset.MinValue;
                views.Add(new RunView("running-" + Stamp(when), when, null, RunStatus.Running, null, "Running now", false));
            }
        }
        else if (ts is { } last && (newest is null || last - newest.WrapperStart > GraceFor(tsLastResult)))
        {
            string message = tsLastResult is null or 0
                ? NotReportedNoCode
                : TaskResultCodes.DescribeLaunchFailure(tsLastResult.Value) ?? TaskResultCodes.Describe(tsLastResult.Value);
            views.Add(new RunView("notreported-" + Stamp(last), last, null, RunStatus.NotReported, tsLastResult, message, false));
        }

        foreach (RunRecord rec in sorted)
        {
            if (views.Count >= max) break;
            views.Add(ToView(rec));
        }
        if (views.Count > max) views.RemoveRange(max, views.Count - max);
        return views;
    }

    /// <summary>
    /// Summary over the newest <paramref name="window"/> NON-skipped records (see <see cref="JobSummary"/>). Success % = Ok / (Ok + Failed +
    /// TimedOut + LaunchError); a record carrying <see cref="RunStatus.NotReported"/> counts as a failure and <see cref="RunStatus.Running"/> is
    /// ignored. Average runtime = mean duration of Ok and Failed runs only. Records older than <paramref name="notBefore"/> are ignored.
    /// </summary>
    public static JobSummary Summarize(IReadOnlyList<RunRecord> records, int window = DefaultWindow, DateTimeOffset? notBefore = null) =>
        SummarizeCore(Ordered(records, notBefore).Select(r => new Sample(r.WrapperStart, r.Status, r.Duration)), window);

    /// <summary>
    /// The same summary over rows from <see cref="Recent"/>, so a synthesized NotReported row counts as a failure. Pass at least
    /// <paramref name="window"/> rows (plus any skipped ones) to <c>Recent</c>.
    /// </summary>
    public static JobSummary Summarize(IReadOnlyList<RunView> views, int window = DefaultWindow) =>
        SummarizeCore(views.OrderByDescending(v => v.When).Select(v => new Sample(v.When, v.Status, v.Duration)), window);

    readonly record struct Sample(DateTimeOffset When, RunStatus Status, TimeSpan? Duration);

    static JobSummary SummarizeCore(IEnumerable<Sample> newestFirst, int window)
    {
        if (window < 1) window = 1;
        DateTimeOffset? lastRunAt = null;
        RunStatus? lastResult = null;
        int counted = 0, ok = 0;
        long okFailTicks = 0;
        int okFailN = 0;

        foreach (Sample s in newestFirst)
        {
            lastRunAt ??= s.When;
            if (s.Status is RunStatus.Skipped) continue;
            if (s.Status is RunStatus.Running) continue;
            if (counted >= window) break;
            counted++;
            lastResult ??= s.Status;
            if (s.Status == RunStatus.Ok) ok++;
            if (s.Status is RunStatus.Ok or RunStatus.Failed && s.Duration is { } d)
            {
                okFailTicks += d.Ticks;
                okFailN++;
            }
        }

        if (lastRunAt is null) return JobSummary.Empty;
        return new JobSummary(
            lastResult,
            lastRunAt,
            okFailN > 0 ? TimeSpan.FromTicks(okFailTicks / okFailN) : null,
            counted > 0 ? (double)ok / counted : null,
            counted);
    }

    /// <summary>Newest first (ties by runId, which sorts chronologically), duplicates of a runId removed, records before <paramref name="notBefore"/> dropped.</summary>
    static List<RunRecord> Ordered(IReadOnlyList<RunRecord> records, DateTimeOffset? notBefore)
    {
        var list = new List<RunRecord>(records.Count);
        foreach (RunRecord r in records)
            if (r is not null && (notBefore is null || r.WrapperStart >= notBefore.Value)) list.Add(r);
        list.Sort((a, b) =>
        {
            int c = b.WrapperStart.CompareTo(a.WrapperStart);
            return c != 0 ? c : string.CompareOrdinal(b.RunId, a.RunId);
        });
        var seen = new HashSet<string>(StringComparer.Ordinal);
        list.RemoveAll(r => !seen.Add(r.RunId));
        return list;
    }

    /// <summary>
    /// A launch failure code (<see cref="TaskResultCodes.IsLaunchFailure"/>) is proof that no wrapper ran, so the run is reported at once
    /// (only <see cref="RunningTolerance"/> of clock slack); otherwise the 2-minute grace applies. Without this, a blocked job that runs more
    /// often than every 2 minutes would never show as "Didn't report" while an older record exists (Checkpoint B).
    /// </summary>
    static TimeSpan GraceFor(int? tsLastResult) =>
        tsLastResult is { } code && TaskResultCodes.IsLaunchFailure(code) ? RunningTolerance : NotReportedGrace;

    static RunView ToView(RunRecord r) =>
        new(r.RunId, r.WrapperStart, r.Duration, r.Status, r.ExitCode, r.Message, r.OutBytes > 0 || r.ErrBytes > 0);

    static string Stamp(DateTimeOffset t) => t.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
}
