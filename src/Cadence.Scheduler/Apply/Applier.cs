using Cadence.Core.Execution;
using Cadence.Core.Metadata;
using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.Core.TaskXml;

namespace Cadence.Scheduler.Apply;

/// <summary>Why an apply failed (PLAN 12.1.3), so a caller (the MCP tools) can pick its wording and its error code.</summary>
public enum ApplyFailure
{
    None,
    /// <summary>Not allowed: not a managed job, wrong job id, invalid or incomplete plan, a name that is taken, a job that can't be composed.</summary>
    Refused,
    /// <summary>The job changed since the writer read it (its revision no longer matches).</summary>
    Stale,
    /// <summary>The task is not there (any more).</summary>
    NotFound,
    /// <summary>Another Cadence process holds the writer lock.</summary>
    Busy,
    /// <summary>Task Scheduler itself reported an error.</summary>
    TaskScheduler,
    /// <summary>Written, but it did not read back as what Cadence wrote.</summary>
    Verify,
    /// <summary>[CP-R6] The owner locked the job against AI agents and the writer is an agent.</summary>
    Locked,
}

/// <summary>The outcome of one <see cref="Applier.Apply"/>. <c>Mismatch</c> lists what differs when Task Scheduler stored something other than what Cadence wrote. <c>Kind</c> says why it failed (None on success).</summary>
public sealed record ApplyResult(bool Success, string Message, ValueList<FieldDiff> Mismatch, ApplyFailure Kind = ApplyFailure.None);

/// <summary>
/// Executes <see cref="PlannedChange"/>s against Task Scheduler (PLAN 3.12). The rules, in force for every kind:
/// 1. Immediately before every write or delete the task is re-read live and re-classified. Foreign and NewerVersion tasks are refused
///    (CorruptMetadata only for Delete), and the live job id must equal the plan's job id.
/// 2. The XML to register must itself be a Cadence definition for that job and name (checked before anything is written); Create and Rename
///    refuse to overwrite a task that already exists under the new name.
/// 3. After a write the task is re-read and re-classified; it must be Managed and InSync (Foreign after Untrack, gone after Delete).
/// 4. Untrack never uses the planned XML: it is rebuilt from the live definition at apply time, so edits made since planning are kept.
/// 5. [AG] The whole of every write (and the plan of the convenience entry points) runs inside the cross-process <see cref="WriterLock"/>, and a
///    plan carrying an <see cref="PlannedChange.ExpectedRevision"/> is refused (Stale) when the live job's <see cref="JobRevision"/> differs.
/// Nothing here throws for an expected failure: it returns <c>ApplyResult(false, ...)</c> with a message fit for the UI.
/// </summary>
public sealed class Applier
{
    readonly ManagedJobs _jobs;
    readonly ITaskSchedulerGateway _gateway;
    readonly TimeSpan _lockTimeout;
    readonly Func<string?>? _preWriteGuard;

    /// <param name="lockTimeout">How long a write waits for the cross-process <see cref="WriterLock"/> (default 15 s).</param>
    /// <param name="preWriteGuard">[AG-P21] Evaluated INSIDE the writer lock, before anything is read or written by <see cref="Apply"/> (and so by every convenience entry
    /// point): it returns null to go on, or the reason for refusing, which comes back as <see cref="ApplyFailure.Refused"/> with nothing written. The MCP write pipeline
    /// uses it to re-check the owner's suspension switch (and the name of a new job) at the last possible moment; the app and the CLI pass none.</param>
    public Applier(ManagedJobs jobs, TimeSpan? lockTimeout = null, Func<string?>? preWriteGuard = null)
    {
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _gateway = jobs.Gateway;
        _lockTimeout = lockTimeout ?? WriterLock.DefaultTimeout;
        _preWriteGuard = preWriteGuard;
    }

    // ---- conveniences: plan from live state, then apply ----

    /// <summary>Saves a job. A <see cref="ComposeException"/> (cron, validation, size) writes nothing and its message is returned verbatim.</summary>
    /// <param name="expectedRevision">The <see cref="JobRevision"/> the caller last saw (existing jobs only); a change is refused as Stale when the live job moved on. Null: no check.</param>
    public ApplyResult Save(JobDefinition edited, string? existingTaskName, string? expectedRevision = null) =>
        Plan(() => _jobs.PlanSave(edited, existingTaskName, expectedRevision));

    public ApplyResult Delete(string taskName, string? expectedRevision = null) =>
        Locked(() => _gateway.Get(taskName) is null ? Ok("'" + taskName + "' is already gone.") : Plan(() => _jobs.PlanDelete(taskName, expectedRevision)));

    public ApplyResult Reapply(string taskName) => Plan(() => _jobs.PlanReapply(taskName));

    public ApplyResult Untrack(string taskName) => Plan(() => _jobs.PlanUntrack(taskName));

    /// <summary>Plans from the live state and applies, all inside one hold of the writer lock (the plan can't go stale between the two).</summary>
    ApplyResult Plan(Func<PlannedChange> plan) => Locked(() =>
    {
        PlannedChange change;
        try { change = plan(); }
        catch (ComposeException ex) { return Fail(ex.Message); }
        catch (PlanRefusedException ex) { return Fail(ex.Message, kind: ex.Kind switch { PlanRefusalKind.Stale => ApplyFailure.Stale, PlanRefusalKind.NotFound => ApplyFailure.NotFound, PlanRefusalKind.Locked => ApplyFailure.Locked, _ => ApplyFailure.Refused }); }
        catch (SchedulerException ex) { return Fail("Task Scheduler: " + ex.Message, kind: ApplyFailure.TaskScheduler); }
        return Apply(change);
    });

    /// <summary>Runs <paramref name="action"/> while holding the cross-process writer lock (re-entrant on this thread), or reports Busy. Synchronous on purpose: a mutex belongs to one thread.</summary>
    ApplyResult Locked(Func<ApplyResult> action)
    {
        using WriterLock? held = WriterLock.TryAcquire(_gateway.Options.FolderName, _lockTimeout);
        if (held is null) return Fail(WriterLock.BusyMessage, kind: ApplyFailure.Busy);
        return action();
    }

    // ---- apply ----

    public ApplyResult Apply(PlannedChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return Locked(() => ApplyLocked(change));
    }

    ApplyResult ApplyLocked(PlannedChange change)
    {
        try
        {
            if (_preWriteGuard?.Invoke() is { } reason) return Fail(reason);
            return change.Kind switch
            {
                ChangeKind.Create => Create(change),
                ChangeKind.Update or ChangeKind.Upgrade or ChangeKind.Reapply => Rewrite(change),
                ChangeKind.Rename => Rename(change),
                ChangeKind.Delete => Delete(change),
                ChangeKind.Untrack => Untrack(change),
                _ => Fail("Unknown change kind " + change.Kind + "."),
            };
        }
        catch (SchedulerException ex) { return Fail("Task Scheduler: " + ex.Message, kind: ApplyFailure.TaskScheduler); }
        catch (ArgumentException ex) { return Fail("Refused: " + ex.Message); }
    }

    ApplyResult Create(PlannedChange c)
    {
        if (c.NewTaskName is null || c.NewXml is null) return Fail("The planned change is incomplete (no task name or definition).");
        if (PlannedXmlProblem(c.NewXml, c.JobId, c.NewTaskName) is { } bad) return Fail(bad);
        if (_gateway.Get(c.NewTaskName) is not null)
            return Fail("A task named '" + c.NewTaskName + "' already exists in " + Folder + "; Cadence will not overwrite it.");

        // TASK_CREATE: a task that appeared after the live check above is never overwritten.
        try { _gateway.Register(c.NewTaskName, c.NewXml, RegisterMode.Create); }
        catch (SchedulerException ex) when (ex.Kind == SchedulerErrorKind.AlreadyExists)
        {
            return Fail("A task named '" + c.NewTaskName + "' already exists in " + Folder + "; Cadence will not overwrite it.");
        }
        return VerifyManaged(c.NewTaskName, c.JobId, "Created " + PathOf(c.NewTaskName) + ".");
    }

    ApplyResult Rewrite(PlannedChange c)
    {
        if (c.OldTaskName is null) return Fail("The planned change is incomplete (no task name).");
        if (c.NewTaskName is not null && !string.Equals(c.OldTaskName, c.NewTaskName, StringComparison.OrdinalIgnoreCase))
            return Fail("This change would rename the task; plan a rename instead.");
        if (Guard(c.OldTaskName, c.JobId, c.Kind.ToString().ToLowerInvariant(), allowCorrupt: false, out ClassifiedTask? live) is { } refused) return refused;
        // An identical save is "no changes" before the revision is looked at: a retried save that already went through is a success, not a stale error.
        if (c.Kind == ChangeKind.Update && c.NewXml is null) return Ok("No changes to save.");
        if (StaleRefusal(live!, c.ExpectedRevision) is { } stale) return stale;
        if (c.NewXml is null) return Fail("The planned change is incomplete (no definition).");
        string name = c.NewTaskName ?? c.OldTaskName;
        if (PlannedXmlProblem(c.NewXml, c.JobId, name) is { } bad) return Fail(bad);

        _gateway.Register(name, c.NewXml);
        string done = c.Kind switch
        {
            ChangeKind.Reapply => "Re-applied Cadence's version of " + PathOf(name) + ".",
            ChangeKind.Upgrade => "Brought " + PathOf(name) + " up to date.",
            _ => "Saved " + PathOf(name) + ".",
        };
        return VerifyManaged(name, c.JobId, done);
    }

    ApplyResult Rename(PlannedChange c)
    {
        if (c.OldTaskName is null || c.NewTaskName is null || c.NewXml is null) return Fail("The planned change is incomplete (names or definition missing).");
        if (string.Equals(c.OldTaskName, c.NewTaskName, StringComparison.OrdinalIgnoreCase))
            return Rewrite(c with { Kind = ChangeKind.Update });   // a change of letter case only: Task Scheduler names are case-insensitive

        if (Guard(c.OldTaskName, c.JobId, "rename", allowCorrupt: false, out ClassifiedTask? liveOld) is { } refused) return refused;
        if (StaleRefusal(liveOld!, c.ExpectedRevision) is { } stale) return stale;
        if (PlannedXmlProblem(c.NewXml, c.JobId, c.NewTaskName) is { } bad) return Fail(bad);
        if (_gateway.Get(c.NewTaskName) is not null)
            return Fail("A task named '" + c.NewTaskName + "' already exists in " + Folder + "; Cadence will not overwrite it.");

        try { _gateway.Register(c.NewTaskName, c.NewXml, RegisterMode.Create); }
        catch (SchedulerException ex) when (ex.Kind == SchedulerErrorKind.AlreadyExists)
        {
            return Fail("A task named '" + c.NewTaskName + "' already exists in " + Folder + "; Cadence will not overwrite it.");
        }
        // For a moment the old and the new task carry one job id: the new one must not be judged as a copy of the old one.
        ApplyResult created = VerifyManaged(c.NewTaskName, c.JobId, "", judgeCopies: false);
        if (!created.Success)
            return created with { Message = created.Message + " The old task '" + c.OldTaskName + "' was left in place." };

        // Defence in depth: the old task is checked again right before it is deleted.
        if (Guard(c.OldTaskName, c.JobId, "delete", allowCorrupt: false, out _, checkAgentLock: false) is { } refusedDelete)
            return refusedDelete with { Message = "The new task was created, but the old one was not removed: " + refusedDelete.Message };
        _gateway.Delete(c.OldTaskName);
        if (_gateway.Get(c.OldTaskName) is not null)
            return Fail("The new task " + PathOf(c.NewTaskName) + " was created, but Task Scheduler still has the old task '" + c.OldTaskName + "'.", kind: ApplyFailure.Verify);
        return Ok("Renamed '" + c.OldTaskName + "' to '" + c.NewTaskName + "'.");
    }

    ApplyResult Delete(PlannedChange c)
    {
        if (c.OldTaskName is null) return Fail("The planned change is incomplete (no task name).");
        if (_gateway.Get(c.OldTaskName) is null) return Ok("'" + c.OldTaskName + "' is already gone.");
        if (Guard(c.OldTaskName, c.JobId, "delete", allowCorrupt: true, out ClassifiedTask? live) is { } refused) return refused;
        if (StaleRefusal(live!, c.ExpectedRevision) is { } stale) return stale;

        _gateway.Delete(c.OldTaskName);
        return _gateway.Get(c.OldTaskName) is null
            ? Ok("Deleted " + PathOf(c.OldTaskName) + ".")
            : Fail("Task Scheduler still has '" + c.OldTaskName + "' after the delete.", kind: ApplyFailure.Verify);
    }

    ApplyResult Untrack(PlannedChange c)
    {
        if (c.OldTaskName is null) return Fail("The planned change is incomplete (no task name).");
        if (Guard(c.OldTaskName, c.JobId, "stop managing", allowCorrupt: false, out ClassifiedTask? live) is { } refused) return refused;
        if (StaleRefusal(live!, c.ExpectedRevision) is { } stale) return stale;

        // Rebuilt from the live definition (never from the possibly stale planned XML).
        PlannedChange fresh = Planner.ForUntrack(live!);
        _gateway.Register(c.OldTaskName, fresh.NewXml!);

        ClassifiedTask? after = _jobs.Reclassify(c.OldTaskName);
        if (after is null) return Fail("Task Scheduler no longer has '" + c.OldTaskName + "' after the change.", kind: ApplyFailure.Verify);
        if (after.Ownership != Ownership.Foreign)
            return Fail("The task is still managed by Cadence after the change (" + after.Ownership + "). " + (after.Problem ?? ""), after.DriftDiffs, ApplyFailure.Verify);
        return Ok("Cadence no longer manages " + PathOf(c.OldTaskName) + "; the task stays in Task Scheduler and now runs the program directly.");
    }

    // ---- shared checks ----

    /// <summary>
    /// The live ownership check. Returns a failure (and does not touch anything) unless the task exists, is Managed (or Corrupt when allowed),
    /// and, for Managed tasks, carries the plan's job id.
    /// </summary>
    ApplyResult? Guard(string taskName, string jobId, string verb, bool allowCorrupt, out ClassifiedTask? live, bool checkAgentLock = true)
    {
        live = _jobs.Reclassify(taskName);
        if (live is null) return Fail("The task '" + taskName + "' no longer exists in Task Scheduler.", kind: ApplyFailure.NotFound);
        if (live.Ownership == Ownership.CorruptMetadata && allowCorrupt) return null;
        if (live.Ownership != Ownership.Managed || live.Job is null)
            return Fail(Planner.Refusal(live, verb).Message);
        if (!string.Equals(live.Job.Id, jobId, StringComparison.Ordinal))
            return Fail("The task '" + taskName + "' belongs to a different job than the one planned (" + live.Job.Id + " vs " + jobId + "); Cadence will not " + verb + " it.");
        // [CP-R6] The owner may have locked the job since the plan was made: an agent writer re-checks the LIVE flag inside the writer lock (the owner's lock
        // is itself a write, so it can't slip in between this check and the write that follows).
        if (checkAgentLock && _jobs.Actor is { Kind: ActorKind.Agent } && live.Job.AgentLock)
            return Fail(ManagedJobs.AgentLockedMessage, kind: ApplyFailure.Locked);
        return null;
    }

    /// <summary>
    /// The optimistic-concurrency check (PLAN 12.1.3), run inside the writer lock right before the write: null when no revision was given or it still
    /// matches the live job, else a Stale failure whose message names who changed the job and when.
    /// </summary>
    ApplyResult? StaleRefusal(ClassifiedTask live, string? expectedRevision)
    {
        if (expectedRevision is null) return null;
        if (string.Equals(JobRevision.Of(live), expectedRevision, StringComparison.Ordinal)) return null;
        return Fail(JobRevision.StaleMessage(live, _jobs.Now), kind: ApplyFailure.Stale);
    }

    /// <summary>Null when the XML is a Cadence definition for the job whose wrapper names exactly <c>\Folder\taskName</c>.</summary>
    string? PlannedXmlProblem(string xml, string jobId, string taskName)
    {
        TaskDefinitionModel model;
        try { model = TaskXmlReader.Read(xml); }
        catch (TaskXmlException ex) { return "The planned definition is not valid task XML: " + ex.Message; }

        if (MetadataCodec.TryDecode(model.RegistrationInfo.Documentation, out CadenceMetadata? meta) != MetadataProblem.None)
            return "The planned definition carries no Cadence job data, so Cadence will not write it.";
        if (!string.Equals(meta!.Job.Id, jobId, StringComparison.Ordinal))
            return "The planned definition is for job " + meta.Job.Id + ", not " + jobId + "; Cadence will not write it.";
        if (model.ExecActions.Count != 1 || !WrapperScript.TryGetTaskPath(model.ExecActions[0], out string tp)
            || !string.Equals(tp, PathOf(taskName), StringComparison.OrdinalIgnoreCase))
            return "The planned definition does not run Cadence's wrapper for " + PathOf(taskName) + ", so Cadence will not write it.";
        return null;
    }

    ApplyResult VerifyManaged(string taskName, string jobId, string successMessage, bool judgeCopies = true)
    {
        ClassifiedTask? live = _jobs.Reclassify(taskName, judgeCopies);
        if (live is null) return Fail("Task Scheduler no longer has '" + taskName + "' after writing it.", kind: ApplyFailure.Verify);
        if (live.Ownership != Ownership.Managed || live.Job is null || !string.Equals(live.Job.Id, jobId, StringComparison.Ordinal))
            return Fail("After writing, '" + taskName + "' does not read back as this Cadence job (" + live.Ownership + ").", live.DriftDiffs, ApplyFailure.Verify);
        if (live.Sync != SyncStatus.InSync)
            return Fail("Task Scheduler stored '" + taskName + "', but it does not read back as what Cadence wrote (" + live.Sync + "). " + (live.Problem ?? ""), live.DriftDiffs, ApplyFailure.Verify);
        return Ok(successMessage);
    }

    string Folder => "\\" + _gateway.Options.FolderName;

    string PathOf(string taskName) => Folder + "\\" + taskName;

    static ApplyResult Ok(string message) => new(true, message, ValueList<FieldDiff>.Empty);

    static ApplyResult Fail(string message, ValueList<FieldDiff>? mismatch = null, ApplyFailure kind = ApplyFailure.Refused) =>
        new(false, message.Trim(), mismatch ?? ValueList<FieldDiff>.Empty, kind);
}
