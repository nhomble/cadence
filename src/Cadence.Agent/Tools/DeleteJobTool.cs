using System.Text.Json;
using System.Text.Json.Nodes;
using Cadence.Agent.Audit;
using Cadence.Mcp;
using Cadence.Core.Planning;
using Cadence.Scheduler;

namespace Cadence.Agent.Tools;

/// <summary>
/// <c>cadence_delete_job</c> (PLAN 12.3.2): deletes ONE Cadence job by id, after the caller proves it means that job by giving its exact current name. There is
/// no bulk form and no wildcard: the id pattern is exactly eight characters and the name must equal the job's. Through the <see cref="WritePipeline"/> (gates,
/// the live guard and revision check inside the writer lock, the second suspension check), then the job's own run-history folder goes through
/// <c>RunRecordStore.DeleteJob</c> (which accepts only a job id and never follows a junction). A job that is not there is "already gone", not an error, and
/// nothing is touched for it. Jobs a human changed in Task Scheduler (drifted, copies, damaged, newer version) and locked jobs are refused.
/// </summary>
internal sealed class DeleteJobTool
{
    const string Tool = ToolCatalog.DeleteJob;
    public const int PerMinute = 10;
    readonly WritePipeline _w;

    public DeleteJobTool(WritePipeline pipeline) => _w = pipeline;

    public ToolResult Call(JsonElement arguments, CallContext context)
    {
        string jobId, confirm;
        try
        {
            ArgReader r = ArgReader.Open(arguments, "jobId", "confirmName");
            jobId = r.String("jobId", required: true, pattern: ArgReader.JobIdPattern(), help: ArgReader.JobIdHelp)!;
            confirm = r.String("confirmName", required: true, min: 1, max: 100)!;
            if (confirm.Trim().Length == 0) throw new ArgumentProblem("confirmName must be the job's name, not blank.", "Take the exact name from cadence_list_jobs.");
        }
        catch (ArgumentProblem p) { return _w.Invalid(Tool, context, p); }

        WriteGate gate = _w.Begin(Tool, context, jobId, allowMissing: true);
        if (gate.Refusal is { } refused) return refused;
        try { return gate.Live is null ? Gone(gate) : Delete(gate, confirm); }
        catch (Exception ex) when (ex is SchedulerException or IOException) { return _w.Fault(gate, ex); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _w.AuditInternal(gate, ex);
            throw;
        }
    }

    ToolResult Gone(WriteGate gate)
    {
        var node = new JsonObject { ["deleted"] = false, ["alreadyGone"] = true, ["jobId"] = gate.JobId };
        return _w.Done(gate, node, "alreadyGone", "No Cadence job has this id; nothing to delete");
    }

    ToolResult Delete(WriteGate gate, string confirm)
    {
        ClassifiedTask live = gate.Live!;
        string name = live.Job!.Name;
        if (!string.Equals(confirm.Trim(), name.Trim(), StringComparison.Ordinal))
            return _w.Refuse(gate, "invalid_argument", "jobId " + gate.JobId + " is the job '" + AuditText.Clean(name, 100) + "', not '" + AuditText.Clean(confirm.Trim(), 100) + "'. Check the id with cadence_list_jobs. Nothing was deleted.");

        // At most 10 deletes a minute, counted for a call that is really going to delete (a wrong name does not use the quota).
        if (_w.Throttle(gate, "delete", PerMinute, TimeSpan.FromMinutes(1), "agents may delete at most " + PerMinute.ToString(System.Globalization.CultureInfo.InvariantCulture) + " jobs a minute") is { } limited) return limited;

        // The revision of the job as read for this call: if the owner (or another agent) changed or renamed it since, the delete is refused as stale, not applied to a job nobody confirmed.
        PlannedChange plan;
        try { plan = gate.Jobs!.PlanDelete(live.Observed.Name, JobRevision.Of(live)); }
        catch (PlanRefusedException ex) { return _w.RefusePlan(gate, ex); }

        return _w.Commit(gate, plan, (apply, _) => new JsonObject
        {
            ["deleted"] = true,
            ["jobId"] = gate.JobId,
            ["name"] = AuditText.Clean(name, 100),
            ["historyRemoved"] = RemoveHistory(gate.JobId!),
        }, auditCode: "deleted");
    }

    /// <summary>The job's run-history folder (never anything else: DeleteJob takes a job id only). False when it could not be removed completely.</summary>
    bool RemoveHistory(string jobId)
    {
        try { return _w.Env.Runs.DeleteJob(jobId); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }
}
