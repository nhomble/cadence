namespace Cadence.Scheduler;

/// <summary>
/// Where the gateway keeps its tasks. The product uses the defaults (<c>\Cadence\</c>); integration tests ALWAYS pass
/// <c>new SchedulerOptions("CadenceTest", "CadenceTest")</c> so that no test can touch the real folder.
/// </summary>
public sealed record SchedulerOptions(string FolderName = "Cadence", string RunsRootName = "Cadence");
