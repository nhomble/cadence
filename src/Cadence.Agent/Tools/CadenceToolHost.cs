using System.Text.Json;
using Cadence.Agent.Audit;
using Cadence.Agent.Policy;
using Cadence.Mcp;
using Cadence.Core.Execution;
using Cadence.History;
using Cadence.Scheduler;

namespace Cadence.Agent.Tools;

/// <summary>
/// What the tools need from the machine, injected so unit tests run on a fake gateway and a temp runs root and Core stays pure (PLAN Phase 18).
/// <see cref="Runs"/> is the wrapper's runs root of the gateway's folder (never the real %LOCALAPPDATA%\Cadence in tests).
/// <see cref="Policy"/> is the READ-ONLY view of the owner's suspension switch and limits (<c>IAgentPolicyReader</c> has no setter, and the MCP host never
/// receives the store); <see cref="Audit"/> is the audit log. <see cref="Switch"/> ([OWNER-Q14]) is the narrow interface that can turn the suspension switch
/// on or off and nothing else: only <see cref="ControlTools"/> uses it, and when it is null the two control tools are not offered.
/// [AG-P21] <see cref="WriteJobs"/> builds the writer for one call as the calling agent (<c>new ManagedJobs(gateway, clock, Actor.Agent(client))</c>, attribution
/// <c>agent:&lt;client&gt;</c>); when it is null no write tool is offered (the read-only host of the unit tests and the self-test). <see cref="OwnExePath"/> is the running
/// exe (rule 5: agents can't schedule Cadence itself) and <see cref="WriterLockTimeout"/> how long a write waits for another Cadence process (default 15 s).
/// </summary>
public sealed record AgentEnvironment(ITaskSchedulerGateway Gateway, RunRecordStore Runs, Func<DateTimeOffset> Clock, TimeZoneInfo Zone,
    IFileProbe Files, string AppVersion, IAgentPolicyReader Policy, IAuditLog Audit, IAgentSwitch? Switch = null,
    string? OwnExePath = null, Func<Cadence.Core.Metadata.Actor, ManagedJobs>? WriteJobs = null, TimeSpan? WriterLockTimeout = null);

/// <summary>
/// The tools of the MCP server. Phase 18: the six read-only tools of PLAN 12.3.1 (they write nothing); Phase 20: the two switch tools; Phases 21-22: <c>cadence_save_job</c>, <c>cadence_set_job_enabled</c>,
/// <c>cadence_run_job_now</c> and <c>cadence_delete_job</c>, the job-writing tools: they pass the write pipeline's gates, and all but run-now write only through it. The read tools never call a gateway Register, Delete or
/// Run, and foreign tasks are never listed (the tools read the managed folder through <c>ManagedJobs.Load(includeForeign: false)</c> only).
/// Validation problems and Task Scheduler failures become error results (<c>isError</c>) that say how to fix the call.
/// </summary>
public sealed class CadenceToolHost : IToolHost, IWriteAwareToolHost
{
    public const string StatusToolName = ToolCatalog.GetStatus;

    readonly ReadTools _read;
    readonly ControlTools? _control;
    readonly SaveJobTool? _save;
    readonly SetEnabledTool? _setEnabled;
    readonly RunJobNowTool? _runNow;
    readonly DeleteJobTool? _delete;

    public CadenceToolHost(AgentEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(env);
        _read = new ReadTools(env);
        var tools = new List<ToolDefinition>(ToolCatalog.ReadTools);
        if (env.WriteJobs is not null)
        {
            var pipeline = new WritePipeline(env);
            _save = new SaveJobTool(pipeline);
            _setEnabled = new SetEnabledTool(pipeline);
            _runNow = new RunJobNowTool(pipeline);
            _delete = new DeleteJobTool(pipeline);
            tools.AddRange(ToolCatalog.WriteTools);
        }
        if (env.Switch is { } sw)
        {
            _control = new ControlTools(env, sw);
            tools.AddRange(ToolCatalog.ControlTools);
        }
        Tools = tools.Count == ToolCatalog.ReadTools.Count ? ToolCatalog.ReadTools : (IReadOnlyList<ToolDefinition>)tools;
    }

    public IReadOnlyList<ToolDefinition> Tools { get; }

    public bool Has(string name) => Tools.Any(t => string.Equals(t.Name, name, StringComparison.Ordinal));

    /// <summary>[CP-R7] The tools that can write a job: the server finishes one that has started before it exits on end of input, and drops one that has not.</summary>
    public bool IsWriteTool(string name) =>
        name is ToolCatalog.SaveJob or ToolCatalog.SetJobEnabled or ToolCatalog.RunJobNow or ToolCatalog.DeleteJob;

    public ToolResult Call(string name, JsonElement arguments, CallContext context)
    {
        try
        {
            return name switch
            {
                ToolCatalog.GetStatus => _read.Status(arguments, context),
                ToolCatalog.ListJobs => _read.ListJobs(arguments),
                ToolCatalog.GetJob => _read.GetJob(arguments),
                ToolCatalog.GetRunOutput => _read.GetRunOutput(arguments),
                ToolCatalog.PreviewSchedule => _read.PreviewSchedule(arguments),
                ToolCatalog.ListTemplates => _read.ListTemplates(arguments),
                ToolCatalog.SaveJob when _save is not null => _save.Call(arguments, context),
                ToolCatalog.SetJobEnabled when _setEnabled is not null => _setEnabled.Call(arguments, context),
                ToolCatalog.RunJobNow when _runNow is not null => _runNow.Call(arguments, context),
                ToolCatalog.DeleteJob when _delete is not null => _delete.Call(arguments, context),
                ToolCatalog.SuspendAgents when _control is not null => _control.Suspend(arguments, context),
                ToolCatalog.ResumeAgents when _control is not null => _control.Resume(arguments, context),
                _ => ReadTools.Error("invalid_argument", "Unknown tool '" + name + "'. Call tools/list to see the tools."),
            };
        }
        catch (ArgumentProblem p)
        {
            return ReadTools.Error("invalid_argument", p.Message, p.Hint);
        }
        catch (Exception ex) when (ex is SchedulerException or IOException)
        {
            return ReadTools.Error("task_scheduler_error",
                "Cadence could not read Task Scheduler (" + ex.GetType().Name + "). Nothing was changed.", "Try again in a moment.");
        }
    }
}
