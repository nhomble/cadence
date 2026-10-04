using Cadence.Agent.Audit;
using Cadence.Agent.Policy;
using Cadence.Core.Execution;
using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.History;
using Cadence.Scheduler.Apply;

namespace Cadence.App.UI.Services;

/// <summary>One schedule of the managed folder with what the list needs: the classified task, its run history and its next real run.</summary>
public sealed record ManagedEntry(ClassifiedTask Task, JobHistory History, DateTimeOffset? Next);

/// <summary>The managed folder as of <see cref="LoadedAt"/>. <see cref="ForeignInFolder"/> are tasks inside \Cadence\ that Cadence does not manage (shown with the other tasks).</summary>
public sealed record ManagedSnapshot(IReadOnlyList<ManagedEntry> Entries, IReadOnlyList<ClassifiedTask> ForeignInFolder, DateTimeOffset LoadedAt);

/// <summary>The Planner's verdict on a draft save: <see cref="Summary"/> is the inline change text ("Will update: time 18:00 -> 18:30") when <see cref="Ok"/>, else a plain sentence about why it can't be saved.</summary>
public sealed record SavePlan(bool Ok, string Summary);

/// <summary>Small persisted user settings (HKCU\Software\Cadence). Reading never creates anything.</summary>
public interface IUserSettings
{
    bool ShowForeign { get; set; }

    /// <summary>The main window's position and size as <see cref="Cadence.App.UI.Logic.WindowPlacement.Format"/> writes it; null until a window was closed once.</summary>
    string? WindowPlacement { get; set; }

    /// <summary>[CP-D] The time of the agent-switch change the owner last acknowledged (by opening the ⋯ menu); the Orange "turned back on" note shows only for a newer change.</summary>
    DateTimeOffset? AgentNoteSeenAt { get => null; set { } }

    /// <summary>[P25] The table's sort as <see cref="Cadence.App.UI.Logic.SortSpec.Format"/> writes it ("rate:desc"); null or garbage means the default.</summary>
    string? ListSort { get => null; set { } }

    /// <summary>[P25] The columns the user hid from the header menu, a comma list of column ids; null when none.</summary>
    string? ListHiddenColumns { get => null; set { } }
}

/// <summary>
/// What the view models know about Task Scheduler, the run files and the clock (PLAN Phase 12, [CP-B] rules). Every member that reaches
/// Task Scheduler or the disk is async and runs on the thread pool, so no COM call can ever run on the UI thread; writes are serialized.
/// The real implementation is <see cref="RealAppServices"/>; the test project has fixtures that serve deterministic data without Task Scheduler.
/// </summary>
public interface IAppServices
{
    /// <summary>"Cadence" or "CadenceTest".</summary>
    string FolderName { get; }

    /// <summary>The current time (a fixed instant in the test fixtures).</summary>
    DateTimeOffset Now { get; }

    /// <summary>The time zone every displayed time is converted to.</summary>
    TimeZoneInfo Zone { get; }

    IUserSettings Settings { get; }

    /// <summary>What the drop interpreter asks about the machine (files, PATH, shortcuts). The test fixtures answer with a pretend machine.</summary>
    IFileProbe FileProbe { get; }

    /// <summary>The folder where run files live (it may not exist yet).</summary>
    string RunsFolderPath { get; }

    /// <summary>The managed folder, classified, with history and next runs. A missing folder is simply empty (nothing is created).</summary>
    Task<ManagedSnapshot> LoadManagedAsync(CancellationToken cancellation = default);

    /// <summary>The read-only tasks of the rest of Task Scheduler. Cached after the first call until <paramref name="refresh"/> is true.</summary>
    Task<IReadOnlyList<ClassifiedTask>> LoadOthersAsync(bool refresh, CancellationToken cancellation = default);

    /// <param name="expectedRevision">The <see cref="Cadence.Core.Planning.JobRevision"/> of the task as the editor loaded it (existing jobs only). The save is
    /// refused (Stale) when the task changed since; the message names who changed it and when. Null: no check.</param>
    Task<ApplyResult> SaveAsync(JobDefinition job, string? existingTaskName, string? expectedRevision = null);

    /// <summary>Plans (reads only, writes nothing) what saving <paramref name="job"/> would do, for the editor's footer. Never throws for an expected problem.</summary>
    Task<SavePlan> PlanSaveAsync(JobDefinition job, string? existingTaskName);

    /// <summary>The newest <paramref name="max"/> runs of a managed job and its summary (the editor's runs panel; "Show more" asks for more).</summary>
    Task<JobHistory> LoadHistoryAsync(ClassifiedTask task, int max);

    /// <summary>The captured output (or errors) of one run; null when nothing was captured or the file is gone.</summary>
    Task<string?> ReadOutputAsync(string jobId, string runId, bool stderr);

    /// <summary>Deletes the task, then its run folder.</summary>
    Task<ApplyResult> DeleteAsync(ClassifiedTask task);

    Task<ApplyResult> ReapplyAsync(string taskName);

    Task<ApplyResult> UntrackAsync(string taskName);

    Task<ApplyResult> SetEnabledAsync(ClassifiedTask task, bool enabled);

    /// <summary>Starts the task now. Returns null on success, else a plain sentence.</summary>
    Task<string?> RunNowAsync(string taskName);

    /// <summary>Deletes every Cadence-managed task of the folder (not other tasks) and their run folders.</summary>
    Task<ApplyResult> DeleteAllManagedAsync();

    // ---- the agent surface (PLAN 12.7, Phase 23): the owner's side ----

    /// <summary>The agent policy (switch and limits) as the registry holds it right now. Never throws: an unreadable setting is <see cref="AgentPolicy.Unreadable"/>.</summary>
    Task<AgentPolicy> LoadAgentPolicyAsync();

    /// <summary>Turns the switch on or off, recorded as "app" with an audit line. Throws <see cref="IOException"/>/<see cref="UnauthorizedAccessException"/> when the registry can't be written.</summary>
    Task SetAgentsSuspendedAsync(bool suspended);

    /// <summary>Sets the limits that are not null (the same checks as <c>--agents config</c>); audited.</summary>
    Task SetAgentLimitsAsync(int? minIntervalMinutes, int? maxAgentJobs);

    /// <summary>Locks or unlocks one job against AI agents: a normal save by the app (revision-checked), audited. Existing, in-sync jobs only.</summary>
    Task<ApplyResult> SetAgentLockAsync(ClassifiedTask task, bool locked);

    /// <summary>The newest <paramref name="max"/> lines of the agent log, newest FIRST (both log files merged); empty when there is no log. Runs on the thread pool.</summary>
    Task<IReadOnlyList<AuditEntry>> LoadAgentLogAsync(int max);

    /// <summary>The agent log file (shown in the Agent activity panel); it may not exist yet.</summary>
    string AgentLogPath { get; }

    /// <summary>The folder that holds the agent log files.</summary>
    string AgentLogFolder { get; }

    bool AgentLogExists { get; }

    /// <summary>Once per start: removes run folders of jobs that no longer exist (older than 30 days).</summary>
    Task PruneOrphansAsync(IReadOnlyCollection<string> liveJobIds);
}
