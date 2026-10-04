using Cadence.Core.Model;

namespace Cadence.Core.Cron;

// Internal representation (PLAN.md 3.3): derived from cron by the compiler, never stored.

public abstract record DayRule;
public sealed record EveryDay : DayRule;
public sealed record WeeklyOn(WeekDays Days) : DayRule;
public sealed record MonthlyOnDays(ValueList<int> Days, bool LastDay, MonthSet Months) : DayRule;
public sealed record MonthlyOnWeekdays(ValueList<WeekOfMonth> Weeks, WeekDays Days, MonthSet Months) : DayRule;

public abstract record TimeRule;
public sealed record AtTimes(ValueList<TimeOnly> Times) : TimeRule;                          // 1..48, sorted
public sealed record RepeatEvery(TimeSpan Every, TimeOnly First, TimeOnly Last) : TimeRule;  // runs First, First+Every, ..., Last (inclusive)

/// <summary>One day rule with one time rule. A cron line can give several groups (OR semantics, strategy C/D).</summary>
public sealed record ScheduleGroup(DayRule Days, TimeRule Times);
