using System.Text.Json;
using System.Text.Json.Nodes;
using Cadence.Agent.Audit;
using Cadence.Agent.Policy;
using Cadence.Mcp;
using Cadence.Core.Metadata;

namespace Cadence.Agent.Tools;

/// <summary>
/// [OWNER-Q14] <c>cadence_suspend_agents</c> and <c>cadence_resume_agents</c>: the two tools that change the owner's agent kill switch. They are the ONLY
/// tools that write anything but jobs, and the only code in the agent server that holds an <see cref="IAgentSwitch"/>. They work while agents are
/// suspended (resume is exactly that path), are idempotent, record who and why (the route is <c>agent:&lt;client name&gt;</c>) in the switch itself and in the
/// audit log, and read the switch back after writing it, so a result never claims a state that is not there. Cadence does not decide whether an agent
/// may call them: a tool-permission layer can allow, ask about or deny them by name.
/// </summary>
internal sealed class ControlTools
{
    /// <summary>[CP-D D7] Switch changes per rolling minute per server process, shared by both tools.</summary>
    public const int ChangesPerMinute = 10;

    const string RateBucket = "switch";

    readonly AgentEnvironment _env;
    readonly IAgentSwitch _switch;
    readonly AgentPolicyGate _gate;
    readonly RateLimiter _limiter;

    public ControlTools(AgentEnvironment env, IAgentSwitch @switch)
    {
        _env = env;
        _switch = @switch;
        _gate = new AgentPolicyGate(@switch, env.Zone);
        _limiter = new RateLimiter(env.Clock);
    }

    public ToolResult Suspend(JsonElement arguments, CallContext context) => Change(suspend: true, ToolCatalog.SuspendAgents, arguments, context);

    public ToolResult Resume(JsonElement arguments, CallContext context) => Change(suspend: false, ToolCatalog.ResumeAgents, arguments, context);

    ToolResult Change(bool suspend, string tool, JsonElement arguments, CallContext context)
    {
        string? reason;
        try
        {
            ArgReader r = ArgReader.Open(arguments, "reason");
            reason = r.String("reason", max: 200);
        }
        catch (ArgumentProblem p)
        {
            // [CP-D D7] A malformed call counts against the limit like any change attempt and is audited once, compactly (identical refusals fold into one counted line).
            if (_limiter.TryTake(RateBucket, ChangesPerMinute, TimeSpan.FromMinutes(1)) is { } waitInvalid) return RateRefusal(context, tool, waitInvalid);
            Audit(context, tool, "refused", "invalid_argument", p.Message, null);
            return ReadTools.Error("invalid_argument", p.Message, p.Hint);
        }
        string route = Actor.Agent(context.Client.DisplayName).Token!;
        DateTimeOffset now = _env.Clock();

        AgentPolicy before = _gate.Read();
        // Already in the wanted state and the setting is readable: nothing to write (idempotent). An unreadable setting counts as "suspended" for the gate,
        // but resuming still writes a valid value, which also repairs it.
        if (before.Suspended == suspend && !before.FailedClosed)
        {
            // Not counted against the limit, and logged without the reason so that repeated calls fold into one counted line.
            Audit(context, tool, "ok", "unchanged", suspend ? "Agents were already suspended" : "Agents were not suspended", null);
            return Ok(Describe(before, changed: false, reason: null));
        }

        // [CP-D D7] At most ChangesPerMinute real changes in any rolling minute per server process; the refusal says how long to wait.
        if (_limiter.TryTake(RateBucket, ChangesPerMinute, TimeSpan.FromMinutes(1)) is { } wait) return RateRefusal(context, tool, wait);

        try
        {
            _switch.SetSuspended(suspend, route, now, null, reason);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            Audit(context, tool, "error", "not_changed", "The switch could not be written (" + ex.GetType().Name + "); the state is unchanged", reason);
            return ReadTools.Error("switch_not_changed",
                "Cadence could not change the agent switch (" + ex.GetType().Name + "). The state is unchanged: agents are still " + (before.Suspended ? "suspended" : "allowed") + ".",
                "Tell the owner; they can change it in the Cadence app or with Cadence.exe --agents.");
        }

        AgentPolicy after = _gate.Read();
        if (after.Suspended != suspend)
        {
            Audit(context, tool, "error", "not_verified", "The switch was written but reads back as " + (after.Suspended ? "suspended" : "allowed"), reason);
            return ReadTools.Error("switch_not_verified",
                "Cadence wrote the agent switch but it reads back as " + (after.Suspended ? "suspended" : "allowed") + ", so the change did not take effect.",
                "Call cadence_get_status to see the current state and tell the owner.");
        }

        Audit(context, tool, suspend ? "suspended" : "resumed", null, suspend ? "Agents suspended by an agent" : "Agents resumed by an agent", reason);
        return Ok(Describe(after, changed: true, reason));
    }

    ToolResult RateRefusal(CallContext context, string tool, TimeSpan wait)
    {
        int seconds = (int)Math.Ceiling(wait.TotalSeconds);
        string message = "Too many changes to the agent switch in a short time (at most " + ChangesPerMinute.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " a minute). Nothing was changed. Wait " + seconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + " seconds and try again.";
        Audit(context, tool, "refused", "rate_limited", "Too many switch changes in a minute", null);
        return ReadTools.Error("rate_limited", message);
    }


    JsonObject Describe(AgentPolicy p, bool changed, string? reason)
    {
        var o = new JsonObject
        {
            ["suspended"] = p.Suspended,
            ["changed"] = changed,
            ["suspendedAt"] = p.Suspended && p.SuspendedAt is { } sa ? JobViews.Iso(sa, _env.Zone) : null,
            ["changedBy"] = p.ChangedBy,
            ["changedAt"] = p.ChangedAt is { } ca ? JobViews.Iso(ca, _env.Zone) : null,
            ["message"] = p.Suspended
                ? "AI agents are suspended: reading works, changing, running and deleting schedules is refused until agents are resumed."
                : "AI agents may change schedules again, within the owner's limits.",
        };
        if (reason is not null) o["reason"] = AgentPolicyParser.CleanFrom(reason);
        return o;
    }

    void Audit(CallContext c, string tool, string outcome, string? code, string detail, string? reason)
    {
        string text = reason is null ? detail : detail + " - reason: " + AgentPolicyParser.CleanFrom(reason);
        // A log that cannot be written must not undo or block the switch change itself; the audit trail is best effort and says so on stderr elsewhere.
        _ = _env.Audit.TryAppend(new AuditEntry(_env.Clock(), c.SessionId, c.Client.DisplayName, c.Client.Version, tool, null, null, outcome, code, text));
    }

    static ToolResult Ok(JsonObject node) => new(false, node.ToJsonString(), node);
}
