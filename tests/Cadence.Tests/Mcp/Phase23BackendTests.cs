using Cadence.Mcp;
using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.Scheduler;
using Cadence.TestHarness;

namespace Cadence.Tests.Mcp;

/// <summary>Phase 23 [CP-D] D6: cadence_set_job_enabled applies the owner's minimum interval when it turns a job ON (disabling is always allowed).</summary>
public static class Phase23BackendTests
{
    const string SetEnabled = "cadence_set_job_enabled";

    public static void EnablingATooFrequentJobIsRefusedLikeASave()
    {
        using var rig = new WriteRig();
        rig.OwnerSave(WriteRig.Job("Every minute", "own00001", "* * * * *") with { Enabled = false });
        ToolResult r = rig.Call(SetEnabled, "{\"jobId\":\"own00001\",\"enabled\":true}");
        T.True("schedule_too_frequent, the same code as a save", r.IsError && WriteRig.Code(r) == "schedule_too_frequent", r.Text);
        T.True("the job is still disabled and in sync, untouched", !rig.Live("own00001").Job!.Enabled && rig.Live("own00001").Sync == SyncStatus.InSync);
        T.True("audited once as refused/schedule_too_frequent", rig.Audit.All.Where(a => a.Tool == SetEnabled).ToList() is [{ Outcome: "refused", Code: "schedule_too_frequent" }]);

        ToolResult same = rig.Call(SetEnabled, "{\"jobId\":\"own00001\",\"enabled\":false}");
        T.True("asking for the state it has is still unchanged (checked first)", !same.IsError && same.Structured!["outcome"]!.GetValue<string>() == "unchanged", same.Text);

        rig.Policy.SetLimits(1, null);
        ToolResult ok = rig.Call(SetEnabled, "{\"jobId\":\"own00001\",\"enabled\":true}");
        T.True("at a limit of 1 minute the same job may be turned on", !ok.IsError && rig.Live("own00001").Job!.Enabled, ok.Text);
    }

    public static void DisablingIsAlwaysAllowedAndASlowJobCanBeEnabled()
    {
        using var rig = new WriteRig();
        rig.OwnerSave(WriteRig.Job("Dense but on", "own00002", "* * * * *"));
        ToolResult off = rig.Call(SetEnabled, "{\"jobId\":\"own00002\",\"enabled\":false}");
        T.True("turning an every-minute job off works", !off.IsError && !rig.Live("own00002").Job!.Enabled, off.Text);
        rig.OwnerSave(WriteRig.Job("Slow", "own00003", "0 18 * * *") with { Enabled = false });
        ToolResult on = rig.Call(SetEnabled, "{\"jobId\":\"own00003\",\"enabled\":true}");
        T.True("a slow job can be turned on", !on.IsError && rig.Live("own00003").Job!.Enabled, on.Text);
        T.True("the catalogue text names the rule", Cadence.Agent.Tools.ToolCatalog.SetJobEnabledDescription.Contains("schedule_too_frequent", StringComparison.Ordinal)
            && Cadence.Agent.Tools.ToolCatalog.SetJobEnabledDescription.Contains("turning it off is always allowed", StringComparison.Ordinal));
    }
}
