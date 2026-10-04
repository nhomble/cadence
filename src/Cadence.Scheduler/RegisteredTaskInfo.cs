namespace Cadence.Scheduler;

/// <summary>
/// A snapshot of one registered task. <c>Xml</c> is the definition as Task Scheduler stores it (normalized; compare parsed models, never text).
/// Dates are local wall-clock times; "never" (year before 2000) is null. <c>LastTaskResult</c> is null when the task has never run
/// (Task Scheduler reports 0x41303 then).
/// </summary>
public sealed record RegisteredTaskInfo(string Name, string Path, string Xml, TaskState State, bool Enabled,
    DateTime? LastRunTime, int? LastTaskResult, DateTime? NextRunTime, int NumberOfMissedRuns);
