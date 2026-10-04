using Cadence.Agent.Audit;
using Cadence.Mcp;
using Cadence.Agent.Tools;
using Cadence.Tests.Mcp;
using Cadence.TestHarness;

namespace Cadence.Tests;

/// <summary>
/// Phase 23 [CP-D] D7: the two switch tools make at most 10 changes per rolling minute per server process (the clock is injected, nothing sleeps); a call that changes nothing is
/// not counted and is logged compactly; a malformed call is counted and audited once, compactly, as refused/invalid_argument.
/// </summary>
public static class SwitchRateLimitTests
{
    const string Suspend = "cadence_suspend_agents";
    const string Resume = "cadence_resume_agents";
    static readonly DateTimeOffset T0 = new(2026, 10, 1, 9, 0, 0, TimeSpan.FromHours(-4));

    public static void TenChangesAMinuteThenRateLimited()
    {
        using var rig = new ReadRig(T0, withSwitch: true);
        for (int i = 0; i < 10; i++)
        {
            rig.Now = T0.AddSeconds(i);
            ToolResult ok = rig.Call(i % 2 == 0 ? Suspend : Resume);
            T.True("change " + (i + 1) + " goes through", !ok.IsError && ok.Structured!["changed"]!.GetValue<bool>(), ok.Text);
        }
        bool suspendedBefore = rig.Policy.Read().Suspended;
        rig.Now = T0.AddSeconds(10);
        ToolResult r = rig.Call(suspendedBefore ? Resume : Suspend);
        T.True("the 11th is rate_limited", r.IsError && ReadRig.Code(r) == "rate_limited", r.Text);
        T.True("the message says nothing was changed and how long to wait", r.Text.Contains("Nothing was changed", StringComparison.Ordinal) && r.Text.Contains("seconds", StringComparison.Ordinal) && r.Text.Contains("at most 10 a minute", StringComparison.Ordinal));
        T.Eq("the switch did not change", suspendedBefore, rig.Policy.Read().Suspended);
        T.True("audited as refused/rate_limited", rig.Audit.All[^1] is { Outcome: "refused", Code: "rate_limited" });

        rig.Now = T0.AddSeconds(30);
        T.True("still limited half a minute later (a rolling window)", ReadRig.Code(rig.Call(suspendedBefore ? Resume : Suspend)) == "rate_limited");
        rig.Now = T0.AddSeconds(61);
        T.True("after the first change leaves the window it works again", !rig.Call(suspendedBefore ? Resume : Suspend).IsError);
    }

    public static void UnchangedCallsAreNotCountedAndFoldIntoOneLine()
    {
        using var rig = new ReadRig(T0, withSwitch: true);
        using var temp = new TempRoot();
        var file = new FileAuditLog("CadenceTest", rootDirectory: temp.Path, clock: () => rig.Now);
        CadenceToolHost host = new(new AgentEnvironment(rig.Gw, rig.Runs, () => rig.Now, TimeZoneInfo.Utc, new NoProbe(), "v0.0.0-test", rig.Policy, file, rig.Policy));
        using var d = System.Text.Json.JsonDocument.Parse("{\"reason\":\"keep trying\"}");

        for (int i = 0; i < 50; i++)
        {
            ToolResult r = host.Call(Resume, d.RootElement, ReadRig.Context);
            T.True("an unchanged call " + i + " is fine, not a change", !r.IsError && !r.Structured!["changed"]!.GetValue<bool>());
        }
        IReadOnlyList<AuditEntry> log = file.Tail(100);
        T.True("fifty unchanged calls are ONE line counted 50 (" + log.Count + ")", log.Count == 1 && log[0] is { Outcome: "ok", Code: "unchanged", Count: 50 });

        // They used none of the quota: ten real changes still work.
        for (int i = 0; i < 10; i++)
            T.True("real change " + (i + 1) + " works", !host.Call(i % 2 == 0 ? Suspend : Resume, d.RootElement, ReadRig.Context).IsError);
        T.True("the eleventh does not", host.Call(Suspend, d.RootElement, ReadRig.Context) is { IsError: true } e && ReadRig.Code(e) == "rate_limited");
        T.True("the switch changes are in the owner stream", File.ReadAllLines(file.OwnerFilePath).Length == 10);
    }

    public static void MalformedCallsAreCountedAndAuditedOnceCompactly()
    {
        using var rig = new ReadRig(T0, withSwitch: true);
        using var temp = new TempRoot();
        var file = new FileAuditLog("CadenceTest", rootDirectory: temp.Path, clock: () => rig.Now);
        CadenceToolHost host = new(new AgentEnvironment(rig.Gw, rig.Runs, () => rig.Now, TimeZoneInfo.Utc, new NoProbe(), "v0.0.0-test", rig.Policy, file, rig.Policy));
        using var bad = System.Text.Json.JsonDocument.Parse("{\"suspended\":false}");

        for (int i = 0; i < 10; i++)
        {
            ToolResult r = host.Call(Resume, bad.RootElement, ReadRig.Context);
            T.True("malformed call " + i + " is invalid_argument", r.IsError && ReadRig.Code(r) == "invalid_argument");
        }
        IReadOnlyList<AuditEntry> log = file.Tail(100);
        T.True("ten identical malformed calls are one refused/invalid_argument line counted 10 (" + log.Count + ")", log.Count == 1 && log[0] is { Outcome: "refused", Code: "invalid_argument", Count: 10, Tool: Resume });

        ToolResult limited = host.Call(Resume, bad.RootElement, ReadRig.Context);
        T.True("they are counted against the limit: the 11th is rate_limited", limited.IsError && ReadRig.Code(limited) == "rate_limited");
        T.True("and a valid change is refused too until the window moves", ReadRig.Code(host.Call(Suspend, System.Text.Json.JsonDocument.Parse("{}").RootElement, ReadRig.Context)) == "rate_limited");
        IReadOnlyList<AuditEntry> after = file.Tail(100);
        T.Eq("the two rate_limited refusals are one line each (a different tool), not one per call", 3, after.Count);
        T.True("the owner stream stays empty: nothing changed", !File.Exists(file.OwnerFilePath));
        T.True("agents are not suspended", !rig.Policy.Read().Suspended);
    }

    sealed class TempRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cadence-switchrl-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* temp: leave it */ }
        }
    }
}
