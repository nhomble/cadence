namespace Cadence.Core.Model;

public sealed record Repetition(TimeSpan Interval, TimeSpan? Duration, bool StopAtDurationEnd);  // Duration null = indefinite

public abstract record TaskTrigger
{
    public DateTime StartBoundary { get; init; }       // Kind=Unspecified local wall clock
    public DateTimeOffset? StartBoundaryOffset { get; init; } // only when the XML carried an offset/Z (foreign tasks)
    public DateTime? EndBoundary { get; init; }
    public bool Enabled { get; init; } = true;
    public Repetition? Repetition { get; init; }
    public TimeSpan? RandomDelay { get; init; }
    public TimeSpan? ExecutionTimeLimit { get; init; }
    public string? Id { get; init; }
}

public sealed record TimeTrigger : TaskTrigger;
public sealed record DailyTrigger(int DaysInterval) : TaskTrigger;
public sealed record WeeklyTrigger(int WeeksInterval, WeekDays Days) : TaskTrigger;
public sealed record MonthlyTrigger(ValueList<int> Days, bool LastDay, MonthSet Months) : TaskTrigger;
public sealed record MonthlyDowTrigger(ValueList<WeekOfMonth> Weeks, WeekDays Days, MonthSet Months) : TaskTrigger;
public sealed record LogonTrigger(string? UserId, TimeSpan? Delay) : TaskTrigger;
public sealed record UnsupportedTrigger(string ElementName, string RawXml) : TaskTrigger;   // Boot, Idle, Event, Registration, SessionStateChange... preserved verbatim
