using System.Text.Json.Nodes;
using Cadence.Mcp;
using Cadence.Agent.Tools;
using Cadence.TestHarness;

namespace Cadence.Tests.Mcp;

/// <summary>Phase 22: the catalogue entries of cadence_set_job_enabled, cadence_run_job_now and cadence_delete_job equal PLAN 12.3.2, and the host routes and flags them as write tools.</summary>
public static class WriteToolCatalogTests
{
    static string[]? PlanLines()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "PLAN.md"))) dir = Path.GetDirectoryName(dir);
        return dir is null ? null : File.ReadAllLines(Path.Combine(dir, "PLAN.md"));
    }

    static string? FromPlan(string[] lines, string tool, string prefix)
    {
        int start = Array.FindIndex(lines, l => l.Trim() == "**`" + tool + "`**");
        if (start < 0) return null;
        for (int i = start + 1; i < lines.Length && !lines[i].StartsWith("**`", StringComparison.Ordinal) && !lines[i].StartsWith("#", StringComparison.Ordinal); i++)
        {
            if (!lines[i].StartsWith(prefix, StringComparison.Ordinal)) continue;
            int a = lines[i].IndexOf('`', StringComparison.Ordinal);
            int b = lines[i].IndexOf("`", a + 1, StringComparison.Ordinal);
            return a >= 0 && b > a ? lines[i][(a + 1)..b] : null;
        }
        return null;
    }

    public static void TheThreeToolsEqualThePlan()
    {
        string[]? plan = PlanLines();
        if (plan is null) { T.True("PLAN.md is not in this checkout (the design log is kept locally, not in the repo): comparison with the plan skipped", true); return; }
        (string Name, string Description, string Schema)[] tools =
        [
            (ToolCatalog.SetJobEnabled, ToolCatalog.SetJobEnabledDescription, ToolCatalog.SetJobEnabledSchema),
            (ToolCatalog.RunJobNow, ToolCatalog.RunJobNowDescription, ToolCatalog.RunJobNowSchema),
            (ToolCatalog.DeleteJob, ToolCatalog.DeleteJobDescription, ToolCatalog.DeleteJobSchema),
        ];
        foreach ((string name, string description, string schema) in tools)
        {
            string? d = FromPlan(plan, name, "- Description");
            T.True(name + ": description found in PLAN.md", d is not null);
            if (d is not null) T.Eq(name + ": description equals PLAN 12.3.2", d, description);
            string? s = FromPlan(plan, name, "- inputSchema");
            T.True(name + ": inputSchema found in PLAN.md", s is not null);
            if (s is not null) T.True(name + ": inputSchema equals PLAN 12.3.2", JsonNode.DeepEquals(JsonNode.Parse(s), JsonNode.Parse(schema)));
            JsonObject o = JsonNode.Parse(schema)!.AsObject();
            T.True(name + ": refuses unknown properties", o["additionalProperties"]!.GetValue<bool>() == false);
            T.True(name + ": jobId pattern", o["properties"]!["jobId"]!["pattern"]!.GetValue<string>() == "^[a-z0-9]{8}$");
        }
    }

    public static void AnnotationsAreThoseOfThePlanTable()
    {
        using var rig = new WriteRig();
        ToolDefinition en = rig.Host.Tools.Single(t => t.Name == "cadence_set_job_enabled");
        ToolDefinition run = rig.Host.Tools.Single(t => t.Name == "cadence_run_job_now");
        ToolDefinition del = rig.Host.Tools.Single(t => t.Name == "cadence_delete_job");
        T.True("set enabled: not read-only, not destructive, idempotent", en.Annotations is { ReadOnly: false, Destructive: false, Idempotent: true, OpenWorld: false });
        T.True("run now: not read-only, destructive, not idempotent", run.Annotations is { ReadOnly: false, Destructive: true, Idempotent: false, OpenWorld: false });
        T.True("delete: not read-only, destructive, idempotent", del.Annotations is { ReadOnly: false, Destructive: true, Idempotent: true, OpenWorld: false });
        T.Eq("titles", "Turn a schedule on or off|Run a schedule now|Delete a schedule", en.Annotations.Title + "|" + run.Annotations.Title + "|" + del.Annotations.Title);
        T.True("a read-only environment offers none of them", ReadRig.Bare().Tools.All(t => t.Name is not ("cadence_set_job_enabled" or "cadence_run_job_now" or "cadence_delete_job")));
        _ = new McpServer(Cadence.Agent.McpHost.ServerInfo("1"), rig.Host, TextWriter.Null);   // every schema is parsed when the server is built
    }

    public static void AllFourWriteToolsAreFlaggedSoTheServerFinishesAWriteInProgress()
    {
        using var rig = new WriteRig();
        foreach (string n in new[] { "cadence_save_job", "cadence_set_job_enabled", "cadence_run_job_now", "cadence_delete_job" })
            T.True(n + " is a write tool", rig.Host.IsWriteTool(n));
        foreach (string n in new[] { "cadence_get_job", "cadence_list_jobs", "cadence_get_status", "cadence_suspend_agents", "cadence_resume_agents", "cadence_unknown" })
            T.True(n + " is not", !rig.Host.IsWriteTool(n));
    }

    public static void ThereIsNoBulkOrFreeFormTool()
    {
        using var rig = new WriteRig(withSwitch: true);
        string[] names = [.. rig.Host.Tools.Select(t => t.Name)];
        T.Eq("exactly the plan's twelve tools", 12, names.Length);
        T.True("nothing that deletes many, runs a command, re-applies or untracks", !names.Any(n => n.Contains("all", StringComparison.Ordinal) && n != "cadence_list_jobs" || n.Contains("untrack", StringComparison.Ordinal) || n.Contains("reapply", StringComparison.Ordinal) || n.Contains("command", StringComparison.Ordinal) || n.Contains("exec", StringComparison.Ordinal)));
        foreach (string unknown in new[] { "cadence_delete_all", "cadence_delete_jobs", "cadence_run_command", "cadence_untrack_job", "cadence_reapply_job", "cadence_set_agents_suspended", "agents_resume" })
        {
            using var d = System.Text.Json.JsonDocument.Parse("{}");
            ToolResult r = rig.Host.Call(unknown, d.RootElement, WriteRig.Context);
            T.True(unknown + " is unknown", r.IsError && WriteRig.Code(r) == "invalid_argument" && WriteRig.Msg(r).StartsWith("Unknown tool", StringComparison.Ordinal));
        }
    }
}
