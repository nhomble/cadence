using System.IO;
using System.Globalization;
using Cadence.Core;
using Cadence.Core.Cron;
using Cadence.Core.Execution;
using Cadence.Core.Holidays;
using Cadence.Core.Metadata;
using Cadence.Core.Model;
using Cadence.Core.Scheduling;
using Cadence.Core.TaskXml;
using Cadence.Core.Text;
using Cadence.Scheduler;

namespace Cadence.App.Cli;

/// <summary>
/// <c>--selftest</c> (PLAN 7.6): fast checks of the engine plus the machine facts Cadence depends on. Read-only: the Task Scheduler check
/// only connects and lists, and the writable check makes and deletes one file directly in %LOCALAPPDATA% (it never creates the
/// Cadence folder). Output is one "PASS name" or "FAIL name: reason" line per check, then a count; the exit code is 0 only when all pass.
/// </summary>
public static class SelfTest
{
    /// <summary>Fixed so the composed output never depends on the clock.</summary>
    static readonly DateTimeOffset FixedNow = new(2026, 10, 1, 9, 0, 0, TimeSpan.FromHours(-4));

    public static int Run(ITaskSchedulerGateway gateway, string localAppData, string systemRoot, Action<string> write)
    {
        int pass = 0, fail = 0;
        void Check(string name, Func<string?> body)
        {
            string? problem;
            string detail = "";
            try
            {
                problem = body();
                if (problem is not null && problem.StartsWith('+')) { detail = " (" + problem[1..] + ")"; problem = null; }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                problem = "threw " + ex.GetType().Name + ": " + ex.Message;
            }
            if (problem is null) { pass++; write("PASS " + name + detail + "\r\n"); }
            else { fail++; write("FAIL " + name + ": " + problem + "\r\n"); }
        }

        Check("templates compile and render", TemplatesCompile);
        Check("cron 0 9-17 * * 1-5 compiles", WeekdayCron);
        Check("holiday rules: 2026-11-26 is NYSE Thanksgiving Day", Holidays);
        Check("next runs skip the NYSE holiday", NextRunsSkip);
        Check("compose, XML, read, fingerprint stable", ComposeRoundTrip);
        Check("metadata encode/decode round trip", MetadataRoundTrip);
        Check("wrapper invariants", WrapperInvariants);
        Check("version text", () => AppVersion.Display().Length > 0 ? "+" + AppVersion.Display() : "empty");
        Check("mcp: in-memory initialize, tools/list and cadence_get_status", McpInMemory);
        Check("Task Scheduler connects (read-only)", () => Connect(gateway));
        Check("%LOCALAPPDATA% is writable", () => Writable(localAppData));
        Check("conhost.exe and powershell.exe exist", () => ToolsExist(systemRoot));

        write("selftest: " + pass.ToString(CultureInfo.InvariantCulture) + " passed, " + fail.ToString(CultureInfo.InvariantCulture) + " failed\r\n");
        return fail == 0 ? 0 : 1;
    }

    static JobDefinition Sample(string cron, HolidayCalendar skip = HolidayCalendar.None) => new()
    {
        Id = "selftest",
        Name = "Self test",
        Enabled = false,
        Schedule = new ScheduleSpec(new ValueList<string>([cron]), skip, new DateOnly(2099, 1, 1)),
        Run = new RunSpec { Exe = "cmd.exe", Args = "/c exit 0" },
    };

    static ComposeContext Ctx(string user) =>
        new("Cadence", "Cadence", user, "Self test", FixedNow, null, ValueList<string>.Empty);

    static string? TemplatesCompile()
    {
        int n = 0;
        foreach (Template t in Templates.All)
        {
            var spec = new ScheduleSpec(new ValueList<string>([t.Cron]), t.SuggestedSkip, new DateOnly(2026, 10, 1));
            CompiledSchedule c = CronCompiler.Compile(spec, new MapContext(@"HOST\user"));
            if (!c.Success) return "template '" + t.Id + "' does not compile: " + string.Join(" ", c.Diagnostics.Select(d => d.Message));
            string text = EnglishRenderer.Describe(spec);
            if (string.IsNullOrWhiteSpace(text) || text == "Invalid schedule") return "template '" + t.Id + "' does not render";
            n++;
        }
        return n == 9 ? "+9 templates" : "expected 9 templates, found " + n.ToString(CultureInfo.InvariantCulture);
    }

    static string? WeekdayCron()
    {
        var spec = new ScheduleSpec(new ValueList<string>(["0 9-17 * * 1-5"]), HolidayCalendar.None, new DateOnly(2026, 10, 1));
        CompiledSchedule c = CronCompiler.Compile(spec, new MapContext(@"HOST\user"));
        if (!c.Success) return string.Join(" ", c.Diagnostics.Select(d => d.Message));
        if (c.Triggers.Count == 0) return "no triggers";
        return "+" + c.Triggers.Count.ToString(CultureInfo.InvariantCulture) + " trigger(s): " + EnglishRenderer.Describe(spec);
    }

    static string? Holidays()
    {
        Holiday? h = HolidayRules.Find(HolidayCalendar.Nyse, new DateOnly(2026, 11, 26));
        if (h is null || h.Name != "Thanksgiving Day") return "found '" + (h?.Name ?? "nothing") + "'";
        int federal = HolidayRules.For(HolidayCalendar.UsFederal, 2026).Count;
        int nyse = HolidayRules.For(HolidayCalendar.Nyse, 2026).Count;
        return federal == 11 && nyse == 10 ? "+2026: 11 federal, 10 NYSE" : "2026 counts were " + federal.ToString(CultureInfo.InvariantCulture) + " federal and " + nyse.ToString(CultureInfo.InvariantCulture) + " NYSE";
    }

    static string? NextRunsSkip()
    {
        var spec = new ScheduleSpec(new ValueList<string>(["0 18 * * 1-5"]), HolidayCalendar.Nyse, new DateOnly(2026, 10, 1));
        CompiledSchedule c = CronCompiler.Compile(spec, new MapContext(@"HOST\user"));
        if (!c.Success) return "does not compile";
        // Monday 2026-11-23 00:00 UTC, evaluated in UTC: the 4th run is Thursday the 26th.
        var after = new DateTimeOffset(2026, 11, 23, 0, 0, 0, TimeSpan.Zero);
        IReadOnlyList<Occurrence> runs = NextRuns.Next(c.Triggers, HolidayCalendar.Nyse, after, 5, TimeZoneInfo.Utc);
        if (runs.Count != 5) return "expected 5 runs, got " + runs.Count.ToString(CultureInfo.InvariantCulture);
        if (runs[3].At.Day != 26 || runs[3].SkippedHoliday != "Thanksgiving Day") return "4th run was " + runs[3].At.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " / " + (runs[3].SkippedHoliday ?? "not marked");
        return runs.Count(r => r.SkippedHoliday is not null) == 1 ? null : "more than one run marked as a holiday";
    }

    static string? ComposeRoundTrip()
    {
        JobDefinition job = Sample("0 18 * * 1-5", HolidayCalendar.Nyse);
        TaskDefinitionModel model = TaskComposer.Compose(job, Ctx(@"HOST\user"));
        string xml = TaskXmlWriter.Write(model);
        TaskDefinitionModel read = TaskXmlReader.Read(xml);
        string fp = Fingerprint.Of(model);
        if (Fingerprint.Of(read) != fp) return "fingerprint changed after writing and reading the XML";
        if (Fingerprint.Of(TaskXmlReader.Read(TaskXmlWriter.Write(read))) != fp) return "fingerprint changed after a second round trip";
        if (MetadataCodec.TryDecode(read.RegistrationInfo.Documentation, out CadenceMetadata? meta) != MetadataProblem.None) return "the metadata did not decode";
        return meta!.Fp == fp ? "+fp " + fp : "metadata fp " + meta.Fp + " differs from " + fp;
    }

    static string? MetadataRoundTrip()
    {
        JobDefinition job = Sample("30 17 * * 1-5");
        TaskDefinitionModel model = TaskComposer.Compose(job, Ctx(@"HOST\user"));
        if (MetadataCodec.TryDecode(model.RegistrationInfo.Documentation, out CadenceMetadata? meta) != MetadataProblem.None) return "did not decode";
        if (!Equals(meta!.Job, job)) return "the decoded job differs from the encoded one";
        string again = MetadataCodec.Encode(meta);
        return again == model.RegistrationInfo.Documentation ? null : "encoding again gave different text";
    }

    static string? WrapperInvariants()
    {
        const string path = @"\Cadence\Self test";
        ExecActionModel hidden = WrapperScript.BuildAction(path, "Cadence", hidden: true);
        ExecActionModel visible = WrapperScript.BuildAction(path, "Cadence", hidden: false);
        if (!WrapperScript.TryGetTaskPath(hidden, out string tp) || tp != path) return "hidden action does not name its task";
        if (!WrapperScript.TryGetTaskPath(visible, out tp) || tp != path) return "visible action does not name its task";
        if (!hidden.Command.EndsWith("conhost.exe", StringComparison.OrdinalIgnoreCase) || !(hidden.Arguments ?? "").StartsWith("--headless ", StringComparison.Ordinal)) return "hidden action is not conhost --headless";
        if (!visible.Command.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase)) return "visible action is not powershell";
        string script = WrapperScript.Build(path, "Cadence");
        if (script.Contains("{TASKPATH}", StringComparison.Ordinal) || script.Contains("{RUNSROOT}", StringComparison.Ordinal)) return "a placeholder is left in the script";
        if (script.Contains('\n', StringComparison.Ordinal) || script.Contains('"', StringComparison.Ordinal)) return "the script is not one line without double quotes";
        if (WrapperScript.ValidateTaskPath(@"\Cadence\it's") || WrapperScript.ValidateTaskPath(@"\Cadence\a;b") || WrapperScript.ValidateTaskPath(@"\Cadence\..\x")) return "an unsafe task path was accepted";
        return null;
    }

    static string? Connect(ITaskSchedulerGateway gw)
    {
        string user = gw.ConnectedUserId;
        if (string.IsNullOrWhiteSpace(user)) return "no connected user";
        if (!gw.ConnectedUserSid.StartsWith("S-1-", StringComparison.Ordinal)) return "no user SID";
        int n = gw.ListManagedFolder().Count;
        return "+" + user + ", " + n.ToString(CultureInfo.InvariantCulture) + " task(s) in \\" + gw.Options.FolderName;
    }

    static string? Writable(string localAppData)
    {
        if (!Directory.Exists(localAppData)) return localAppData + " does not exist";
        string probe = Path.Combine(localAppData, "cadence-selftest-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    /// <summary>Runs the MCP server over in-memory streams: no process, no Task Scheduler (the job count is a constant), nothing written.</summary>
    static string? McpInMemory()
    {
        var env = new Cadence.Agent.Tools.AgentEnvironment(new EmptyGateway(), new Cadence.History.RunRecordStore(Path.Combine(Path.GetTempPath(), "cadence-selftest-runs")),
            () => FixedNow, TimeZoneInfo.Utc, new Cadence.Agent.Tools.AgentFileProbe(), "selftest",
            new Cadence.Agent.Policy.MemoryAgentPolicyStore(), Cadence.Agent.Audit.NullAuditLog.Instance);
        var host = new Cadence.Agent.Tools.CadenceToolHost(env);
        var server = new Cadence.Mcp.McpServer(new Cadence.Mcp.McpServerInfo("cadence", "Cadence", "selftest"), host, TextWriter.Null);
        string[] lines =
        [
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{\"name\":\"selftest\",\"version\":\"1\"}}}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}",
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}",
            "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"cadence_get_status\",\"arguments\":{}}}",
        ];
        var input = new MemoryStream(new System.Text.UTF8Encoding(false).GetBytes(string.Join("\n", lines) + "\n"));
        var output = new MemoryStream();
        int rc = server.Run(input, output);
        if (rc != 0) return "the server exited with " + rc.ToString(CultureInfo.InvariantCulture);
        string[] replies = System.Text.Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (replies.Length != 3) return "expected 3 replies, got " + replies.Length.ToString(CultureInfo.InvariantCulture);
        var byId = new Dictionary<long, System.Text.Json.JsonElement>();
        foreach (string r in replies)
        {
            System.Text.Json.JsonElement e = System.Text.Json.JsonDocument.Parse(r).RootElement;
            byId[e.GetProperty("id").GetInt64()] = e;
        }
        if (byId[1].GetProperty("result").GetProperty("protocolVersion").GetString() != "2025-11-25") return "initialize did not echo 2025-11-25";
        System.Text.Json.JsonElement tools = byId[2].GetProperty("result").GetProperty("tools");
        if (tools.GetArrayLength() != 6 || tools[0].GetProperty("name").GetString() != "cadence_get_status") return "tools/list is not the six read-only tools";
        System.Text.Json.JsonElement st = byId[3].GetProperty("result");
        if (st.GetProperty("isError").GetBoolean()) return "cadence_get_status returned an error";
        return st.GetProperty("structuredContent").GetProperty("folder").GetString() == "\\CadenceTest" ? null : "wrong folder in the status";
    }

    /// <summary>A Task Scheduler with no tasks and no way to write: the in-memory MCP check must not touch the real one.</summary>
    sealed class EmptyGateway : ITaskSchedulerGateway
    {
        public SchedulerOptions Options { get; } = new("CadenceTest", "CadenceTest");
        public string ConnectedUserId => "HOST\\user";
        public string ConnectedUserSid => "S-1-5-21-0-0-0-1";
        public bool IsConnectedUser(string? principalUserId) => false;
        public IReadOnlyList<RegisteredTaskInfo> ListManagedFolder() => [];
        public IReadOnlyList<RegisteredTaskInfo> ListOtherTasks(int max = 5000) => [];
        public RegisteredTaskInfo? Get(string taskName) => null;
        public RegisteredTaskInfo Register(string taskName, string xml, RegisterMode mode = RegisterMode.CreateOrUpdate) => throw new NotSupportedException();
        public void Delete(string taskName) => throw new NotSupportedException();
        public void Run(string taskName) => throw new NotSupportedException();
        public void Stop(string taskName) => throw new NotSupportedException();
        public void DeleteFolderIfEmpty() => throw new NotSupportedException();
    }

    static string? ToolsExist(string systemRoot)
    {
        string conhost = Path.Combine(systemRoot, "System32", "conhost.exe");
        string powershell = Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(conhost)) return conhost + " is missing";
        if (!File.Exists(powershell)) return powershell + " is missing";
        return null;
    }
}
