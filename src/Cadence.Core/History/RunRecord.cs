namespace Cadence.Core.History;

/// <summary>
/// What a run looks like. <see cref="Ok"/> .. <see cref="Skipped"/> come from the wrapper's run record; <see cref="Running"/> and
/// <see cref="NotReported"/> are synthesized by <see cref="RunHistory.Recent"/> from Task Scheduler's own state (PLAN 3.13).
/// </summary>
public enum RunStatus { Ok, Failed, TimedOut, LaunchError, Skipped, Running, NotReported }

/// <summary>
/// One line of a job's <c>runs.jsonl</c> (PLAN Appendix A). <paramref name="OutBytes"/> and <paramref name="ErrBytes"/> are .NET string
/// LENGTHS (UTF-16 characters of the captured output before the 256 KB tail cut), not bytes: label them "characters" or do not show them.
/// </summary>
public sealed record RunRecord(string JobId, string TaskPath, string RunId, DateTimeOffset WrapperStart,
    DateTimeOffset? Start, DateTimeOffset? End, int? ExitCode, RunStatus Status, int? Pid, long OutBytes, long ErrBytes, string? Message)
{
    /// <summary>Wrapper message that accompanies ok/fail/timeout when a program the job started still held the output pipes.</summary>
    public const string OutputNotFullyCapturedPrefix = "output not fully captured";

    /// <summary>Wrapper message when the holiday list ran out and the job ran anyway.</summary>
    public const string HolidayListExpiredPrefix = "holiday list expired";

    /// <summary>
    /// <c>End - Start</c> of the program (wrapper overhead excluded). Null for skipped runs and launch errors, when a timestamp is
    /// missing, and when the clock went backwards (End before Start).
    /// </summary>
    public TimeSpan? Duration
    {
        get
        {
            if (Status is RunStatus.Skipped or RunStatus.LaunchError or RunStatus.Running or RunStatus.NotReported) return null;
            if (Start is not { } s || End is not { } e) return null;
            TimeSpan d = e - s;
            return d < TimeSpan.Zero ? null : d;
        }
    }

    /// <summary>True when <see cref="Message"/> is a note about a run that otherwise went as reported (not an error text).</summary>
    public bool MessageIsInformational => RunMessages.IsInformational(Status, Message);

    /// <summary>The program's output was captured only partly (a child it started kept the pipes open). Status and exit code are still right.</summary>
    public bool OutputIncomplete => Message is { } m && m.StartsWith(OutputNotFullyCapturedPrefix, StringComparison.Ordinal);
}

/// <summary>Classifies wrapper messages.</summary>
public static class RunMessages
{
    /// <summary>
    /// Skipped runs carry <c>holiday: NAME</c>; an ok/fail/timeout run may carry "output not fully captured..." or "holiday list expired;
    /// ran anyway". None of these is a failure. Launch errors and NotReported carry real error text.
    /// </summary>
    public static bool IsInformational(RunStatus status, string? message)
    {
        if (string.IsNullOrEmpty(message)) return false;
        switch (status)
        {
            case RunStatus.Skipped:
                return true;
            case RunStatus.Ok:
            case RunStatus.Failed:
            case RunStatus.TimedOut:
                return message.StartsWith(RunRecord.OutputNotFullyCapturedPrefix, StringComparison.Ordinal)
                    || message.StartsWith(RunRecord.HolidayListExpiredPrefix, StringComparison.Ordinal);
            default:
                return false;
        }
    }
}
