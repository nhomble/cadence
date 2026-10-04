using System.Text.Json;
using System.Text.Json.Nodes;
using Cadence.Agent.Audit;
using Cadence.Agent.Policy;
using Cadence.Mcp;
using Cadence.Agent.Tools;
using Cadence.Tests.Mcp;
using Cadence.TestHarness;

namespace Cadence.Tests;

/// <summary>
/// [OWNER-Q14] <c>cadence_suspend_agents</c> and <c>cadence_resume_agents</c>: agents may suspend and resume themselves, as two distinct, honestly described,
/// attributed and audited actions, so a tool-permission layer can allow, ask about or deny exactly that action. Cadence does not decide who may call them.
/// </summary>
public static class ControlToolsTests
{
    const string Suspend = "cadence_suspend_agents";
    const string Resume = "cadence_resume_agents";

    static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.FromHours(-4));

    static bool Flag(ToolResult r) => r.Structured!["suspended"]!.GetValue<bool>();

    static bool Changed(ToolResult r) => r.Structured!["changed"]!.GetValue<bool>();

    public static void TheTwoToolsAreDistinctAndHonest()
    {
        using var rig = new ReadRig(withSwitch: true);
        ToolDefinition suspend = rig.Host.Tools.Single(t => t.Name == Suspend);
        ToolDefinition resume = rig.Host.Tools.Single(t => t.Name == Resume);
        T.True("neither is read-only (they change the switch)", !suspend.Annotations.ReadOnly && !resume.Annotations.ReadOnly);
        T.True("both are idempotent and closed-world, and delete nothing", suspend.Annotations.Idempotent && resume.Annotations.Idempotent && !suspend.Annotations.OpenWorld && !resume.Annotations.OpenWorld && !suspend.Annotations.Destructive && !resume.Annotations.Destructive);
        T.True("titles differ", suspend.Annotations.Title == "Suspend AI agents" && resume.Annotations.Title == "Resume AI agents");
        T.True("descriptions say what they do in plain words", suspend.Description.Contains("kill switch", StringComparison.Ordinal) && suspend.Description.Contains("cadence_resume_agents", StringComparison.Ordinal)
            && resume.Description.Contains("only when the owner has clearly asked", StringComparison.Ordinal) && resume.Description.Contains("recorded with your name", StringComparison.Ordinal));
        using JsonDocument s = JsonDocument.Parse(suspend.InputSchemaJson);
        T.True("the schema takes only an optional bounded reason", s.RootElement.GetProperty("additionalProperties").GetBoolean() == false && s.RootElement.GetProperty("properties").EnumerateObject().Select(p => p.Name).SequenceEqual(["reason"])
            && s.RootElement.GetProperty("properties").GetProperty("reason").GetProperty("maxLength").GetInt32() == 200 && !s.RootElement.TryGetProperty("required", out _));
        T.Eq("both tools share one schema", suspend.InputSchemaJson, resume.InputSchemaJson);
    }

    public static void SuspendAndResumeChangeTheSwitchAndAreAttributed()
    {
        using var rig = new ReadRig(T0, withSwitch: true);
        ToolResult r = rig.Call(Suspend, "{\"reason\":\"owner asked me to stop\"}");
        T.True("suspend succeeds and says so", !r.IsError && Flag(r) && Changed(r) && r.Structured!["message"]!.GetValue<string>().Contains("suspended", StringComparison.Ordinal));
        AgentPolicy p = rig.Policy.Read();
        T.True("the switch is on, by this agent (the route is agent:<client name>), at the clock, with the reason", p is { Suspended: true, SuspendedBy: "agent:Test Agent", ChangedBy: "agent:Test Agent", ChangedReason: "owner asked me to stop" } && p.SuspendedAt == T0);
        T.Eq("the reason is echoed", "owner asked me to stop", r.Structured!["reason"]!.GetValue<string>());

        // The status tool shows who and why, and the next call sees the switch (nothing is cached).
        JsonObject st = rig.Ok("cadence_get_status");
        T.True("status: suspended, changed by the agent, with the reason", st["agents"]!["suspended"]!.GetValue<bool>() && st["agents"]!["changedBy"]!.GetValue<string>() == "agent:Test Agent" && st["agents"]!["changedReason"]!.GetValue<string>() == "owner asked me to stop");

        rig.Now = T0.AddMinutes(7);
        r = rig.Call(Resume);
        T.True("resume succeeds while agents are suspended (it is exactly that path)", !r.IsError && !Flag(r) && Changed(r));
        p = rig.Policy.Read();
        T.True("the switch is off, changed by this agent, and that is not 'outside Cadence'", p is { Suspended: false, ChangedBy: "agent:Test Agent", ChangedReason: null } && !p.ResumedOutsideCadence);
        st = rig.Ok("cadence_get_status");
        T.True("status: agents allowed again, with the agent as the last to change it", !st["agents"]!["suspended"]!.GetValue<bool>() && st["agents"]!["changedBy"]!.GetValue<string>() == "agent:Test Agent");

        T.Eq("two audit lines", 2, rig.Audit.All.Count);
        AuditEntry a = rig.Audit.All[0], b = rig.Audit.All[1];
        T.True("audited as suspended then resumed, with the tool, the client and the reason", a.Outcome == "suspended" && a.Tool == Suspend && a.Client == "Test Agent" && a.Session == "abcd1234" && a.Detail.Contains("reason: owner asked me to stop", StringComparison.Ordinal)
            && b.Outcome == "resumed" && b.Tool == Resume && b.Client == "Test Agent");
    }

    public static void BothAreIdempotentAndWorkWhileSuspended()
    {
        using var rig = new ReadRig(T0, withSwitch: true);
        ToolResult r = rig.Call(Resume);
        T.True("resume when agents are not suspended changes nothing", !r.IsError && !Flag(r) && !Changed(r));
        T.Eq("no write: the policy has no change record", null, rig.Policy.Read().ChangedBy);
        T.True("an unchanged call is still audited, as ok / unchanged", rig.Audit.All.Single() is { Outcome: "ok", Code: "unchanged" });

        rig.Call(Suspend);
        r = rig.Call(Suspend);
        T.True("suspending again changes nothing and keeps the first time", !r.IsError && Flag(r) && !Changed(r) && rig.Policy.Read().SuspendedAt == T0);
        T.True("while suspended every read tool still works", !rig.Call("cadence_list_jobs").IsError && !rig.Call("cadence_get_status").IsError);
        r = rig.Call(Resume);
        T.True("resume works while suspended", !r.IsError && !Flag(r) && Changed(r));
        T.True("and again does nothing", !Changed(rig.Call(Resume)));
    }

    public static void TheReasonIsCleanedAndBounded()
    {
        using var rig = new ReadRig(T0, withSwitch: true);
        string hostile = "line one\nline two\u0000 " + (char)0x202E + "evil\t" + new string('x', 60);
        ToolResult r = rig.Call(Suspend, "{\"reason\":" + JsonSerializer.Serialize(hostile) + "}");
        T.True("a reason with control and bidi characters is accepted", !r.IsError);
        string? stored = rig.Policy.Read().ChangedReason;
        T.True("and stored on one line with nothing invisible", stored is not null && stored.All(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format) && stored.StartsWith("line one line two", StringComparison.Ordinal));
        T.True("the audit line carries the cleaned text only", rig.Audit.All.Single().Detail.All(c => !char.IsControl(c)));

        r = rig.Call(Suspend, "{\"reason\":" + JsonSerializer.Serialize(new string('y', 201)) + "}");
        T.True("a reason over 200 characters is refused with a clear error", r.IsError && ReadRig.Code(r) == "invalid_argument");
        r = rig.Call(Resume, "{\"reason\":5}");
        T.True("a reason that is not text is refused", r.IsError && ReadRig.Code(r) == "invalid_argument");
        r = rig.Call(Resume, "{\"suspended\":false}");
        T.True("any other argument is refused (the switch cannot be set by value)", r.IsError && ReadRig.Code(r) == "invalid_argument" && rig.Policy.Read().Suspended);
    }

    public static void ASwitchThatCannotBeWrittenLeavesTheStateAndSaysSo()
    {
        using var rig = new ReadRig(T0, withSwitch: true);
        rig.Policy.ThrowOnWrite = new UnauthorizedAccessException("denied");
        ToolResult r = rig.Call(Suspend);
        T.True("an error that names the unchanged state", r.IsError && ReadRig.Code(r) == "switch_not_changed" && r.Text.Contains("The state is unchanged: agents are still allowed", StringComparison.Ordinal));
        T.True("the switch did not change", !rig.Policy.Read().Suspended);
        T.True("the failure is audited", rig.Audit.All.Single() is { Outcome: "error", Code: "not_changed" });

        rig.Policy.ThrowOnWrite = null;
        rig.Call(Suspend);
        rig.Policy.ThrowOnWrite = new System.Security.SecurityException("denied");
        r = rig.Call(Resume);
        T.True("a failed resume leaves agents suspended and says so", r.IsError && r.Text.Contains("agents are still suspended", StringComparison.Ordinal) && rig.Policy.Read().Suspended);
    }

    sealed class StuckSwitch : IAgentSwitch
    {
        public AgentPolicy Read() => AgentPolicy.Defaults;

        public void SetSuspended(bool suspended, string by, DateTimeOffset at, string? changedFrom = null, string? reason = null) { /* accepts and does nothing */ }
    }

    static CadenceToolHost HostWith(ReadRig rig, IAgentPolicyReader policy, IAgentSwitch @switch, IAuditLog audit) =>
        new(new AgentEnvironment(rig.Gw, rig.Runs, () => T0, TimeZoneInfo.Utc, new NoProbe(), "v0.0.0-test", policy, audit, @switch));

    public static void AWriteThatDoesNotReadBackIsNotReportedAsDone()
    {
        using var rig = new ReadRig(T0);
        var stuck = new StuckSwitch();
        var audit = new MemoryAuditLog();
        CadenceToolHost host = HostWith(rig, stuck, stuck, audit);
        using JsonDocument d = JsonDocument.Parse("{}");
        ToolResult r = host.Call(Suspend, d.RootElement, ReadRig.Context);
        T.True("an error, not a success", r.IsError && ReadRig.Code(r) == "switch_not_verified");
        T.True("audited as an error", audit.All.Single() is { Outcome: "error", Code: "not_verified" });
    }

    public static void AnUnreadableSettingIsRepairedByResume()
    {
        using var rig = new ReadRig(T0);
        var store = new MemoryAgentPolicyStore(AgentPolicy.Unreadable("Suspended is 2 (it must be 0 or 1)"));
        var audit = new MemoryAuditLog();
        CadenceToolHost host = HostWith(rig, store, store, audit);
        using JsonDocument d = JsonDocument.Parse("{}");
        ToolResult r = host.Call(Resume, d.RootElement, ReadRig.Context);
        T.True("resume writes a valid value over the unreadable one", !r.IsError && !Flag(r) && Changed(r) && !store.Read().FailedClosed && !store.Read().Suspended);
        T.True("audited as resumed", audit.All.Single().Outcome == "resumed");
    }

    public static void WithoutASwitchTheControlToolsDoNotExist()
    {
        using var rig = new ReadRig(T0);
        T.True("not offered", !rig.Host.Tools.Any(t => t.Name is Suspend or Resume) && !rig.Host.Has(Suspend) && !rig.Host.Has(Resume));
        T.True("and calling them is an unknown tool", rig.Call(Resume).IsError && rig.Call(Resume).Text.StartsWith("Unknown tool", StringComparison.Ordinal));
    }

    public static void TheRouteTokenIsValidatedAndCleaned()
    {
        T.Eq("app", "app", AgentPolicyParser.NormalizeWho("app"));
        T.Eq("cli", "cli", AgentPolicyParser.NormalizeWho("cli"));
        T.Eq("an agent", "agent:Claude Code", AgentPolicyParser.NormalizeWho("agent:Claude Code"));
        T.Eq("blanks around are trimmed", "cli", AgentPolicyParser.NormalizeWho("  cli "));
        T.Eq("bidi characters in an agent name are dropped", "agent:evil", AgentPolicyParser.NormalizeWho("agent:" + (char)0x202E + "evil"));
        T.Eq("a long agent name is cut to 64", 64 + "agent:".Length, AgentPolicyParser.NormalizeWho("agent:" + new string('n', 200))!.Length);
        foreach (string? bad in new string?[] { null, "", " ", "agent", "agent:", "agent:  ", "root", "App2", "app\u0000" })
            T.True("'" + bad + "' is not a route", AgentPolicyParser.NormalizeWho(bad) is null || bad == "app\u0000");
        T.Throws<ArgumentException>("the store refuses a route that is not one", () => new MemoryAgentPolicyStore().SetSuspended(true, "root", T0));
        T.Throws<ArgumentException>("a bare 'agent' is refused", () => new MemoryAgentPolicyStore().SetSuspended(true, "agent", T0));
    }

    public static void AnAgentThatCopiesTheAppsNameStillReadsAsAnAgent()
    {
        var store = new MemoryAgentPolicyStore();
        store.SetSuspended(true, "agent:Cadence (the app)", T0);
        AgentPolicy p = store.Read();
        T.Eq("the route keeps the agent prefix", "agent:Cadence (the app)", p.ChangedBy);
        T.Eq("and a person reads it as an AI agent (Review R)", "Cadence (the app) (AI agent)", Cadence.Core.Metadata.Actor.Parse(p.ChangedBy).Describe());
    }
}
