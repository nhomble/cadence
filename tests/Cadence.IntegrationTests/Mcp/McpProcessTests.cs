using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Cadence.Scheduler.Com;
using Cadence.TestHarness;
using Microsoft.Win32;

namespace Cadence.IntegrationTests.Mcp;

/// <summary>
/// Phase 17: the real build of Cadence.exe --mcp --folder CadenceTest spawned as a child process and driven over pipes by
/// <see cref="McpTestClient"/>. Read-only against Task Scheduler (the status tool lists \CadenceTest, which usually does not exist and is never
/// created). The checks that nothing named Cadence (no \Cadence folder, no %LOCALAPPDATA%\Cadence, no HKCU\Software\Cadence) appeared
/// bracket the whole class: <see cref="AaStartSnapshot"/> first, <see cref="ZzNothingAppearedAndNoStrayProcess"/> last.
/// </summary>
public static class McpProcessTests
{
    const string InitializeParams = "{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{\"name\":\"cadence-tests\",\"title\":\"Cadence Test Agent\",\"version\":\"1\"}}";

    static string[] ServerArgs => ["--mcp", "--folder", "CadenceTest"];

    sealed record Footprint(bool LocalDir, bool HkcuKey, bool TaskFolder);

    static Footprint? _before;

    static Footprint Snapshot()
    {
        string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cadence");
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Cadence");
        bool folder;
        using (TaskServiceSession s = TaskServiceSession.Open())
        {
            object? f = s.TryGetFolder("\\Cadence");
            folder = f is not null;
            ComRelease.Release(f);
        }
        return new Footprint(Directory.Exists(local), key is not null, folder);
    }

    public static void AaStartSnapshot()
    {
        _before = Snapshot();
        T.True("the server exe exists (" + McpTestClient.ExePath() + ")", File.Exists(McpTestClient.ExePath()));
    }

    static bool ValidJsonRpcLine(string line)
    {
        try
        {
            JsonElement e = JsonDocument.Parse(line).RootElement;
            if (e.ValueKind != JsonValueKind.Object) return false;
            if (!e.TryGetProperty("jsonrpc", out JsonElement v) || v.GetString() != "2.0") return false;
            bool hasId = e.TryGetProperty("id", out _);
            bool result = e.TryGetProperty("result", out _), error = e.TryGetProperty("error", out _), method = e.TryGetProperty("method", out _);
            return (hasId && (result ^ error)) || (!hasId && method);
        }
        catch (JsonException) { return false; }
    }

    public static void FullSessionOverRealPipes()
    {
        using var c = new McpTestClient(ServerArgs);
        try
        {
            T.Eq("DOTNET_DISABLE_GUI_ERRORS is set for the spawn (client side)", "1", c.Env["DOTNET_DISABLE_GUI_ERRORS"]);
            long maxHandle = c.MainWindowHandle;

            JsonElement? init = c.Request("initialize", InitializeParams);
            T.True("initialize answered", init is not null);
            if (init is null) return;
            JsonElement res = init.Value.GetProperty("result");
            T.Eq("protocolVersion echoed", "2025-11-25", res.GetProperty("protocolVersion").GetString());
            T.Eq("serverInfo.name", "cadence", res.GetProperty("serverInfo").GetProperty("name").GetString());
            T.Eq("serverInfo.title", "Cadence", res.GetProperty("serverInfo").GetProperty("title").GetString());
            T.True("serverInfo.version present", !string.IsNullOrEmpty(res.GetProperty("serverInfo").GetProperty("version").GetString()));
            T.Eq("tools capability", false, res.GetProperty("capabilities").GetProperty("tools").GetProperty("listChanged").GetBoolean());
            T.True("instructions present", res.GetProperty("instructions").GetString()!.StartsWith("Cadence manages scheduled jobs", StringComparison.Ordinal));
            maxHandle = Math.Max(maxHandle, c.MainWindowHandle);

            c.Notify("notifications/initialized");

            JsonElement? ping = c.Request("ping");
            T.Eq("ping result is {}", "{}", ping?.GetProperty("result").GetRawText());

            JsonElement? list = c.Request("tools/list");
            JsonElement tools = list!.Value.GetProperty("result").GetProperty("tools");
            T.Eq("the six read-only tools, the four write tools (Phases 21-22) and the two control tools ([OWNER-Q14])", 12, tools.GetArrayLength());
            T.Eq("tool names in catalogue order", "cadence_get_status,cadence_list_jobs,cadence_get_job,cadence_get_run_output,cadence_preview_schedule,cadence_list_templates,cadence_save_job,cadence_set_job_enabled,cadence_run_job_now,cadence_delete_job,cadence_suspend_agents,cadence_resume_agents",
                string.Join(",", tools.EnumerateArray().Select(t => t.GetProperty("name").GetString())));
            JsonElement t0 = tools[0];
            T.Eq("tool name", "cadence_get_status", t0.GetProperty("name").GetString());
            T.Eq("tool title", "Cadence status", t0.GetProperty("title").GetString());
            T.Eq("input schema type", "object", t0.GetProperty("inputSchema").GetProperty("type").GetString());
            JsonElement ann = t0.GetProperty("annotations");
            T.Eq("readOnlyHint", true, ann.GetProperty("readOnlyHint").GetBoolean());
            T.Eq("destructiveHint", false, ann.GetProperty("destructiveHint").GetBoolean());
            T.Eq("idempotentHint", true, ann.GetProperty("idempotentHint").GetBoolean());
            T.Eq("openWorldHint", false, ann.GetProperty("openWorldHint").GetBoolean());
            maxHandle = Math.Max(maxHandle, c.MainWindowHandle);

            JsonElement? call = c.Request("tools/call", "{\"name\":\"cadence_get_status\",\"arguments\":{}}");
            JsonElement cr = call!.Value.GetProperty("result");
            T.Eq("get_status isError", false, cr.GetProperty("isError").GetBoolean());
            JsonElement s = cr.GetProperty("structuredContent");
            T.Eq("folder", "\\CadenceTest", s.GetProperty("folder").GetString());
            T.Eq("you", "Cadence Test Agent", s.GetProperty("you").GetString());
            T.Eq("protocolVersion in status", "2025-11-25", s.GetProperty("protocolVersion").GetString());
            T.True("jobs is a count", s.GetProperty("jobs").GetInt32() >= 0);
            T.True("timeZone is the PC's", s.GetProperty("timeZone").GetString() == TimeZoneInfo.Local.Id);
            bool parsed = DateTimeOffset.TryParse(s.GetProperty("now").GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset now);
            T.True("now is ISO 8601 with an offset", parsed && s.GetProperty("now").GetString()!.Length == 25);
            T.True("now is within 5 minutes of this PC's clock", parsed && Math.Abs((now - DateTimeOffset.Now).TotalMinutes) < 5);
            T.True("offset is this PC's", parsed && now.Offset == TimeZoneInfo.Local.GetUtcOffset(now));
            T.Eq("text mirrors structured", s.GetRawText(), JsonDocument.Parse(cr.GetProperty("content")[0].GetProperty("text").GetString()!).RootElement.GetRawText());
            maxHandle = Math.Max(maxHandle, c.MainWindowHandle);

            JsonElement? bad = c.Request("tools/call", "{\"name\":\"cadence_delete_all\",\"arguments\":{}}");
            T.Eq("a bulk-delete tool does not exist: -32602", -32602, bad!.Value.GetProperty("error").GetProperty("code").GetInt32());

            T.Eq("no window at any point", 0L, maxHandle);

            bool exited = c.CloseStdinAndWait(5000);
            T.True("exits within 5 s after stdin closes", exited);
            if (exited) T.Eq("exit code 0", 0, c.ExitCode);

            string[] lines = c.StdoutLines;
            T.Eq("stdout has exactly the 5 responses", 5, lines.Length);
            T.True("every stdout line is a valid JSON-RPC message", lines.All(ValidJsonRpcLine));
            T.True("stderr has the startup line (" + c.Stderr.Split('\n')[0] + ")",
                System.Text.RegularExpressions.Regex.IsMatch(c.Stderr, @"^cadence-mcp: v\S+ folder \\CadenceTest pid " + c.Pid.ToString(CultureInfo.InvariantCulture) + @" protocol 2025-11-25,2025-06-18", System.Text.RegularExpressions.RegexOptions.Multiline));
            T.True("stderr lines are all prefixed", c.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).All(l => l.StartsWith("cadence-mcp: ", StringComparison.Ordinal)));
        }
        finally { c.Kill(); }
    }

    public static void UnsupportedVersionAndHostileInputOverRealPipes()
    {
        using var c = new McpTestClient(ServerArgs);
        try
        {
            JsonElement? init = c.Request("initialize", "{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"clientInfo\":{\"name\":\"old-client\"}}");
            T.Eq("an old version gets the latest", "2025-11-25", init?.GetProperty("result").GetProperty("protocolVersion").GetString());

            c.SendRaw("{this is not json");
            JsonElement? perr = c.ReceiveLine();
            T.Eq("malformed JSON is -32700", -32700, perr?.GetProperty("error").GetProperty("code").GetInt32());
            T.True("with a null id", perr?.GetProperty("id").ValueKind == JsonValueKind.Null);

            c.SendRaw("[]");
            T.Eq("a batch is -32600", -32600, c.ReceiveLine()?.GetProperty("error").GetProperty("code").GetInt32());

            // 1 MiB + 1 byte on one line, written from a second thread so the pipe cannot deadlock; non-ASCII UTF-8 first.
            byte[] big = new byte[(1 << 20) + 1];
            Array.Fill(big, (byte)'a');
            var writer = Task.Run(() => c.SendRawBytes([.. big, (byte)'\n']));
            JsonElement? over = c.ReceiveLine(20000);
            T.True("the oversize line was written", writer.Wait(20000));
            T.Eq("an oversize line is -32600", -32600, over?.GetProperty("error").GetProperty("code").GetInt32());
            T.True("and says so", over?.GetProperty("error").GetProperty("message").GetString()!.Contains("1 MiB", StringComparison.Ordinal) == true);

            JsonElement? ping = c.Request("ping");
            T.True("the server keeps serving after all of that", ping is not null);

            JsonElement? status = c.Request("tools/call", "{\"name\":\"cadence_get_status\",\"arguments\":{\"x\":1}}");
            T.Eq("an extra argument is a tool error, not a protocol error", true, status?.GetProperty("result").GetProperty("isError").GetBoolean());

            T.True("exits on EOF", c.CloseStdinAndWait(5000));
            T.True("every stdout line is a valid JSON-RPC message", c.StdoutLines.All(ValidJsonRpcLine));
        }
        finally { c.Kill(); }
    }

    public static void NonAsciiClientNameSurvivesTheWire()
    {
        using var c = new McpTestClient(ServerArgs);
        try
        {
            string name = "Café 日本";   // "Café 日本"
            c.Request("initialize", "{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{\"name\":\"x\",\"title\":\"" + name + "\"}}");
            JsonElement? call = c.Request("tools/call", "{\"name\":\"cadence_get_status\"}");
            T.Eq("the title arrived intact as UTF-8", name, call?.GetProperty("result").GetProperty("structuredContent").GetProperty("you").GetString());
            T.True("the output line is plain ASCII", c.StdoutLines.All(l => l.All(ch => ch < 128)));
            c.CloseStdinAndWait(5000);
        }
        finally { c.Kill(); }
    }

    public static void TwoHundredSequentialPingsAreFast()
    {
        using var c = new McpTestClient(ServerArgs);
        try
        {
            c.Request("initialize", InitializeParams);
            var sw = Stopwatch.StartNew();
            int ok = 0;
            for (int i = 0; i < 200; i++) if (c.Request("ping", timeoutMs: 5000) is not null) ok++;
            sw.Stop();
            T.Eq("200 pongs", 200, ok);
            T.True("under 10 s (" + sw.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms)", sw.ElapsedMilliseconds < 10000);
            c.CloseStdinAndWait(5000);
        }
        finally { c.Kill(); }
    }

    public static void UsageErrorsGoToStderrOnly()
    {
        foreach (string[] args in new string[][] { ["--mcp", "--folder", "Microsoft"], ["--mcp", "--probe"], ["--mcp", "--yes"], ["--mcp", "somefile"], ["--probe", "--mcp"] })
        {
            using var c = new McpTestClient(args);
            try
            {
                bool exited = c.CloseStdinAndWait(10000);
                string label = string.Join(" ", args);
                T.True("'" + label + "' exits", exited);
                if (!exited) continue;
                T.Eq("'" + label + "' exit code 2", 2, c.ExitCode);
                T.Eq("'" + label + "' leaves stdout empty", 0, c.StdoutLines.Length);
                T.True("'" + label + "' explains itself on stderr (" + c.Stderr.Trim() + ")", c.Stderr.StartsWith("ERROR: ", StringComparison.Ordinal));
            }
            finally { c.Kill(); }
        }
    }

    public static void KillingTheClientSideMidSessionLeavesNoProcess()
    {
        int pid;
        using (var c = new McpTestClient(ServerArgs))
        {
            pid = c.Pid;
            c.Request("initialize", InitializeParams);
            T.True("alive mid-session", !c.HasExited);
            c.Kill();
            T.True("dead after the kill", c.HasExited);
        }
        bool alive;
        try { using Process p = Process.GetProcessById(pid); alive = !p.HasExited; }
        catch (ArgumentException) { alive = false; }
        T.True("no process with that pid remains", !alive);
    }

    public static void ClosingStdinWithoutInitializeExitsCleanly()
    {
        using var c = new McpTestClient(ServerArgs);
        try
        {
            T.True("exits on EOF", c.CloseStdinAndWait(5000));
            T.Eq("exit code 0", 0, c.ExitCode);
            T.Eq("stdout empty", 0, c.StdoutLines.Length);
        }
        finally { c.Kill(); }
    }

    public static void ZzNothingAppearedAndNoStrayProcess()
    {
        Footprint after = Snapshot();
        T.True("\\Cadence, %LOCALAPPDATA%\\Cadence and HKCU\\Software\\Cadence are unchanged", _before is not null && after == _before);
        string agents = @"Software\CadenceTest\Agents";
        using (RegistryKey? k = Registry.CurrentUser.OpenSubKey(agents)) T.True("the server did not create HKCU\\" + agents, k is null);
        T.Eq("spawned servers in this run", true, McpTestClient.TrackedPids.Length >= 8);
        T.True("none of the spawned servers is alive", !McpTestClient.AnyTrackedAlive(), string.Join("; ", McpTestClient.AliveDescriptions()));
    }
}
