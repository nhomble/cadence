namespace Cadence.Core.Scheduling;

/// <summary>One predicted run. <see cref="At"/> carries the offset in force in the evaluation time zone.</summary>
public readonly record struct Occurrence(
    DateTimeOffset At,
    int TriggerIndex,
    bool DstAdjusted,
    TimeSpan RandomDelayUpTo,
    string? SkippedHoliday = null);
