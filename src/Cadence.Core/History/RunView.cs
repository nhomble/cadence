namespace Cadence.Core.History;

/// <summary>
/// One row of the editor's recent-runs panel. <see cref="When"/> is the wrapper start (when Task Scheduler started the job).
/// <see cref="Duration"/> is null for skipped runs, launch errors and the synthesized rows.
/// </summary>
public sealed record RunView(string RunId, DateTimeOffset When, TimeSpan? Duration, RunStatus Status, int? ExitCode, string? Message, bool HasOutput)
{
    /// <summary>True for the rows <see cref="RunHistory.Recent"/> made up from Task Scheduler's state (Running, NotReported).</summary>
    public bool IsSynthetic => Status is RunStatus.Running or RunStatus.NotReported;

    /// <summary>True when <see cref="Message"/> is a note (holiday name, output partly captured) and not an error text.</summary>
    public bool MessageIsInformational => RunMessages.IsInformational(Status, Message);
}
