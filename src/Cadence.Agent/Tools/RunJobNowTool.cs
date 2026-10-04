using System.Text.Json;
using System.Text.Json.Nodes;
using Cadence.Mcp;
using Cadence.Core.Planning;
using Cadence.Scheduler;

namespace Cadence.Agent.Tools;

/// <summary>
/// <c>cadence_run_job_now</c> (PLAN 12.3.2): starts one EXISTING Cadence job once, right now. It is the only tool that calls the gateway's <c>Run</c> (and the
/// only file allowed to: the source-grep safety test pins that), and it writes nothing: no plan, no Applier, no command line of its own. The gates are the
/// pipeline's (arguments, suspension 1, rate limit: one start per job per 30 s on top of the general one, live lookup with the state and lock checks), then the
/// tool's rules (job enabled, not already running), then the pipeline's second check (suspension re-read, job re-read live, lock) immediately before
/// <c>Run</c>. The tool never retries and never renames: when Windows refuses the launch later (Defender, PLAN 10.2 Q6) the history row says so.
/// </summary>
internal sealed class RunJobNowTool
{
    const string Tool = ToolCatalog.RunJobNow;
    public const int CheckAfterSeconds = 5;
    public static readonly TimeSpan PerJobWindow = TimeSpan.FromSeconds(30);
    readonly WritePipeline _w;

    public RunJobNowTool(WritePipeline pipeline) => _w = pipeline;

    public ToolResult Call(JsonElement arguments, CallContext context)
    {
        string jobId;
        try
        {
            ArgReader r = ArgReader.Open(arguments, "jobId");
            jobId = r.String("jobId", required: true, pattern: ArgReader.JobIdPattern(), help: ArgReader.JobIdHelp)!;
        }
        catch (ArgumentProblem p) { return _w.Invalid(Tool, context, p); }

        WriteGate gate = _w.Begin(Tool, context, jobId);
        if (gate.Refusal is { } refused) return refused;
        try { return Start(gate); }
        catch (Exception ex) when (ex is SchedulerException or IOException) { return _w.Fault(gate, ex); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _w.AuditInternal(gate, ex);
            throw;
        }
    }

    static ToolResult? Rules(WritePipeline w, WriteGate gate, ClassifiedTask live)
    {
        if (!live.Job!.Enabled)
            return w.Refuse(gate, "not_allowed_in_this_state", "Job " + gate.JobId + " is turned off, so it can't be run. Turn it on first with cadence_set_job_enabled, or ask the user.");
        if (live.Observed.State == (int)TaskState.Running)
            return w.Refuse(gate, "not_allowed_in_this_state", "Job " + gate.JobId + " is already running; Windows would ignore a second start. Call cadence_get_job in a few seconds to see the result.");
        return null;
    }

    ToolResult Start(WriteGate gate)
    {
        if (Rules(_w, gate, gate.Live!) is { } no) return no;

        // One start per job per 30 s, counted only for a call that passed the rules (a refused call must not use up the quota of the one that would have worked).
        if (_w.Throttle(gate, "run:" + gate.JobId, 1, PerJobWindow, "a job can be started at most once every 30 seconds") is { } limited) return limited;

        // Gate 6, right before the action: the switch is read again, and the job is re-read live (state, lock) so the decision is made on what is there now.
        if (_w.Recheck(gate) is { } late) return late;
        if (Rules(_w, gate, gate.Live!) is { } now) return now;

        string taskName = gate.Live!.Observed.Name;
        DateTimeOffset startedAt = _w.Now;
        try { _w.Env.Gateway.Run(taskName); }
        catch (Exception ex) when (ex is SchedulerException or ArgumentException)
        {
            return _w.Error(gate, "task_scheduler_error", "Task Scheduler could not start the job (" + ex.GetType().Name + "). Nothing was changed.", "Call cadence_get_job to see the job's state, and tell the user if it keeps failing.");
        }

        var node = new JsonObject
        {
            ["started"] = true,
            ["jobId"] = gate.JobId,
            ["startedAt"] = JobViews.Iso(startedAt, _w.Env.Zone),
            ["checkAfterSeconds"] = CheckAfterSeconds,
            ["note"] = "Call cadence_get_job to see the result.",
        };
        return _w.Done(gate, node, "started", "Asked Task Scheduler to start the job");
    }
}
