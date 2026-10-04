namespace Cadence.Core.Planning;

/// <summary>A task as read from Task Scheduler (PLAN 3.12). <c>Path</c> is the full task path (\Folder\Name); dates are local wall-clock.</summary>
public sealed record ObservedTask(string Name, string Path, string Xml, int State, DateTime? LastRunTime, int? LastTaskResult, DateTime? NextRunTime);
