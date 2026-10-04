using Cadence.Core.History;
using Cadence.Core.Planning;

namespace Cadence.History;

/// <summary>What the editor's runs panel shows for one job: the newest rows (synthesized Running / NotReported first) and the summary above them.</summary>
public sealed record JobHistory(IReadOnlyList<RunView> Recent, JobSummary Summary)
{
    public static JobHistory Empty { get; } = new([], JobSummary.Empty);
}

/// <summary>
/// Combines the wrapper's run records (<see cref="RunRecordStore"/>) with Task Scheduler's own state from the <see cref="ObservedTask"/>
/// of a classified job (PLAN 3.13). Foreign or corrupt tasks (no job id) have no history.
/// </summary>
public sealed class HistoryService
{
    readonly RunRecordStore _store;
    readonly DateTimeOffset? _notBefore;

    /// <param name="store">Where the wrapper writes (the runs root of the gateway's options, not necessarily the real %LOCALAPPDATA%\Cadence).</param>
    /// <param name="notBefore">Optional retention cut-off: older records are ignored (the wrapper's file is never rewritten from here).</param>
    public HistoryService(RunRecordStore store, DateTimeOffset? notBefore = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _notBefore = notBefore;
    }

    /// <summary>
    /// The newest <paramref name="max"/> rows of the job and a summary over the newest <see cref="RunHistory.DefaultWindow"/> counted runs
    /// (a synthesized NotReported row counts as a failure, Running and skipped rows do not count).
    /// </summary>
    public JobHistory Load(ClassifiedTask job, int max)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (job.Job is not { } def || max <= 0) return JobHistory.Empty;

        ObservedTask t = job.Observed;
        int lines = Math.Max(max, RunHistory.DefaultWindow) * 2;
        IReadOnlyList<RunRecord> records = _store.ReadTail(def.Id, t.Path, lines);
        bool running = t.State == 4;   // TASK_STATE_RUNNING

        // Rows for the summary window (plus any skipped ones) are built first; the panel then takes its first `max`.
        IReadOnlyList<RunView> all = RunHistory.Recent(records, int.MaxValue, running, t.LastRunTime, t.LastTaskResult, _notBefore);
        JobSummary summary = RunHistory.Summarize(all, RunHistory.DefaultWindow);
        IReadOnlyList<RunView> recent = all.Count <= max ? all : all.Take(max).ToList();
        return new JobHistory(recent, summary);
    }
}
