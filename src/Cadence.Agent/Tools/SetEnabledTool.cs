using System.Text.Json;
using System.Text.Json.Nodes;
using Cadence.Mcp;
using Cadence.Core.Execution;
using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.Scheduler;

namespace Cadence.Agent.Tools;

/// <summary>
/// <c>cadence_set_job_enabled</c> (PLAN 12.3.2): turns one job on or off. It is a save with <c>Enabled</c> flipped, through the <see cref="WritePipeline"/> (the same
/// path as the app's on/off switch), so attribution, the revision check, the lock checks, the second suspension check inside the writer lock and the read-back
/// verification are the save tool's. A job already in the wanted state reports <c>unchanged</c> and nothing is written (checked before the revision, 12.1.3).
/// </summary>
internal sealed class SetEnabledTool
{
    const string Tool = ToolCatalog.SetJobEnabled;
    readonly WritePipeline _w;

    public SetEnabledTool(WritePipeline pipeline) => _w = pipeline;

    public ToolResult Call(JsonElement arguments, CallContext context)
    {
        string jobId;
        string? revision;
        bool enabled;
        try
        {
            ArgReader r = ArgReader.Open(arguments, "jobId", "enabled", "expectedRevision");
            jobId = r.String("jobId", required: true, pattern: ArgReader.JobIdPattern(), help: ArgReader.JobIdHelp)!;
            revision = r.String("expectedRevision", pattern: ArgReader.RevisionPattern(), help: "must be 12 lowercase hex characters, the revision from cadence_get_job");
            enabled = r.Bool("enabled") ?? throw new ArgumentProblem("enabled is required: true to turn the job on, false to turn it off.");
        }
        catch (ArgumentProblem p) { return _w.Invalid(Tool, context, p); }

        WriteGate gate = _w.Begin(Tool, context, jobId);
        if (gate.Refusal is { } refused) return refused;
        try { return Change(gate, enabled, revision); }
        catch (Exception ex) when (ex is SchedulerException or IOException) { return _w.Fault(gate, ex); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _w.AuditInternal(gate, ex);
            throw;
        }
    }

    ToolResult Change(WriteGate gate, bool enabled, string? revision)
    {
        ClassifiedTask live = gate.Live!;
        JobDefinition cur = live.Job!;
        string word = enabled ? "on" : "off";

        if (cur.Enabled == enabled)
        {
            var same = new JsonObject
            {
                ["outcome"] = "unchanged",
                ["message"] = "'" + Clean(cur.Name) + "' is already turned " + word + "; nothing was changed.",
                ["job"] = _w.View(live),
            };
            return _w.Unchanged(gate, same, "Already " + (enabled ? "enabled" : "disabled"));
        }

        // [CP-D D6] Turning a job ON is judged like saving its schedule: an agent must not enable an owner's disabled every-minute job. Turning it off is always allowed.
        if (enabled && _w.CheckSchedule(gate, cur.Schedule, gate.Policy!, setsSchedule: true, out _) is { } tooOften) return tooOften;

        PlannedChange plan;
        // [CP-D] Without a revision from the agent, the revision read for THIS call is used: the plan carries the job as read at gate 4, so a change the owner
        // (or another agent) made in the moment before the writer lock must be refused as stale, never overwritten with the old settings.
        try { plan = gate.Jobs!.PlanSave(cur with { Enabled = enabled }, live.Observed.Name, revision ?? JobRevision.Of(live)); }
        catch (ComposeException ex) { return _w.Refuse(gate, "invalid_argument", ex.Message); }
        catch (PlanRefusedException ex) { return _w.RefusePlan(gate, ex); }

        return _w.Commit(gate, plan, (apply, after) => new JsonObject
        {
            ["outcome"] = "updated",
            ["message"] = apply.Message,
            ["changes"] = plan.Summary,
            ["job"] = after is { Job: not null } ? _w.View(after) : null,
        }, auditCode: enabled ? "enabled" : "disabled");
    }

    static string Clean(string name) => Audit.AuditText.Clean(name, 100);
}
