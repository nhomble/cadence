using System.Reflection;
using System.Text;
using System.Text.Json;
using Cadence.Agent;
using Cadence.Agent.Audit;
using Cadence.Agent.Policy;
using Cadence.Mcp;
using Cadence.Agent.Tools;
using Cadence.Core.Execution;
using Cadence.Tests.Mcp;
using Cadence.TestHarness;

namespace Cadence.Tests;

/// <summary>
/// Phase 20 (PLAN 12.5, [CP-R8], [CP-R9]): an agent must have NO path to the suspension switch or the limits through MCP (no tool, no type the tool host can
/// reach, no source file outside <c>Policy\</c>), the rescoped source rules over <c>src\Cadence.Agent</c>, and the pure Cadence-reference rules agents must pass.
/// </summary>
public static class AgentSafetyTests
{
    // ---- MCP cannot write the flag ----

    static readonly string[] SixReadTools = ["cadence_get_status", "cadence_list_jobs", "cadence_get_job", "cadence_get_run_output", "cadence_preview_schedule", "cadence_list_templates"];

    public static void NoToolCanChangeThePolicy()
    {
        CadenceToolHost host = ReadRig.Bare();
        T.True("the tool host offers exactly the read tools (no write tool exists before Phase 21)", host.Tools.Select(t => t.Name).SequenceEqual(SixReadTools));
        T.True("without a switch in the environment no tool is about suspending or resuming", host.Tools.All(t => !t.Name.Contains("suspend", StringComparison.OrdinalIgnoreCase) && !t.Name.Contains("resume", StringComparison.OrdinalIgnoreCase)
            && !t.Name.Contains("limit", StringComparison.OrdinalIgnoreCase) && !t.Name.Contains("policy", StringComparison.OrdinalIgnoreCase) && !t.Name.Contains("config", StringComparison.OrdinalIgnoreCase)));

        // [OWNER-Q14] With the switch, exactly the two control tools are added; nothing else about the policy is ever a tool (limits, lock, config, policy).
        using var withSwitch = new ReadRig(withSwitch: true);
        T.True("with a switch the host offers the read tools plus exactly the two control tools", withSwitch.Host.Tools.Select(t => t.Name).SequenceEqual([.. SixReadTools, "cadence_suspend_agents", "cadence_resume_agents"]));
        T.True("no other tool is about the policy", withSwitch.Host.Tools.Where(t => t.Name is not ("cadence_suspend_agents" or "cadence_resume_agents")).All(t => !t.Name.Contains("suspend", StringComparison.OrdinalIgnoreCase) && !t.Name.Contains("resume", StringComparison.OrdinalIgnoreCase)
            && !t.Name.Contains("limit", StringComparison.OrdinalIgnoreCase) && !t.Name.Contains("policy", StringComparison.OrdinalIgnoreCase) && !t.Name.Contains("config", StringComparison.OrdinalIgnoreCase) && !t.Name.Contains("lock", StringComparison.OrdinalIgnoreCase)));

        // Every name an agent might try is "Unknown tool", answered as a JSON-RPC error at once (and never reaches the host).
        string[] attempts = ["cadence_resume", "cadence_suspend", "cadence_set_suspended", "cadence_set_limits", "cadence_set_policy", "cadence_config", "cadence_agents_resume",
            "cadence_unlock_job", "cadence_set_job_lock", "cadence_clear_suspension", "resume", "agents/resume", "cadence_get_status/../resume", "CADENCE_GET_STATUS ", "cadence_get_status\u0000"];
        var lines = new List<string> { Session.Init };
        for (int i = 0; i < attempts.Length; i++)
            lines.Add("{\"jsonrpc\":\"2.0\",\"id\":" + (100 + i).ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"method\":\"tools/call\",\"params\":{\"name\":" + JsonSerializer.Serialize(attempts[i]) + ",\"arguments\":{\"suspended\":false}}}");
        var rig = new ReadRig();
        rig.Policy.SetSuspended(true, "app", new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero));
        var (rc, replies, _) = RunSession(rig.Host, [.. lines]);
        T.Eq("the session ends cleanly", 0, rc);
        for (int i = 0; i < attempts.Length; i++)
        {
            JsonElement r = Session.ById(replies, 100 + i);
            T.True("attempt '" + attempts[i].Replace("\0", "\\0", StringComparison.Ordinal) + "' is a -32602 Unknown tool", r.TryGetProperty("error", out JsonElement e) && e.GetProperty("code").GetInt32() == -32602 && e.GetProperty("message").GetString()!.StartsWith("Unknown tool", StringComparison.Ordinal));
        }
        T.True("and the switch is still on", rig.Policy.Read().Suspended);
        rig.Dispose();
    }

    static (int Rc, List<JsonElement> Replies, byte[] Raw) RunSession(IToolHost host, params string[] lines)
    {
        var s = new Session(host: host);
        foreach (string l in lines) s.Input.Line(l);
        int rc = s.Finish();
        return (rc, s.Replies(), s.Output.Raw());
    }

    public static void EveryReadToolRefusesPolicyArgumentsAndLeavesTheSwitchAlone()
    {
        using var rig = new ReadRig();
        rig.Policy.SetSuspended(true, "cli", new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero));
        int reads = rig.Policy.Reads;
        foreach (string tool in SixReadTools)
        {
            foreach (string args in new[] { "{\"suspended\":false}", "{\"resume\":true}", "{\"minIntervalMinutes\":1}", "{\"maxAgentJobs\":500}", "{\"agentLock\":false}" })
            {
                ToolResult r = rig.Call(tool, args);
                T.True(tool + " " + args + ": refused as an unknown property", r.IsError && ReadRig.Code(r) == "invalid_argument");
            }
        }
        T.True("the switch is still on after every attempt", rig.Policy.Read().Suspended);
        T.True("and the limits are untouched", rig.Policy.Read() is { MinIntervalMinutes: 5, MaxAgentJobs: 20 });
        T.True("the read tools did read the policy where they need it (status, preview) and write nothing", rig.Policy.Reads >= reads);
    }

    public static void TheHostOnlyEverGetsTheReaderAndTheSwitch()
    {
        AgentEnvironment env = McpHost.CreateEnvironment("CadenceTest");
        T.True("McpHost builds the environment with the read-only registry reader", env.Policy is RegistryAgentPolicyReader);
        T.True("which is not a store: a cast cannot reach a setter", env.Policy is not IAgentPolicyStore && env.Policy.GetType().GetInterfaces().All(i => i != typeof(IAgentPolicyStore)));
        // [OWNER-Q14] The only writer the host holds is the switch-only one: it can suspend and resume, and a cast cannot reach SetLimits.
        T.True("the host also holds the switch-only writer", env.Switch is RegistryAgentSwitch);
        T.True("which is not a store and has no way to set the limits", env.Switch is not IAgentPolicyStore && !typeof(RegistryAgentSwitch).GetMethods().Any(m => m.Name == "SetLimits") && !typeof(IAgentSwitch).GetMethods().Any(m => m.Name == "SetLimits"));
        T.True("IAgentSwitch is the reader plus SetSuspended and nothing else", typeof(IAgentSwitch).GetInterfaces().SequenceEqual([typeof(IAgentPolicyReader)]) && typeof(IAgentSwitch).GetMethods().Select(m => m.Name).SequenceEqual(["SetSuspended"]) && typeof(IAgentSwitch).GetProperties().Length == 0);
        T.True("the audit log is the folder's file log", env.Audit is FileAuditLog { FilePath: var path } && path.EndsWith("\\CadenceTest\\agents\\audit.jsonl", StringComparison.OrdinalIgnoreCase));
        T.True("the gateway is the test folder's", env.Gateway.Options.FolderName == "CadenceTest");

        // No type in Cadence.Agent outside Policy\ mentions a policy store anywhere in its shape (fields, properties, methods, constructors).
        Type[] stores = [typeof(IAgentPolicyStore), typeof(RegistryAgentPolicyStore), typeof(MemoryAgentPolicyStore)];
        var offenders = new List<string>();
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (Type t in typeof(McpHost).Assembly.GetTypes())
        {
            if (t.Namespace == "Cadence.Agent.Policy" || t.Name.StartsWith('<')) continue;
            var used = new List<Type>();
            foreach (FieldInfo f in t.GetFields(all)) used.Add(f.FieldType);
            foreach (PropertyInfo p in t.GetProperties(all)) used.Add(p.PropertyType);
            foreach (MethodBase m in t.GetMethods(all).Cast<MethodBase>().Concat(t.GetConstructors(all)))
            {
                used.AddRange(m.GetParameters().Select(x => x.ParameterType));
                if (m is MethodInfo mi) used.Add(mi.ReturnType);
            }
            used.AddRange(t.GetInterfaces());
            if (t.BaseType is not null) used.Add(t.BaseType);
            if (used.Any(u => stores.Contains(u) || (u.IsGenericType && u.GetGenericArguments().Any(stores.Contains)))) offenders.Add(t.FullName!);
        }
        T.True("no type outside Cadence.Agent.Policy reaches a policy store (" + string.Join("; ", offenders) + ")", offenders.Count == 0);
        T.True("the tool host's environment holds the reader type and the switch type only", typeof(AgentEnvironment).GetProperty("Policy")!.PropertyType == typeof(IAgentPolicyReader) && typeof(AgentEnvironment).GetProperty("Switch")!.PropertyType == typeof(IAgentSwitch));
        T.True("IAgentPolicyReader has no setter", typeof(IAgentPolicyReader).GetMethods().Select(m => m.Name).SequenceEqual(["Read"]));
    }

    // ---- source rules (PLAN 12.5.1, [CP-R8]) ----

    static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "Cadence.Agent"))) dir = Path.GetDirectoryName(dir);
        // [CP-R8] A missing repository root is a FAILURE: these rules must never pass silently because the tests ran somewhere else.
        if (dir is null) T.True("repository root found (src\\Cadence.Agent next to the test exe's ancestors)", false, AppContext.BaseDirectory);
        return dir ?? "";
    }

    static IEnumerable<(string Relative, string Code)> AgentSources()
    {
        string dir = RepoRoot();
        if (dir.Length == 0) yield break;
        // The protocol module is scanned with the agent project: the same rules (no stdout writers, no registry) hold for it. Its files are reported as "Cadence.Mcp\..." so the Policy\ rules never mistake them for policy code.
        foreach (string project in new[] { "Cadence.Agent", "Cadence.Mcp" })
        {
            string root = Path.Combine(dir, "src", project);
            foreach (string f in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (f.Contains("\\obj\\", StringComparison.Ordinal) || f.Contains("\\bin\\", StringComparison.Ordinal)) continue;
                string rel = Path.GetRelativePath(root, f);
                yield return (project == "Cadence.Mcp" ? "Cadence.Mcp\\" + rel : rel, string.Join("\n", File.ReadAllLines(f).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal))));
            }
        }
    }

    public static void TheAgentSourcesFollowTheRescopedSafetyRules()
    {
        var files = AgentSources().ToList();
        int mcpFiles = files.Count(f => f.Relative.StartsWith("Cadence.Mcp\\", StringComparison.Ordinal));
        T.True("the agent sources were found (" + (files.Count - mcpFiles) + " files)", files.Count - mcpFiles >= 18);
        T.True("the protocol sources were found (" + mcpFiles + " files)", mcpFiles >= 6);
        var offenders = new List<string>();
        string[] bannedEverywhere =
        [
            ".Register(", ".Delete(", ".Stop(", "PlanUntrack", "PlanReapply", "Untrack(", "Reapply(", "DeleteAllManaged", "DeleteFolderIfEmpty", "ListOtherTasks", "LoadOthers",
            "Load(true)", "includeForeign: true",
        ];
        foreach ((string rel, string code) in files)
        {
            string name = Path.GetFileName(rel);
            bool inPolicy = rel.StartsWith("Policy\\", StringComparison.Ordinal);
            foreach (string bad in bannedEverywhere)
                if (code.Contains(bad, StringComparison.Ordinal)) offenders.Add(rel + ": " + bad);
            // The Applier belongs to the write pipeline only (Phase 21: WritePipeline.cs); nothing else in the agent project may touch it.
            if (name != "WritePipeline.cs" && code.Contains("Applier", StringComparison.Ordinal)) offenders.Add(rel + ": Applier outside WritePipeline.cs");
            // The one direct gateway call is run-now (Phase 22: RunJobNowTool.cs).
            if (name != "RunJobNowTool.cs" && (code.Contains("Gateway.Run(", StringComparison.Ordinal) || code.Contains("gateway.Run(", StringComparison.Ordinal))) offenders.Add(rel + ": gateway Run outside RunJobNowTool.cs");
            // The registry is the policy's business only, and only the READ side of it may be used by the MCP host: no store type anywhere outside Policy\.
            if (!inPolicy)
            {
                foreach (string bad in new[] { "Registry.", "Microsoft.Win32", "IAgentPolicyStore", "RegistryAgentPolicyStore", "MemoryAgentPolicyStore", "SetLimits" })
                    if (code.Contains(bad, StringComparison.Ordinal)) offenders.Add(rel + ": " + bad + " outside Policy\\");
                // [OWNER-Q14] The switch-only writer: SetSuspended is called by ControlTools.cs alone; the type is named only where it is wired (the environment record, the host).
                if (name != "ControlTools.cs" && code.Contains("SetSuspended", StringComparison.Ordinal)) offenders.Add(rel + ": SetSuspended outside ControlTools.cs");
                if (name is not ("ControlTools.cs" or "CadenceToolHost.cs" or "McpHost.cs") && (code.Contains("IAgentSwitch", StringComparison.Ordinal) || code.Contains("RegistryAgentSwitch", StringComparison.Ordinal))) offenders.Add(rel + ": the switch type outside ControlTools.cs, CadenceToolHost.cs and McpHost.cs");
            }
            // The audit directory is created by FileAuditLog only.
            if (name != "FileAuditLog.cs" && code.Contains("CreateDirectory", StringComparison.Ordinal)) offenders.Add(rel + ": CreateDirectory outside FileAuditLog.cs");
        }
        T.True("no offending reference in Cadence.Agent (" + string.Join("; ", offenders) + ")", offenders.Count == 0);

        // The switch-writing method is DEFINED only in Policy\ (and called by ControlTools.cs); the limits are written by the CLI and the app only.
        var definers = files.Where(f => f.Code.Contains("void SetSuspended(", StringComparison.Ordinal)).Select(f => f.Relative).ToList();
        T.True("SetSuspended exists only in Policy\\ (" + string.Join(", ", definers) + ")", definers.All(d => d.StartsWith("Policy\\", StringComparison.Ordinal)));
    }

    // ---- Cadence-related commands in a job (CP-R1 / CP-R2 / CP-R9: the shared validator Phase 21 calls) ----

    public static void AgentsCannotScheduleCadenceItself()
    {
        string running = @"C:\Users\tester\bin\Cadence.exe";
        T.True("the running exe", AgentInputRules.IsCadenceProgram(running, running));
        T.True("the running exe in another case and with slashes", AgentInputRules.IsCadenceProgram("c:/users/tester/BIN/cadence.EXE", running));
        T.True("any Cadence*.exe", AgentInputRules.IsCadenceProgram(@"D:\x\Cadence.exe", null) && AgentInputRules.IsCadenceProgram(@"D:\x\Cadence-standalone.exe", null) && AgentInputRules.IsCadenceProgram(@"D:\x\cadence2.exe", null) && AgentInputRules.IsCadenceProgram(@"D:\x\CadenceTest.exe", null));
        T.True("a .com spelling too", AgentInputRules.IsCadenceProgram(@"D:\x\Cadence.com", null));
        T.True("a trailing dot or blank is how Windows reads it (Cadence.exe. is Cadence.exe)", AgentInputRules.IsCadenceProgram(@"D:\x\Cadence.exe.", null) && AgentInputRules.IsCadenceProgram(@"D:\x\Cadence.exe  ", null));
        T.True("quotes in the path do not hide it", AgentInputRules.IsCadenceProgram("\"C:\\Program Files\\Cad\"ence\\Cadence.exe", null));
        T.True("a bare name", AgentInputRules.IsCadenceProgram("cadence.exe", null));
        T.True("other programs are fine", !AgentInputRules.IsCadenceProgram(@"C:\Windows\System32\cmd.exe", running) && !AgentInputRules.IsCadenceProgram(@"C:\Windows\py.exe", running) && !AgentInputRules.IsCadenceProgram(@"C:\Tools\Cadencer.txt", running) && !AgentInputRules.IsCadenceProgram(@"C:\Cadence\tool.exe", running));
        T.True("null and blank are not Cadence", !AgentInputRules.IsCadenceProgram(null, running) && !AgentInputRules.IsCadenceProgram("  ", running) && !AgentInputRules.IsCadenceProgram(@"C:\x\a.exe", null));
    }

    public static void CadenceRelatedArgumentsAreRefused()
    {
        string[] refused =
        [
            "/c Cadence.exe --agents resume --yes", "/c \"C:\\x\\Cadence.exe\" --agents resume --yes", "--agents resume", "--AGENTS status", "-mcp --mcp", "/c start Cadence-standalone.exe",
            "reg add HKCU\\Software\\Cadence\\Agents /v Suspended /d 0", "reg add HKCU/Software/Cadence/Agents /v Suspended /d 0", "reg delete \"HKCU\\Software\\CadenceTest\\Agents\" /f",
            "/c Cad^ence.exe --ag^ents resume", "/c C\"ad\"ence.exe", "/c `Cadence.exe`", "/c CADENCE.EXE", "/c x & cadence.exe --agents resume --yes", "-Command \"& 'C:\\x\\Cadence.exe' --agents resume\"",
            "Set-ItemProperty -Path HKCU:\\Software\\Cadence\\Agents -Name Suspended -Value 0",
        ];
        foreach (string a in refused)
            T.True("refused: " + a, AgentInputRules.MentionsCadence(a) && AgentInputRules.CadenceReference(@"C:\Windows\System32\cmd.exe", a, null) == AgentInputRules.CadenceReferenceMessage);
        string[] fine =
        [
            "", "/d /c echo hello", "-u \"C:\\Users\\Dad\\stocks\\ingest.py\"", "--agent", "-NoProfile -File C:\\x\\cadencer.ps1", "/c backup.bat --go",
            "/c echo Cadence is great", "--max 5 --json",
        ];
        foreach (string a in fine)
        {
            string? r = AgentInputRules.CadenceReference(@"C:\Windows\System32\cmd.exe", a, null);
            T.True("fine: '" + a + "'" + (r is null ? "" : " refused: " + r), r is null);
        }
        T.True("--agentsmith contains --agents and is refused (a speed bump errs on the safe side)", AgentInputRules.MentionsCadence("--agentsmith"));
        T.True("the message is the plan's sentence", AgentInputRules.CadenceReferenceMessage == "Agents can't schedule Cadence itself or change its settings.");
        T.True("null and empty mention nothing", !AgentInputRules.MentionsCadence(null) && !AgentInputRules.MentionsCadence(""));

        T.True("the program path itself can name Cadence in a folder (a path under software\\cadence is not an exe)", AgentInputRules.CadenceReference(@"C:\Windows\reg.exe", "query HKCU\\Software\\Cadence", null) is not null);
        T.True("the program path is scanned for markers too", AgentInputRules.CadenceReference(@"C:\x\--agents.exe", null, null) is not null);
        T.True("an environment value that holds a command line is refused", AgentInputRules.CadenceReference(@"C:\Windows\System32\cmd.exe", "/c %CMD%", null, ["Cadence.exe --agents resume --yes"]) is not null);
        T.True("harmless environment values are fine", AgentInputRules.CadenceReference(@"C:\Windows\System32\cmd.exe", "/c %CMD%", null, ["utf-8", "C:\\data", ""]) is null);
        T.True("Cadence itself as the program is refused even with clean arguments", AgentInputRules.CadenceReference(@"C:\x\Cadence.exe", "", null) is not null);
    }
}
