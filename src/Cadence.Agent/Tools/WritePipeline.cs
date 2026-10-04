using System.Globalization;
using System.Text.Json.Nodes;
using Cadence.Agent.Audit;
using Cadence.Agent.Policy;
using Cadence.Mcp;
using Cadence.Core.Cron;
using Cadence.Core.Metadata;
using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.Core.Scheduling;
using Cadence.Core.History;
using Cadence.History;
using Cadence.Scheduler;
using Cadence.Scheduler.Apply;

namespace Cadence.Agent.Tools;

/// <summary>
/// What a write tool holds between its gates: who is calling, the live managed folder as read for this call, the live job (for a call that names one), the policy
/// as read at the start, and a <see cref="Refusal"/> that is already audited and ready to return when a gate said no.
/// </summary>
internal sealed class WriteGate
{
    public required string Tool { get; init; }
    public required CallContext Context { get; init; }
    public string? JobId { get; set; }
    /// <summary>The job's name for the audit line (set by the pipeline for an existing job, by the tool for a new one).</summary>
    public string? JobName { get; set; }
    public ManagedJobs? Jobs { get; set; }
    public IReadOnlyList<ClassifiedTask> Managed { get; set; } = [];
    public ClassifiedTask? Live { get; set; }
    public AgentPolicy? Policy { get; set; }
    public ToolResult? Refusal { get; set; }
    /// <summary>A refusal by the agent log itself ("The agent log could not be written") travels with the result as a warning.</summary>
    public string? AuditWarning { get; set; }
    public bool Audited { get; set; }
}

/// <summary>
/// The shared write pipeline of PLAN 12.3.2 (Phase 22's tools reuse it): the gates every agent write passes, in order, and the ONE place in the agent project that
/// touches <c>Applier</c>. A write tool opens it with <see cref="Begin"/> (suspension check 1, rate limit, live lookup, state and lock checks), applies its own
/// rules, then either reports <see cref="Unchanged"/> or <see cref="Commit"/>s: the second suspension check runs INSIDE the writer lock, immediately before the
/// write, through the Applier's pre-write guard, so a switch flipped while the call was being planned is obeyed and nothing is written. Writers are built as
/// <c>Actor.Agent(client name)</c> (attribution) and reach Task Scheduler only through <c>ManagedJobs</c> and <c>Applier</c> in the managed folder. Every outcome,
/// refusal or error is audited exactly once (tool, job id, code; never argument text or environment values).
/// </summary>
internal sealed class WritePipeline
{
    /// <summary>Write calls per rolling minute per server process (PLAN 12.3.2 gate 3).</summary>
    public const int WritesPerMinute = 30;

    readonly AgentEnvironment _env;
    readonly AgentPolicyGate _policy;
    readonly RateLimiter _limiter;
    readonly HistoryService _history;

    public WritePipeline(AgentEnvironment env)
    {
        _env = env ?? throw new ArgumentNullException(nameof(env));
        if (env.WriteJobs is null) throw new ArgumentException("The environment has no way to build a writer.", nameof(env));
        _policy = new AgentPolicyGate(env.Policy, env.Zone);
        _limiter = new RateLimiter(env.Clock);
        _history = new HistoryService(env.Runs);
    }

    public AgentEnvironment Env => _env;

    public DateTimeOffset Now => TimeZoneInfo.ConvertTime(_env.Clock(), _env.Zone);

    // ---- gates 2-4 ----

    /// <summary>
    /// Gates 2 to 4: suspension check 1, the rate limit (<paramref name="extraLimit"/> is a tool's own bucket on top of the general one), then the live lookup of the
    /// job in the managed folder (when <paramref name="jobId"/> is given: not_found; Managed, untouched and not a copy; not locked). A refusal is audited and returned
    /// in <see cref="WriteGate.Refusal"/>.
    /// </summary>
    /// <param name="allowMissing">Delete only: a job id that is not there is not a refusal; <see cref="WriteGate.Live"/> stays null and the tool reports "already gone".</param>
    public WriteGate Begin(string tool, CallContext ctx, string? jobId, (string Bucket, int Max, TimeSpan Window, string Rule)? extraLimit = null, bool allowMissing = false)
    {
        var gate = new WriteGate { Tool = tool, Context = ctx, JobId = jobId };
        AgentPolicy policy = _policy.Read();
        gate.Policy = policy;
        if (_policy.CheckWrite(policy) is { } suspended)
        {
            gate.Refusal = Refuse(gate, suspended.Code, suspended.Message);
            return gate;
        }

        TimeSpan? wait = _limiter.TryTake("write", WritesPerMinute, TimeSpan.FromMinutes(1));
        string rule = "agents may make at most " + WritesPerMinute.ToString(CultureInfo.InvariantCulture) + " write calls a minute";
        if (wait is null && extraLimit is { } extra)
        {
            wait = _limiter.TryTake(extra.Bucket, extra.Max, extra.Window);
            rule = extra.Rule;
        }
        if (wait is { } w)
        {
            gate.Refusal = RateRefusal(gate, w, rule);
            return gate;
        }

        try
        {
            gate.Jobs = _env.WriteJobs!(Actor.Agent(ctx.Client.DisplayName));
            gate.Managed = gate.Jobs.Load(includeForeign: false);
        }
        catch (Exception ex) when (ex is SchedulerException or IOException)
        {
            gate.Refusal = Fault(gate, ex);
            return gate;
        }

        if (jobId is null) return gate;

        ClassifiedTask? live = Find(gate.Managed, jobId);
        if (live is null)
        {
            if (allowMissing) return gate;
            gate.Refusal = Refuse(gate, "not_found", "No Cadence job has the id " + jobId + ". List the jobs with cadence_list_jobs.");
            return gate;
        }
        gate.JobName = JobViews.DisplayName(live);
        gate.Live = live;
        if (StateProblem(live) is { } why)
        {
            gate.Refusal = Refuse(gate, "not_allowed_in_this_state", "Job " + jobId + " can't be changed by an agent: " + why + ". Ask the owner to open it in Cadence and resolve it there.");
            return gate;
        }
        if (live.Job!.AgentLock)
            gate.Refusal = Refuse(gate, "locked", ManagedJobs.AgentLockedMessage);
        return gate;
    }

    ToolResult RateRefusal(WriteGate gate, TimeSpan wait, string rule) =>
        Refuse(gate, "rate_limited", "Too many changes in a short time (" + rule + "). Nothing was changed. Wait "
            + ((int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture) + " seconds and try again.");

    /// <summary>
    /// A tool's own rate bucket taken AFTER its rules passed (run now: one start per job per 30 s), so a call that was refused for another reason (the job is off, it is
    /// running, it is locked) does not use up the quota of the call that would have worked. Null when the call may go on, else the audited refusal.
    /// </summary>
    public ToolResult? Throttle(WriteGate gate, string bucket, int max, TimeSpan window, string rule) =>
        _limiter.TryTake(bucket, max, window) is { } wait ? RateRefusal(gate, wait, rule) : null;

    /// <summary>The managed job with this id (an original wins over a copy, as the CLI does), or null.</summary>
    public static ClassifiedTask? Find(IReadOnlyList<ClassifiedTask> all, string id) =>
        all.Where(c => c.Job is not null && string.Equals(c.Job.Id, id, StringComparison.Ordinal)).OrderBy(c => c.DuplicateOf is not null).FirstOrDefault();

    /// <summary>Why an agent may not write this job (null when it may): only Managed, untouched, original jobs are writable. An Outdated job is (a save refreshes it).</summary>
    static string? StateProblem(ClassifiedTask c)
    {
        if (c.Ownership != Ownership.Managed || c.Job is null)
            return c.Ownership == Ownership.NewerVersion ? "it was written by a newer version of Cadence" : "its stored data is damaged";
        if (c.DuplicateOf is not null) return "it is a copy of another job (the same job id appears twice)";
        if (c.Sync == SyncStatus.Drifted) return "it was changed in Task Scheduler outside Cadence";
        if (c.Sync is not (SyncStatus.InSync or SyncStatus.Outdated)) return "its stored data is damaged";
        return null;
    }

    /// <summary>The cap on agent-created jobs against the live count (PLAN 12.4.5): null when a new job may be created.</summary>
    public PolicyRefusal? CreateRefusal(WriteGate gate) =>
        AgentPolicyGate.CheckCreate(AgentPolicyGate.CountAgentJobs(gate.Managed), gate.Policy!);

    // ---- gates 6-8 ----

    /// <summary>
    /// Gates 6 to 8: writes <paramref name="plan"/> through <c>Applier</c> as the calling agent with the plan's expected revision. The second suspension check is the
    /// Applier's pre-write guard: it re-reads the policy INSIDE the writer lock, right before the write (and runs <paramref name="extraGuard"/>, for example the name of
    /// a new job). A failed write is mapped to its code and audited; a successful one is read back and handed to <paramref name="shape"/> for the result.
    /// </summary>
    /// <param name="auditCode">The code of the audit line of a success (default: the node's <c>outcome</c>, else "ok"), for example "deleted".</param>
    public ToolResult Commit(WriteGate gate, PlannedChange plan, Func<ApplyResult, ClassifiedTask?, JsonObject> shape, Func<PolicyRefusal?>? extraGuard = null, string? auditCode = null)
    {
        PolicyRefusal? guardRefusal = null;
        var applier = new Applier(gate.Jobs!, _env.WriterLockTimeout, () =>
        {
            guardRefusal = _policy.CheckWrite() ?? extraGuard?.Invoke();
            return guardRefusal?.Message;
        });

        ApplyResult result;
        try { result = applier.Apply(plan); }
        catch (Exception ex) when (ex is SchedulerException or IOException) { return Fault(gate, ex); }

        if (!result.Success) return Failed(gate, result, guardRefusal);

        string taskName = plan.NewTaskName ?? plan.OldTaskName ?? "";
        ClassifiedTask? after = null;
        try { after = gate.Jobs!.Reclassify(taskName); }
        catch (Exception ex) when (ex is SchedulerException or IOException) { /* the write itself succeeded: report it without the view */ }
        if (after?.Job is { } j) gate.JobName = j.Name;

        JsonObject node = shape(result, after);
        string outcome = auditCode ?? node["outcome"]?.GetValue<string>() ?? "ok";
        return Success(gate, node, outcome, result.Message);
    }

    /// <summary>Nothing needed writing (an identical retry): audits it as ok/unchanged and returns <paramref name="node"/>.</summary>
    public ToolResult Unchanged(WriteGate gate, JsonObject node, string detail) => Success(gate, node, "unchanged", detail);

    /// <summary>A success that wrote nothing through the Applier (run now, "already gone"): audited as ok with <paramref name="code"/>.</summary>
    public ToolResult Done(WriteGate gate, JsonObject node, string code, string detail) => Success(gate, node, code, detail);

    /// <summary>Maps a refusal of the planner (stale, gone, locked, not allowed) to the tool error code and wording.</summary>
    public ToolResult RefusePlan(WriteGate gate, PlanRefusedException ex) => ex.Kind switch
    {
        PlanRefusalKind.Stale => Refuse(gate, "stale_revision", AgentStale(ex.Message)),
        PlanRefusalKind.NotFound => Refuse(gate, "not_found", ex.Message + " List the jobs with cadence_list_jobs."),
        PlanRefusalKind.Locked => Refuse(gate, "locked", ManagedJobs.AgentLockedMessage),
        _ => Refuse(gate, "not_allowed_in_this_state", ex.Message, "Ask the owner to open the job in Cadence if it needs fixing."),
    };

    /// <summary>
    /// Gate 6 for a tool that does not write through the Applier (run now): immediately before the action, the suspension switch is read again and the job is
    /// re-read live (still there, still a plain Managed job, not locked against agents). Returns the refusal (audited) or null with <see cref="WriteGate.Live"/> refreshed.
    /// </summary>
    public ToolResult? Recheck(WriteGate gate)
    {
        if (_policy.CheckWrite() is { } suspended) return Refuse(gate, suspended.Code, suspended.Message);
        ClassifiedTask? live;
        try
        {
            live = Find(gate.Jobs!.Load(includeForeign: false), gate.JobId!);
        }
        catch (Exception ex) when (ex is SchedulerException or IOException) { return Fault(gate, ex); }
        if (live is null) return Refuse(gate, "not_found", "No Cadence job has the id " + gate.JobId + " any more. List the jobs with cadence_list_jobs.");
        if (StateProblem(live) is { } why)
            return Refuse(gate, "not_allowed_in_this_state", "Job " + gate.JobId + " can't be changed by an agent: " + why + ". Ask the owner to open it in Cadence and resolve it there.");
        if (live.Job!.AgentLock) return Refuse(gate, "locked", ManagedJobs.AgentLockedMessage);
        gate.Live = live;
        return null;
    }

    ToolResult Failed(WriteGate gate, ApplyResult r, PolicyRefusal? guardRefusal)
    {
        if (guardRefusal is not null) return Refuse(gate, guardRefusal.Code, guardRefusal.Message);
        switch (r.Kind)
        {
            case ApplyFailure.Stale:
                return Refuse(gate, "stale_revision", AgentStale(r.Message));
            case ApplyFailure.Locked:
                return Refuse(gate, "locked", ManagedJobs.AgentLockedMessage);
            case ApplyFailure.Busy:
                return Refuse(gate, "busy", r.Message, "Nothing was changed. Try the same call again in a few seconds.");
            case ApplyFailure.NotFound:
                return Refuse(gate, "not_found", r.Message + " List the jobs with cadence_list_jobs.");
            case ApplyFailure.Verify:
                return Error(gate, "verify_failed", r.Message, "Call cadence_get_job to see what Task Scheduler holds now, and tell the user if it is not what they asked for.");
            case ApplyFailure.TaskScheduler:
                return Error(gate, "task_scheduler_error", r.Message, "Nothing was changed unless cadence_get_job shows a new revision. Try again in a moment.");
            default:
                return Refuse(gate, "not_allowed_in_this_state", r.Message, "Ask the owner to open the job in Cadence if it needs fixing.");
        }
    }

    /// <summary>The stale message tells an agent exactly what to call.</summary>
    static string AgentStale(string message) =>
        message.Replace("Read it again and re-apply your change.", "Read it again with cadence_get_job and re-apply your change.", StringComparison.Ordinal);

    /// <summary>Compiles the schedule with the app's compiler and, when the call set the schedule, applies the owner's minimum interval to its shortest gap.</summary>
    public ToolResult? CheckSchedule(WriteGate gate, ScheduleSpec spec, AgentPolicy policy, bool setsSchedule, out CompiledSchedule? compiled)
    {
        compiled = CronCompiler.Compile(spec, new MapContext("YOU"));
        if (!compiled.Success)
        {
            CompiledSchedule failed = compiled;
            string errors = string.Join(" ", failed.Diagnostics.Where(d => d.Severity == CronSeverity.Error).Select(d => d.Message.TrimEnd('.') + "."));
            return Refuse(gate, "schedule_invalid", "The schedule can't be used. " + errors + " Check it with cadence_preview_schedule.", null, err =>
            {
                var diags = new JsonArray();
                foreach (CronDiagnostic d in failed.Diagnostics)
                    diags.Add(new JsonObject { ["severity"] = d.Severity.ToString().ToLowerInvariant(), ["line"] = d.Line, ["field"] = d.Field, ["message"] = d.Message });
                err["diagnostics"] = diags;
                if (failed.ClosestAlternative is { } alt)
                {
                    var a = new JsonArray();
                    foreach (string line in alt) a.Add(line);
                    err["closestAlternative"] = a;
                    err["closestAlternativeExplanation"] = failed.AlternativeExplanation;
                }
            });
        }
        if (setsSchedule)
        {
            // [CP-D] Judged over four years, not only the next 14 days: "every minute on 20 December" must not pass because today is 1 October.
            TimeSpan? gap = ScheduleDensity.GapForLimit(compiled.Triggers, Now, _env.Zone, TimeSpan.FromMinutes(policy.MinIntervalMinutes));
            if (AgentPolicyGate.CheckSchedule(gap, policy) is { } tooOften) return Refuse(gate, tooOften.Code, tooOften.Message);
        }
        return null;
    }

    // ---- results and audit ----

    /// <summary>The JobView (12.3.3) of a job as it is now; environment values are never in it.</summary>
    public JsonObject View(ClassifiedTask c) =>
        JobViews.Full(c, _history.Load(c, RunHistory.DefaultWindow), Now, _env.Zone);

    ToolResult Success(WriteGate gate, JsonObject node, string code, string detail)
    {
        string? warning = AuditOnce(gate, "ok", code, detail);
        if (warning is not null)
        {
            if (node["warnings"] is not JsonArray warnings) node["warnings"] = warnings = [];
            warnings.Add(warning);
        }
        return new ToolResult(false, node.ToJsonString(), node);
    }

    /// <summary>A refusal the agent can fix or must report: audited as refused with its code.</summary>
    public ToolResult Refuse(WriteGate gate, string code, string message, string? hint = null, Action<JsonObject>? extra = null) =>
        Fail(gate, "refused", code, message, hint, extra);

    /// <summary>A failure of the machine (Task Scheduler, the disk): audited as error.</summary>
    public ToolResult Error(WriteGate gate, string code, string message, string? hint = null) =>
        Fail(gate, "error", code, message, hint, null);

    ToolResult Fail(WriteGate gate, string outcome, string code, string message, string? hint, Action<JsonObject>? extra)
    {
        string? warning = AuditOnce(gate, outcome, code, message);
        ToolResult r = ReadTools.Error(code, message, hint);
        if (extra is not null) extra((JsonObject)r.Structured!["error"]!);
        if (warning is not null) r.Structured!["warnings"] = new JsonArray(warning);
        return r;
    }

    /// <summary>A malformed call (gate 1): audited as refused/invalid_argument, the message names the field.</summary>
    public ToolResult Invalid(string tool, CallContext ctx, ArgumentProblem problem)
    {
        var gate = new WriteGate { Tool = tool, Context = ctx };
        return Refuse(gate, "invalid_argument", problem.Message, problem.Hint);
    }

    /// <summary>Task Scheduler or the disk failed while reading or writing: nothing is claimed about the state beyond "nothing was changed unless the revision moved".</summary>
    public ToolResult Fault(WriteGate gate, Exception ex) =>
        Error(gate, "task_scheduler_error", "Cadence could not reach Task Scheduler (" + ex.GetType().Name + "). Nothing was changed unless cadence_get_job shows a new revision.", "Try again in a moment.");

    /// <summary>An unexpected exception: one audit line, then the caller rethrows (the server logs it and answers "internal").</summary>
    public void AuditInternal(WriteGate gate, Exception ex) => AuditOnce(gate, "error", "internal", "Unexpected error (" + ex.GetType().Name + ")");

    string? AuditOnce(WriteGate gate, string outcome, string? code, string detail)
    {
        if (gate.Audited) return gate.AuditWarning;
        gate.Audited = true;
        CallContext c = gate.Context;
        gate.AuditWarning = _env.Audit.TryAppend(new AuditEntry(_env.Clock(), c.SessionId, c.Client.DisplayName, c.Client.Version, gate.Tool, gate.JobId, gate.JobName, outcome, code, detail));
        return gate.AuditWarning;
    }
}
