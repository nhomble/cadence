using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Cadence.Agent.Audit;
using Cadence.Agent.Policy;
using Cadence.Core.Execution;
using Cadence.Core.Metadata;
using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.Core.TaskXml;
using Cadence.IntegrationTests.Agents;
using Cadence.IntegrationTests.Mcp;
using Cadence.Scheduler;
using Cadence.TestHarness;
using Microsoft.Win32;

namespace Cadence.IntegrationTests;

/// <summary>
/// Phase 20 against the REAL exe and the REAL registry, only ever <c>HKCU\Software\CadenceTest\Agents</c> and <c>%LOCALAPPDATA%\CadenceTest\agents</c>:
/// the registry store and its hostile values, <c>--agents status|suspend|resume|config|log|lock|unlock</c>, a running MCP session obeying a flag that the
/// command line flips between two calls, the fail-closed reader, and "an agent cannot resume through MCP". Each test cleans the agents state before and
/// after it (the later Mcp* suites assert that the server itself never creates the key). The real confirmation window is checked by hand with UI Automation.
/// </summary>
public static class AgentPolicyIntegrationTests
{
    const string Folder = "CadenceTest";
    const string InitializeParams = "{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{\"name\":\"cadence-tests\",\"title\":\"Cadence Test Agent\",\"version\":\"1\"}}";
    static readonly string[] Test = ["--folder", Folder];

    static Footprint? _before;

    sealed record Footprint(bool ProductKey, bool ProductAgentsKey, bool ProductLocalDir, bool ProductTaskFolder);

    static Footprint Snapshot()
    {
        using RegistryKey? k = Registry.CurrentUser.OpenSubKey(@"Software\Cadence");
        using RegistryKey? a = Registry.CurrentUser.OpenSubKey(@"Software\Cadence\Agents");
        string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cadence");
        return new Footprint(k is not null, a is not null, Directory.Exists(local), false);
    }

    public static void AaStart()
    {
        _before = Snapshot();
        T.True("the server exe exists (" + McpTestClient.ExePath() + ")", File.Exists(McpTestClient.ExePath()));
        TestFolder.CleanAgentState();
    }

    static string[] A(params string[] args) => [.. args, .. Test];

    static RegistryKey? OpenAgents(bool writable = false) => Registry.CurrentUser.OpenSubKey(TestFolder.AgentsKey, writable);

    static bool KeyExists() { using RegistryKey? k = OpenAgents(); return k is not null; }

    static JsonDocument StatusJson() => JsonDocument.Parse(RealCli.Run(A("--agents", "status", "--json")).Stdout);

    static JsonElement ToolCall(McpTestClient c, string name, string args = "{}") =>
        c.Request("tools/call", "{\"name\":" + JsonSerializer.Serialize(name) + ",\"arguments\":" + args + "}") ?? throw new InvalidOperationException(name + ": no answer");

    static JsonElement Structured(JsonElement response) => response.GetProperty("result").GetProperty("structuredContent");

    static JsonElement AgentsOf(McpTestClient c) => Structured(ToolCall(c, "cadence_get_status")).GetProperty("agents");

    static IReadOnlyList<AuditEntry> Audit() => new FileAuditLog(Folder).Tail(500);

    // ---- the registry store and reader ----

    public static void StoreRoundTripAndRegistryTypes()
    {
        TestFolder.CleanAgentState();
        try
        {
            var store = new RegistryAgentPolicyStore(Folder);
            var reader = new RegistryAgentPolicyReader(Folder);
            T.True("no key: defaults, not suspended", store.Read() is { Suspended: false, MinIntervalMinutes: 5, MaxAgentJobs: 20, ReadProblem: null });
            T.True("reading created nothing", !KeyExists());
            T.True("the reader does not create it either", reader.Read().Suspended == false && !KeyExists());

            var at = new DateTimeOffset(2026, 10, 1, 14, 2, 11, 123, TimeSpan.FromHours(-4));
            store.SetSuspended(true, "cli", at);
            using (RegistryKey k = OpenAgents()!)
            {
                T.Eq("Suspended is a DWORD", RegistryValueKind.DWord, k.GetValueKind("Suspended"));
                T.Eq("Suspended is 1", 1, (int)k.GetValue("Suspended")!);
                T.Eq("SuspendedAt is a string", RegistryValueKind.String, k.GetValueKind("SuspendedAt"));
                T.Eq("SuspendedAt is ISO 8601 with milliseconds and offset", "2026-10-01T14:02:11.123-04:00", (string)k.GetValue("SuspendedAt")!);
                T.Eq("SuspendedBy", "cli", (string)k.GetValue("SuspendedBy")!);
                T.Eq("ChangedBy", "cli", (string)k.GetValue("ChangedBy")!);
                T.Eq("ChangedAt equals SuspendedAt", (string)k.GetValue("SuspendedAt")!, (string)k.GetValue("ChangedAt")!);
                T.True("no ChangedFrom after a suspend", k.GetValue("ChangedFrom") is null);
            }
            AgentPolicy p = reader.Read();
            T.True("the reader sees it", p is { Suspended: true, SuspendedBy: "cli", ChangedBy: "cli" } && p.SuspendedAt == at && p.ChangedAt == at);

            store.SetSuspended(false, "app", at.AddMinutes(3), "cmd.exe < svchost.exe");
            p = reader.Read();
            T.True("resumed through the app: flag off, who/when/from recorded", !p.Suspended && p.ChangedBy == "app" && p.ChangedAt == at.AddMinutes(3) && p.ChangedFrom == "cmd.exe < svchost.exe" && p.SuspendedAt == at);
            T.True("and not 'outside Cadence'", !p.ResumedOutsideCadence);
            using (RegistryKey k = OpenAgents()!) T.Eq("Suspended is the DWORD 0", 0, (int)k.GetValue("Suspended")!);

            store.SetLimits(15, 3);
            using (RegistryKey k = OpenAgents()!)
            {
                T.True("limits are DWORDs", k.GetValueKind("MinIntervalMinutes") == RegistryValueKind.DWord && k.GetValueKind("MaxAgentJobs") == RegistryValueKind.DWord);
                T.True("with the values given", (int)k.GetValue("MinIntervalMinutes")! == 15 && (int)k.GetValue("MaxAgentJobs")! == 3);
            }
            T.True("the reader sees the limits", reader.Read() is { MinIntervalMinutes: 15, MaxAgentJobs: 3 });
            T.Throws<ArgumentOutOfRangeException>("a limit out of range is refused", () => store.SetLimits(0, null));
            T.True("and writes nothing", reader.Read() is { MinIntervalMinutes: 15, MaxAgentJobs: 3 });

            // A second suspend after a resume clears the old source.
            store.SetSuspended(true, "app", at.AddMinutes(10));
            using (RegistryKey k = OpenAgents()!) T.True("a new suspend removes ChangedFrom", k.GetValue("ChangedFrom") is null);

            // Everything above happened under the test key only.
            T.True("the real product key was not touched", Snapshot() == _before);
        }
        finally { TestFolder.CleanAgentState(); }
    }

    static void PutRaw(string name, object value, RegistryValueKind kind)
    {
        using RegistryKey k = Registry.CurrentUser.CreateSubKey(TestFolder.AgentsKey);
        k.SetValue(name, value, kind);
    }

    public static void HostileRegistryValuesFailClosed()
    {
        TestFolder.CleanAgentState();
        try
        {
            var reader = new RegistryAgentPolicyReader(Folder);
            var cases = new (string Label, object Value, RegistryValueKind Kind)[]
            {
                ("REG_SZ \"0\"", "0", RegistryValueKind.String),
                ("REG_SZ \"false\"", "false", RegistryValueKind.String),
                ("REG_EXPAND_SZ \"0\"", "0", RegistryValueKind.ExpandString),
                ("REG_QWORD 0", 0L, RegistryValueKind.QWord),
                ("REG_QWORD 1", 1L, RegistryValueKind.QWord),
                ("REG_DWORD 2", 2, RegistryValueKind.DWord),
                ("REG_DWORD 0xFFFFFFFF", -1, RegistryValueKind.DWord),
                ("REG_BINARY 00", new byte[] { 0 }, RegistryValueKind.Binary),
                ("REG_MULTI_SZ", new[] { "0" }, RegistryValueKind.MultiString),
            };
            foreach ((string label, object value, RegistryValueKind kind) in cases)
            {
                PutRaw("Suspended", value, kind);
                AgentPolicy p = reader.Read();
                T.True(label + ": suspended and failed closed", p is { Suspended: true, FailedClosed: true } && !string.IsNullOrEmpty(p.ReadProblem));
            }
            PutRaw("Suspended", 0, RegistryValueKind.DWord);
            T.True("DWORD 0 after all that: allowed", reader.Read() is { Suspended: false, FailedClosed: false });
            PutRaw("Suspended", 1, RegistryValueKind.DWord);
            T.True("DWORD 1: suspended, not failed closed", reader.Read() is { Suspended: true, FailedClosed: false });

            PutRaw("Suspended", 0, RegistryValueKind.DWord);
            PutRaw("MinIntervalMinutes", 0, RegistryValueKind.DWord);
            PutRaw("MaxAgentJobs", 501, RegistryValueKind.DWord);
            AgentPolicy limits = reader.Read();
            T.True("limits out of range: defaults and a note, still allowed", limits is { MinIntervalMinutes: 5, MaxAgentJobs: 20, Suspended: false, FailedClosed: false } && limits.ReadProblem is not null);
            PutRaw("MinIntervalMinutes", "15", RegistryValueKind.String);
            PutRaw("MaxAgentJobs", 5L, RegistryValueKind.QWord);
            T.True("limits of the wrong type: defaults and a note", reader.Read() is { MinIntervalMinutes: 5, MaxAgentJobs: 20, ReadProblem: not null });
            PutRaw("SuspendedAt", new string('9', 5000), RegistryValueKind.String);
            PutRaw("ChangedFrom", "a\r\nb" + new string('x', 5000), RegistryValueKind.String);
            AgentPolicy junk = reader.Read();
            T.True("an absurd date and a huge ChangedFrom are tolerated and capped", junk.SuspendedAt is null && (junk.ChangedFrom is null || (junk.ChangedFrom.Length <= 200 && !junk.ChangedFrom.Contains('\n', StringComparison.Ordinal))));

            T.True("hostile values never created the product key", Snapshot() == _before);
        }
        finally { TestFolder.CleanAgentState(); }
    }

    // ---- the real exe: status, suspend, resume, config, log ----

    public static void StatusWithNoKeyCreatesNothingAndShowsTheDefaults()
    {
        TestFolder.CleanAgentState();
        T.True("no key before", !KeyExists());
        RealCli.Result json = RealCli.Run(A("--agents", "status", "--json"));
        T.Eq("status --json: exit 0", 0, json.Exit);
        using (JsonDocument d = JsonDocument.Parse(json.Stdout))
        {
            JsonElement r = d.RootElement;
            T.Eq("folder", "\\CadenceTest", r.GetProperty("folder").GetString());
            T.Eq("not suspended", false, r.GetProperty("suspended").GetBoolean());
            T.Eq("default min interval", 5, r.GetProperty("minIntervalMinutes").GetInt32());
            T.Eq("default cap", 20, r.GetProperty("maxAgentJobs").GetInt32());
            T.Eq("no agent jobs", 0, r.GetProperty("agentJobs").GetInt32());
            T.Eq("not failed closed", false, r.GetProperty("failedClosed").GetBoolean());
            T.True("the audit log path is the test folder's", r.GetProperty("auditLog").GetString()!.EndsWith("\\CadenceTest\\agents\\audit.jsonl", StringComparison.OrdinalIgnoreCase));
        }
        RealCli.Result text = RealCli.Run(A("--agents", "status"));
        T.Eq("status (text): exit 0", 0, text.Exit);
        T.True("status (text): allowed with the defaults", text.Stdout.Contains("AI agents on this PC (\\CadenceTest)", StringComparison.Ordinal) && text.Stdout.Contains("allowed.", StringComparison.Ordinal) && text.Stdout.Contains("(default)", StringComparison.Ordinal));
        T.True("the key was NOT created by status", !KeyExists());
        T.True("no audit file for a read", !File.Exists(new FileAuditLog(Folder).FilePath));
        T.True("the task folder was not created either", !TestFolder.Exists());
        T.True("the product key is untouched", Snapshot() == _before);
    }

    public static void SuspendResumeConfigAndLogThroughTheRealExe()
    {
        TestFolder.CleanAgentState();
        try
        {
            string me = Process.GetCurrentProcess().ProcessName + ".exe";

            RealCli.Result s = RealCli.Run(A("--agents", "suspend"));
            T.Eq("suspend: exit 0", 0, s.Exit);
            T.True("suspend: says so", s.Stdout.Contains("AI agents are suspended on this PC (\\CadenceTest)", StringComparison.Ordinal));
            using (RegistryKey k = OpenAgents()!)
            {
                T.Eq("Suspended DWORD 1", 1, (int)k.GetValue("Suspended")!);
                T.Eq("SuspendedBy cli", "cli", (string)k.GetValue("SuspendedBy")!);
                T.True("SuspendedAt parses", DateTimeOffset.TryParse((string)k.GetValue("SuspendedAt")!, CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
            }
            using (JsonDocument d = StatusJson())
            {
                T.True("status --json: suspended, by cli, with a time", d.RootElement.GetProperty("suspended").GetBoolean() && d.RootElement.GetProperty("suspendedBy").GetString() == "cli" && d.RootElement.GetProperty("suspendedAt").ValueKind == JsonValueKind.String);
            }
            RealCli.Result st = RealCli.Run(A("--agents", "status"));
            T.True("status (text): SUSPENDED since ... from the command line", st.Stdout.Contains("SUSPENDED since", StringComparison.Ordinal) && st.Stdout.Contains("(from the command line)", StringComparison.Ordinal));

            // [OWNER-Q14] Resume is a plain, distinct command: no window, no --yes (an old --yes is accepted and ignored).
            RealCli.Result resume = RealCli.Run(A("--agents", "resume"));
            T.Eq("resume: exit 0, with no window and no --yes", 0, resume.Exit);
            T.True("resume says agents may change schedules again", resume.Stdout.Contains("may change your schedules again", StringComparison.Ordinal));
            AgentPolicy p = new RegistryAgentPolicyReader(Folder).Read();
            T.True("resumed", !p.Suspended && p.ChangedBy == "cli");
            T.True("ChangedFrom names this process's name as an ancestor ([CP-R2]): " + p.ChangedFrom, p.ChangedFrom is not null && p.ChangedFrom.Contains(me, StringComparison.OrdinalIgnoreCase));
            T.True("a resume through Cadence is not 'outside Cadence'", !p.ResumedOutsideCadence);
            using (JsonDocument d = StatusJson())
            {
                T.True("status --json after: not suspended, not outside, ChangedFrom shown", !d.RootElement.GetProperty("suspended").GetBoolean() && !d.RootElement.GetProperty("resumedOutsideCadence").GetBoolean() && d.RootElement.GetProperty("changedFrom").GetString() == p.ChangedFrom);
            }

            // Someone clears the value without Cadence (reg.exe, a script): detected.
            Assert0(RealCli.Run(A("--agents", "suspend")).Exit, "suspend again");
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(TestFolder.AgentsKey, writable: true)!) k.SetValue("Suspended", 0, RegistryValueKind.DWord);
            using (JsonDocument d = StatusJson())
                T.True("cleared without Cadence: resumedOutsideCadence is true", !d.RootElement.GetProperty("suspended").GetBoolean() && d.RootElement.GetProperty("resumedOutsideCadence").GetBoolean());
            RealCli.Result outside = RealCli.Run(A("--agents", "status"));
            T.True("status (text) warns", outside.Stdout.Contains("turned back on outside Cadence", StringComparison.Ordinal));

            // config
            RealCli.Result cfg = RealCli.Run(A("--agents", "config", "--min-interval", "15", "--max-jobs", "3"));
            T.Eq("config: exit 0", 0, cfg.Exit);
            using (JsonDocument d = StatusJson())
                T.True("status shows the new limits", d.RootElement.GetProperty("minIntervalMinutes").GetInt32() == 15 && d.RootElement.GetProperty("maxAgentJobs").GetInt32() == 3);
            T.Eq("config --min-interval 0: exit 2", 2, RealCli.Run(A("--agents", "config", "--min-interval", "0")).Exit);
            T.Eq("config --max-jobs 501: exit 2", 2, RealCli.Run(A("--agents", "config", "--max-jobs", "501")).Exit);
            T.Eq("config with nothing: exit 2", 2, RealCli.Run(A("--agents", "config")).Exit);
            using (JsonDocument d = StatusJson())
                T.True("a refused config changed nothing", d.RootElement.GetProperty("minIntervalMinutes").GetInt32() == 15 && d.RootElement.GetProperty("maxAgentJobs").GetInt32() == 3);

            // the audit log
            IReadOnlyList<AuditEntry> log = Audit();
            T.True("a 'suspended' line from the cli", log.Any(e => e.Outcome == "suspended" && e.Tool == "cli" && e.Client == "the command line"));
            T.True("the suspend line names the parent process (" + me + ")", log.Any(e => e.Outcome == "suspended" && e.Detail.Contains(me, StringComparison.OrdinalIgnoreCase)));
            T.True("a 'resumed' line with the parent process name", log.Any(e => e.Outcome == "resumed" && e.Tool == "cli" && e.Detail.Contains(me, StringComparison.OrdinalIgnoreCase)));
            T.True("the limits change is logged", log.Any(e => e.Code == "limits_changed"));
            T.True("usage errors wrote no audit line: exactly suspend, resumed, suspend, limits (" + log.Count + ")", log.Count == 4);

            RealCli.Result tail = RealCli.Run(A("--agents", "log", "--max", "4"));
            T.Eq("log --max 4: exit 0", 0, tail.Exit);
            T.Eq("log --max 4 prints four lines", 4, tail.Stdout.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
            T.True("the newest is the limits change", tail.Stdout.TrimEnd().Split("\r\n")[^1].Contains("limits_changed", StringComparison.Ordinal));
            RealCli.Result jsonLog = RealCli.Run(A("--agents", "log", "--max", "2", "--json"));
            string[] jl = jsonLog.Stdout.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            T.Eq("log --json: two JSON lines", 2, jl.Length);
            foreach (string l in jl) { using JsonDocument d = JsonDocument.Parse(l); T.Eq("v:1", 1, d.RootElement.GetProperty("v").GetInt32()); }

            T.True("the product key and folder were never touched", Snapshot() == _before);
        }
        finally { TestFolder.CleanAgentState(); }
    }

    static void Assert0(int exit, string label) => T.Eq(label + ": exit 0", 0, exit);

    // ---- a running MCP session obeys a flag flipped between two calls ----

    public static void ARunningServerObeysTheFlagOnTheVeryNextCall()
    {
        TestFolder.CleanAgentState();
        using var c = new McpTestClient(["--mcp", "--folder", Folder]);
        try
        {
            T.True("initialize answered", c.Request("initialize", InitializeParams) is not null);
            c.Notify("notifications/initialized");

            JsonElement a0 = AgentsOf(c);
            T.Eq("call 1: allowed", false, a0.GetProperty("suspended").GetBoolean());
            T.True("call 1: no key was created by the server's read", !KeyExists());

            T.Eq("CLI suspend: exit 0", 0, RealCli.Run(A("--agents", "suspend")).Exit);
            JsonElement a1 = AgentsOf(c);   // the SAME session, no restart
            T.Eq("call 2 (same session): suspended", true, a1.GetProperty("suspended").GetBoolean());
            T.Eq("call 2: by cli", "cli", a1.GetProperty("suspendedBy").GetString());
            T.True("call 2: with a time", a1.GetProperty("suspendedAt").ValueKind == JsonValueKind.String);
            T.True("the read tools keep working while suspended", ToolCall(c, "cadence_list_jobs").GetProperty("result").GetProperty("isError").GetBoolean() == false
                && ToolCall(c, "cadence_list_templates").GetProperty("result").GetProperty("isError").GetBoolean() == false);

            T.Eq("CLI resume: exit 0", 0, RealCli.Run(A("--agents", "resume")).Exit);
            JsonElement a2 = AgentsOf(c);
            T.Eq("call 3 (same session): allowed again", false, a2.GetProperty("suspended").GetBoolean());
            T.Eq("call 3: not 'outside Cadence'", false, a2.GetProperty("resumedOutsideCadence").GetBoolean());

            T.Eq("CLI config: exit 0", 0, RealCli.Run(A("--agents", "config", "--min-interval", "15", "--max-jobs", "3")).Exit);
            JsonElement a3 = AgentsOf(c);
            T.True("call 4: the new limits", a3.GetProperty("minIntervalMinutes").GetInt32() == 15 && a3.GetProperty("maxAgentJobs").GetInt32() == 3);
            JsonElement prev = Structured(ToolCall(c, "cadence_preview_schedule", "{\"cron\":[\"*/10 * * * *\"]}"));
            T.Eq("the preview uses the live limit: 10 minutes is now too frequent", false, prev.GetProperty("agentsMaySave").GetBoolean());

            // Fail closed, live: wrong types and values are refused on the very next call, and a good value is trusted again.
            PutRaw("Suspended", "0", RegistryValueKind.String);
            JsonElement f1 = AgentsOf(c);
            T.True("REG_SZ \"0\": suspended with the reason", f1.GetProperty("suspended").GetBoolean() && f1.GetProperty("readProblem").GetString()!.Contains("Suspended", StringComparison.Ordinal));
            PutRaw("Suspended", 2, RegistryValueKind.DWord);
            T.True("DWORD 2: suspended", AgentsOf(c).GetProperty("suspended").GetBoolean());
            PutRaw("Suspended", 7L, RegistryValueKind.QWord);
            T.True("REG_QWORD: suspended", AgentsOf(c).GetProperty("suspended").GetBoolean());
            PutRaw("Suspended", 0, RegistryValueKind.DWord);
            T.True("DWORD 0 again: allowed", !AgentsOf(c).GetProperty("suspended").GetBoolean());
            Registry.CurrentUser.DeleteSubKeyTree(TestFolder.AgentsKey, throwOnMissingSubKey: false);   // the registry only: the audit log is read below
            JsonElement gone = AgentsOf(c);
            T.True("the key deleted: allowed with the defaults", !gone.GetProperty("suspended").GetBoolean() && gone.GetProperty("minIntervalMinutes").GetInt32() == 5 && gone.GetProperty("maxAgentJobs").GetInt32() == 20);
            T.True("and the server's reads never recreated it", !KeyExists());

            bool exited = c.CloseStdinAndWait(8000);
            T.True("the server exits when stdin closes", exited);
        }
        finally { c.Kill(); }

        IReadOnlyList<AuditEntry> log = Audit();
        AuditEntry? connected = log.FirstOrDefault(e => e.Outcome == "connected");
        T.True("a connected line with the client's name and version", connected is not null && connected.Client == "Cadence Test Agent" && connected.ClientVersion == "1" && connected.Tool == "session" && connected.Detail.StartsWith("protocol 2025-11-25", StringComparison.Ordinal));
        T.True("a disconnected line for the same session", connected is not null && log.Any(e => e.Outcome == "disconnected" && e.Session == connected.Session));
        T.True("the session id is 8 hex characters", connected is not null && connected.Session.Length == 8 && connected.Session.All(Uri.IsHexDigit));
        T.True("the suspend/resume lines are in the same log", log.Any(e => e.Outcome == "suspended") && log.Any(e => e.Outcome == "resumed"));
        TestFolder.CleanAgentState();
        T.True("the product key was never touched", Snapshot() == _before);
    }

    // ---- [OWNER-Q14] an agent can suspend and resume through two distinct tools, and nothing else about the policy ----

    public static void AnAgentUsesTheTwoDistinctControlToolsAndNothingElse()
    {
        TestFolder.CleanAgentState();
        using var c = new McpTestClient(["--mcp", "--folder", Folder]);
        try
        {
            c.Request("initialize", InitializeParams);
            c.Notify("notifications/initialized");

            JsonElement? list = c.Request("tools/list");
            string[] names = [.. list!.Value.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()!)];
            T.True("the server lists the six read tools, the four write tools (Phases 21-22) and exactly the two control tools", names.OrderBy(n => n, StringComparer.Ordinal).SequenceEqual(["cadence_delete_job", "cadence_get_job", "cadence_get_run_output", "cadence_get_status", "cadence_list_jobs", "cadence_list_templates", "cadence_preview_schedule", "cadence_resume_agents", "cadence_run_job_now", "cadence_save_job", "cadence_set_job_enabled", "cadence_suspend_agents"]));

            // Everything else about the policy is still not a tool.
            foreach (string attempt in new[] { "cadence_resume", "cadence_set_suspended", "cadence_set_limits", "cadence_agents", "cadence_set_policy", "cadence_unsuspend", "cadence_set_job_lock", "agents/resume", "resume" })
            {
                JsonElement r = ToolCall(c, attempt, "{\"suspended\":false,\"resume\":true}");
                T.True(attempt + ": JSON-RPC -32602 Unknown tool", r.TryGetProperty("error", out JsonElement err) && err.GetProperty("code").GetInt32() == -32602 && err.GetProperty("message").GetString()!.StartsWith("Unknown tool", StringComparison.Ordinal));
            }

            // The owner suspends from the command line; the agent resumes through its own tool, while suspended.
            T.Eq("CLI suspend: exit 0", 0, RealCli.Run(A("--agents", "suspend")).Exit);
            T.True("suspended", AgentsOf(c).GetProperty("suspended").GetBoolean());
            JsonElement resumed = Structured(ToolCall(c, "cadence_resume_agents", "{\"reason\":\"the owner asked me to carry on\"}"));
            T.True("cadence_resume_agents: now allowed, changed", !resumed.GetProperty("suspended").GetBoolean() && resumed.GetProperty("changed").GetBoolean());
            AgentPolicy p = new RegistryAgentPolicyReader(Folder).Read();
            T.True("the registry says allowed, changed by this agent (agent:<client name>), with the reason", p is { Suspended: false, ChangedBy: "agent:Cadence Test Agent", ChangedReason: "the owner asked me to carry on" });
            T.True("it is not 'outside Cadence'", !p.ResumedOutsideCadence);
            JsonElement st = AgentsOf(c);
            T.True("the next status shows who changed it", !st.GetProperty("suspended").GetBoolean() && st.GetProperty("changedBy").GetString() == "agent:Cadence Test Agent" && st.GetProperty("changedReason").GetString() == "the owner asked me to carry on");
            T.True("the command line status names the agent as an AI agent", RealCli.Run(A("--agents", "status")).Stdout.Contains("AI agent Cadence Test Agent", StringComparison.Ordinal));

            // The agent can also suspend itself, and again is idempotent.
            JsonElement suspended = Structured(ToolCall(c, "cadence_suspend_agents"));
            T.True("cadence_suspend_agents: suspended, changed", suspended.GetProperty("suspended").GetBoolean() && suspended.GetProperty("changed").GetBoolean());
            using (RegistryKey k = OpenAgents()!)
            {
                T.Eq("Suspended DWORD 1", 1, (int)k.GetValue("Suspended")!);
                T.Eq("SuspendedBy is the agent", "agent:Cadence Test Agent", (string)k.GetValue("SuspendedBy")!);
            }
            T.True("suspending again changes nothing", !Structured(ToolCall(c, "cadence_suspend_agents")).GetProperty("changed").GetBoolean());

            // A value cannot be set through any other argument, and the limits are untouched by all of this.
            foreach (string tool in new[] { "cadence_suspend_agents", "cadence_resume_agents" })
            {
                JsonElement r = ToolCall(c, tool, "{\"suspended\":false,\"minIntervalMinutes\":1,\"maxAgentJobs\":500}");
                T.True(tool + ": any argument but reason is refused", r.GetProperty("result").GetProperty("isError").GetBoolean() && r.GetProperty("result").GetProperty("structuredContent").GetProperty("error").GetProperty("code").GetString() == "invalid_argument");
            }
            T.True("the limits are still the defaults", AgentsOf(c).GetProperty("minIntervalMinutes").GetInt32() == 5 && AgentsOf(c).GetProperty("maxAgentJobs").GetInt32() == 20);

            // The owner can resume from the command line as before; every route is in the same audit log.
            T.Eq("CLI resume: exit 0", 0, RealCli.Run(A("--agents", "resume")).Exit);
            T.True("allowed again", !AgentsOf(c).GetProperty("suspended").GetBoolean());
            IReadOnlyList<AuditEntry> log = Audit();
            T.True("the agent's resume is audited with its tool, client and reason", log.Any(e => e.Outcome == "resumed" && e.Tool == "cadence_resume_agents" && e.Client == "Cadence Test Agent" && e.Detail.Contains("reason: the owner asked me to carry on", StringComparison.Ordinal)));
            T.True("the agent's suspend is audited", log.Any(e => e.Outcome == "suspended" && e.Tool == "cadence_suspend_agents" && e.Client == "Cadence Test Agent"));
            T.True("the command line's changes are in the same log", log.Any(e => e.Outcome == "suspended" && e.Tool == "cli") && log.Any(e => e.Outcome == "resumed" && e.Tool == "cli"));
            c.CloseStdinAndWait(8000);
        }
        finally { c.Kill(); TestFolder.CleanAgentState(); }
        T.True("the product key was never touched", Snapshot() == _before);
    }

    // ---- lock / unlock on a real task, and the agent job count ----

    public static void LockUnlockAndTheAgentJobCountOnRealTasks() => E2E.Scenario(e =>
    {
        const string Id = "lockjob1";
        JobDefinition job = Fx.Job("lock test job", ["0 18 * * *"]) with { Id = Id };
        var appSaver = new Cadence.Scheduler.Apply.Applier(new ManagedJobs(e.Gw, () => Fx.Now, Actor.App));
        T.True("the app creates the job", appSaver.Save(job, null).Success);
        JobDefinition agentJob = Fx.Job("made by an agent", ["0 19 * * *"]) with { Id = "agentjb1" };
        T.True("an agent-created job", new Cadence.Scheduler.Apply.Applier(new ManagedJobs(e.Gw, () => Fx.Now, Actor.Agent("Claude Code"))).Save(agentJob, null).Success);

        using JsonDocument st = StatusJson();
        T.Eq("status: the owner's job is not an agent job, the other is", 1, st.RootElement.GetProperty("agentJobs").GetInt32());

        string xmlBefore = e.Gw.Get("lock test job")!.Xml;
        T.True("starts unlocked", !Meta(e, "lock test job").Job.AgentLock);

        RealCli.Result lockIt = RealCli.Run(A("--agents", "lock", Id, "--yes"));
        T.Eq("lock: exit 0 (" + lockIt.Stdout.Trim() + ")", 0, lockIt.Exit);
        CadenceMetadata m = Meta(e, "lock test job");
        T.True("agentLock is stored in the task's metadata", m.Job.AgentLock);
        T.Eq("updatedBy is cli", "cli", m.UpdatedBy);
        T.Eq("createdBy is still the app", "app", m.CreatedBy);
        T.Eq("the task is still InSync", SyncStatus.InSync, e.Jobs.Reclassify("lock test job")!.Sync);

        // A no-op lock writes nothing (the stored XML is byte for byte what it was).
        string lockedXml = e.Gw.Get("lock test job")!.Xml;
        RealCli.Result again = RealCli.Run(A("--agents", "lock", Id, "--yes"));
        T.True("locking again: exit 0 and 'already locked'", again.Exit == 0 && again.Stdout.Contains("already locked", StringComparison.Ordinal));
        T.Eq("the stored XML is unchanged by a no-op", Cadence.IntegrationTests.ApplyFx.Sha(lockedXml), Cadence.IntegrationTests.ApplyFx.Sha(e.Gw.Get("lock test job")!.Xml));

        // The MCP tools report it.
        using (var c = new McpTestClient(["--mcp", "--folder", Folder]))
        {
            try
            {
                c.Request("initialize", InitializeParams);
                JsonElement view = Structured(ToolCall(c, "cadence_get_job", "{\"jobId\":\"" + Id + "\"}"));
                T.Eq("cadence_get_job reports lockedForAgents", true, view.GetProperty("lockedForAgents").GetBoolean());
                T.Eq("and updated by the command line", "cli", view.GetProperty("updated").GetProperty("byKind").GetString());
                JsonElement status = AgentsOf(c);
                T.Eq("cadence_get_status counts the agent-created job only", 1, status.GetProperty("agentJobs").GetInt32());
                c.CloseStdinAndWait(8000);
            }
            finally { c.Kill(); }
        }

        RealCli.Result unlock = RealCli.Run(A("--agents", "unlock", Id, "--yes"));
        T.Eq("unlock: exit 0", 0, unlock.Exit);
        T.True("agentLock is cleared", !Meta(e, "lock test job").Job.AgentLock);
        T.True("a foreign or missing id is refused", RealCli.Run(A("--agents", "lock", "nosuchid", "--yes")).Exit == 1);
        T.Eq("a bad usage (no --yes) is exit 2", 2, RealCli.Run(A("--agents", "lock", Id)).Exit);
        T.True("the job's other settings are as they were (only the lock and the stamps changed)", Meta(e, "lock test job").Job == PlanSamplesJob(xmlBefore));
        IReadOnlyList<AuditEntry> log = Audit();
        T.True("lock and unlock are audited with the job id and name", log.Any(x => x.Code == "locked" && x.JobId == Id && x.JobName == "lock test job") && log.Any(x => x.Code == "unlocked" && x.JobId == Id));
        _ = xmlBefore;
    }, spawnsProcesses: true);

    static JobDefinition PlanSamplesJob(string xml)
    {
        TaskDefinitionModel model = TaskXmlReader.Read(xml);
        MetadataCodec.TryDecode(model.RegistrationInfo.Documentation, out CadenceMetadata? m);
        return m!.Job;
    }

    static CadenceMetadata Meta(E2E e, string taskName)
    {
        TaskDefinitionModel model = TaskXmlReader.Read(e.Gw.Get(taskName)!.Xml);
        T.Eq("'" + taskName + "' metadata decodes", MetadataProblem.None, MetadataCodec.TryDecode(model.RegistrationInfo.Documentation, out CadenceMetadata? m));
        return m!;
    }

    // ---- the end ----

    public static void ZzNothingIsLeftBehind()
    {
        TestFolder.CleanAgentState();
        T.True("the agents key is gone", !KeyExists());
        T.True("no audit directory is left", !Directory.Exists(Path.GetDirectoryName(new FileAuditLog(Folder).FilePath)!));
        T.True("the product's key, agents key, local folder were not touched by these tests", Snapshot() == _before);
        T.True("every CLI process started here has exited (" + RealCli.TrackedPids.Length + " started)", RealCli.TrackedPids.Length >= 20 && !RealCli.AnyAlive());
        T.True("and every MCP server too", !McpTestClient.AnyTrackedAlive());
    }
}
