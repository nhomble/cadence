using Cadence.Core.Execution;
using Cadence.Core.Metadata;
using Cadence.Core.Model;
using Cadence.Core.Planning;

namespace Cadence.Scheduler;

/// <summary>
/// The facade the UI and the Applier use: reads tasks through the gateway, classifies them (<see cref="Classifier"/>) with the right compose
/// context, and plans changes (<see cref="Planner"/>) from the LIVE state. Nothing here writes to Task Scheduler.
/// </summary>
public sealed class ManagedJobs
{
    readonly ITaskSchedulerGateway _gateway;
    readonly Func<DateTimeOffset> _clock;

    /// <param name="actor">Who is writing through this instance (PLAN 12.4.1): the app, the command line or an agent. Recorded as <c>updatedBy</c>
    /// (and <c>createdBy</c> for a new job). Null records nothing; product code always passes one, a reader may leave it out.</param>
    public ManagedJobs(ITaskSchedulerGateway gateway, Func<DateTimeOffset>? clock = null, Actor? actor = null)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _clock = clock ?? (() => DateTimeOffset.Now);
        Actor = actor;
    }

    public Actor? Actor { get; }

    /// <summary>[CP-R6] What an agent is told when the owner locked the job. The same text everywhere (planner, applier, MCP tools).</summary>
    public const string AgentLockedMessage = "The owner locked this job against changes by AI agents. Tell the user; only they can unlock it in Cadence.";

    bool IsAgent => Actor is { Kind: ActorKind.Agent };

    public ITaskSchedulerGateway Gateway => _gateway;

    public DateTimeOffset Now => _clock();

    /// <summary>The compose context for an existing task (FolderName/RunsRootName from the gateway options, Now = metadata.Updated).</summary>
    public ComposeContext ContextFor(CadenceMetadata metadata, string taskName) =>
        new(_gateway.Options.FolderName, _gateway.Options.RunsRootName, _gateway.ConnectedUserId, taskName, metadata.Updated, metadata.Created, metadata.Prev);

    /// <summary>The compose context for writing now (a new job, or a change to an existing one; the Planner fills in name, Created and PreviousNames).</summary>
    public ComposeContext NewContext(string taskName = "job") =>
        new(_gateway.Options.FolderName, _gateway.Options.RunsRootName, _gateway.ConnectedUserId, taskName, Now, null, ValueList<string>.Empty, Actor);

    public static ObservedTask Observe(RegisteredTaskInfo info) =>
        new(info.Name, info.Path, info.Xml, (int)info.State, info.LastRunTime, info.LastTaskResult, info.NextRunTime);

    public ClassifiedTask Classify(RegisteredTaskInfo info)
    {
        DateTimeOffset now = Now;
        return Classifier.Classify(Observe(info), ContextFor, DateOnly.FromDateTime(now.DateTime), _gateway.IsConnectedUser);
    }

    /// <summary>
    /// Classifies a whole folder listing and then marks duplicate job ids (R21: <see cref="DuplicateJobIds"/>), so a copy of a task made outside
    /// Cadence shows as Drifted ("This is a copy of ...") and can't be saved or re-applied. Order is kept.
    /// </summary>
    public IReadOnlyList<ClassifiedTask> ClassifyFolder(IEnumerable<RegisteredTaskInfo> infos)
    {
        ArgumentNullException.ThrowIfNull(infos);
        return DuplicateJobIds.Resolve(infos.Select(Classify).ToList());
    }

    /// <summary>
    /// Reads the task live from the managed folder and classifies it; null if it does not exist. A managed task is judged against the rest of
    /// the folder, so a copy (duplicate job id) comes back as such.
    /// </summary>
    /// <param name="judgeCopies">False to look at the task on its own (no duplicate-id check): used while a rename briefly has two tasks with one id.</param>
    public ClassifiedTask? Reclassify(string taskName, bool judgeCopies = true)
    {
        RegisteredTaskInfo? info = _gateway.Get(taskName);
        if (info is null) return null;
        ClassifiedTask c = Classify(info);
        if (!judgeCopies || c.Ownership != Ownership.Managed || c.Job is null) return c;
        return ClassifyFolder(_gateway.ListManagedFolder()).FirstOrDefault(x => string.Equals(x.Observed.Path, c.Observed.Path, StringComparison.OrdinalIgnoreCase)) ?? c;
    }

    /// <summary>
    /// The tasks of the managed folder, classified, ordered by name. Foreign tasks (including those inside the managed folder) are only
    /// returned when <paramref name="includeForeign"/> is true; then the read-only tasks of the rest of Task Scheduler follow. A task outside
    /// the managed folder is always reported Foreign, even if it carries Cadence metadata (a copy), so nothing there can be planned or applied.
    /// </summary>
    public IReadOnlyList<ClassifiedTask> Load(bool includeForeign)
    {
        var result = new List<ClassifiedTask>();
        foreach (ClassifiedTask c in ClassifyFolder(_gateway.ListManagedFolder().OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList()))
        {
            if (c.Ownership == Ownership.Foreign && !includeForeign) continue;
            result.Add(c);
        }
        if (includeForeign) result.AddRange(LoadOthers());
        return result;
    }

    /// <summary>
    /// Only the read-only rest of Task Scheduler (everything outside the managed folder), classified as Foreign, ordered by path. A managed-folder
    /// refresh never needs this (it reads 160+ task definitions), so the UI calls it once when the "other tasks" toggle is on and caches the result.
    /// </summary>
    public IReadOnlyList<ClassifiedTask> LoadOthers()
    {
        var result = new List<ClassifiedTask>();
        foreach (RegisteredTaskInfo info in _gateway.ListOtherTasks().OrderBy(t => t.Path, StringComparer.OrdinalIgnoreCase))
        {
            ClassifiedTask c = Classify(info);
            if (c.Ownership != Ownership.Foreign)
                c = c with
                {
                    Ownership = Ownership.Foreign, Sync = SyncStatus.NotApplicable, Metadata = null, Job = null,
                    DriftDiffs = ValueList<FieldDiff>.Empty,
                    Problem = "This task carries Cadence data but is outside the managed folder, so Cadence treats it as read-only.",
                };
            result.Add(c);
        }
        return result;
    }

    // ---- planning from live state (reads only) ----

    /// <summary>Plans saving <paramref name="edited"/>: a new job when <paramref name="existingTaskName"/> is null, else a change to that task.</summary>
    /// <param name="expectedRevision">The <see cref="JobRevision"/> the writer last saw (existing jobs only). A save that would change the job is refused
    /// (<see cref="PlanRefusalKind.Stale"/>) when the live revision differs; a save that changes nothing is not (a retried save reports "No changes").
    /// The Applier checks it again, inside the writer lock, right before writing.</param>
    /// <exception cref="ComposeException">The job can't be composed (cron, validation, size); nothing has been written.</exception>
    /// <exception cref="PlanRefusedException">The existing task is not Managed, no longer exists, or has changed since <paramref name="expectedRevision"/>.</exception>
    public PlannedChange PlanSave(JobDefinition edited, string? existingTaskName, string? expectedRevision = null)
    {
        ArgumentNullException.ThrowIfNull(edited);
        IReadOnlyList<RegisteredTaskInfo> all = _gateway.ListManagedFolder();
        var taken = new HashSet<string>(all.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);

        ClassifiedTask? existing = null;
        if (existingTaskName is not null)
            existing = Reclassify(existingTaskName)
                ?? throw new PlanRefusedException("The task '" + existingTaskName + "' no longer exists in Task Scheduler.", PlanRefusalKind.NotFound);
        if (existing is null)
        {
            // A new job must not reuse the id of a task that is already there (it would look like a rename or a copy).
            foreach (RegisteredTaskInfo info in all)
                if (Classify(info) is { Job: { } job } && string.Equals(job.Id, edited.Id, StringComparison.Ordinal))
                    throw new PlanRefusedException("A job with the id " + edited.Id + " already exists ('" + info.Name + "').");
        }
        if (IsAgent)
        {
            // [CP-R6] An agent can neither set nor clear the lock, and can't touch a locked job: the flag is always the live one (false for a new job).
            if (existing?.Job is { AgentLock: true }) throw new PlanRefusedException(AgentLockedMessage, PlanRefusalKind.Locked);
            edited = edited with { AgentLock = existing?.Job?.AgentLock ?? false };
        }
        PlannedChange plan = Planner.ForSave(edited, existing, taken, NewContext());
        if (existing is null || expectedRevision is null) return plan;
        if (plan.NewXml is not null && !string.Equals(JobRevision.Of(existing), expectedRevision, StringComparison.Ordinal))
            throw new PlanRefusedException(JobRevision.StaleMessage(existing, Now), PlanRefusalKind.Stale);
        return plan with { ExpectedRevision = expectedRevision };
    }

    /// <summary>Plans deleting the task. With <paramref name="expectedRevision"/> the delete is refused (Stale) when the job changed since the writer read it.</summary>
    public PlannedChange PlanDelete(string taskName, string? expectedRevision = null)
    {
        ClassifiedTask live = RequireLive(taskName);
        if (IsAgent && live.Job is { AgentLock: true }) throw new PlanRefusedException(AgentLockedMessage, PlanRefusalKind.Locked);   // [CP-R6]
        PlannedChange plan = Planner.ForDelete(live);
        if (expectedRevision is null) return plan;
        if (!string.Equals(JobRevision.Of(live), expectedRevision, StringComparison.Ordinal))
            throw new PlanRefusedException(JobRevision.StaleMessage(live, Now), PlanRefusalKind.Stale);
        return plan with { ExpectedRevision = expectedRevision };
    }

    public PlannedChange PlanReapply(string taskName) => Planner.ForReapply(RequireLive(taskName), NewContext(taskName));

    public PlannedChange PlanUntrack(string taskName) => Planner.ForUntrack(RequireLive(taskName));

    ClassifiedTask RequireLive(string taskName) =>
        Reclassify(taskName) ?? throw new PlanRefusedException("The task '" + taskName + "' no longer exists in Task Scheduler.", PlanRefusalKind.NotFound);
}
