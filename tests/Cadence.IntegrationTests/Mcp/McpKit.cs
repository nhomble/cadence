using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Cadence.IntegrationTests.Agents;
using Cadence.TestHarness;

namespace Cadence.IntegrationTests.Mcp;

/// <summary>The answer of one tools/call: isError, the structuredContent (default when none), the text and the raw JSON-RPC line.</summary>
internal sealed record McpReply(bool IsError, JsonElement Structured, string Text, string Line, int? RpcCode = null)
{
    /// <summary>The tool error code, or "rpc-32602" style text when the server answered with a JSON-RPC error instead of a tool result.</summary>
    public string Code => RpcCode is { } rpc ? "rpc" + rpc.ToString(CultureInfo.InvariantCulture) : Structured.GetProperty("error").GetProperty("code").GetString()!;

    public string Message => Structured.GetProperty("error").GetProperty("message").GetString()!;

    public string Str(params string[] path)
    {
        JsonElement e = Structured;
        foreach (string p in path) e = e.GetProperty(p);
        return e.GetString()!;
    }
}

/// <summary>
/// Shared pieces of the Phase 22 integration tests: a real <c>Cadence.exe --mcp --folder CadenceTest</c> client, request builders, and a helper process that holds the
/// writer lock of the TEST folder (a PowerShell child, started without a window, tracked and killed by the test; the abandoned-mutex behaviour of PLAN 12.1.3 needs a
/// holder that is a different PROCESS and that can be killed).
/// </summary>
internal static class McpKit
{
    public const string Folder = "CadenceTest";
    public const string Cmd = "C:\\\\Windows\\\\System32\\\\cmd.exe";
    public const string DefaultTitle = "Cadence Test Agent";

    public static string InitializeParams(string title = DefaultTitle) =>
        "{\"protocolVersion\":\"2025-11-25\",\"capabilities\":{},\"clientInfo\":{\"name\":\"cadence-tests\",\"title\":" + JsonSerializer.Serialize(title) + ",\"version\":\"1\"}}";

    /// <summary>A started and initialized server.</summary>
    public static McpTestClient Start(string title = DefaultTitle)
    {
        var c = new McpTestClient(["--mcp", "--folder", Folder]);
        try
        {
            if (c.Request("initialize", InitializeParams(title)) is null) throw new InvalidOperationException("the server did not answer initialize");
            c.Notify("notifications/initialized");
            return c;
        }
        catch { c.Dispose(); throw; }
    }

    public static McpReply Call(McpTestClient c, string tool, string arguments, int timeoutMs = 60000)
    {
        JsonElement? r = c.Request("tools/call", "{\"name\":\"" + tool + "\",\"arguments\":" + arguments + "}", timeoutMs);
        if (r is null) throw new InvalidOperationException(tool + ": no answer from the server for " + arguments + (c.HasExited ? " (the server exited with " + c.ExitCode.ToString(CultureInfo.InvariantCulture) + ")" : " (still running)") + "; stderr: " + c.Stderr);
        return Parse(tool, r);
    }

    public static McpReply Parse(string what, JsonElement? r)
    {
        if (r is null) throw new InvalidOperationException(what + ": no answer from the server");
        if (r.Value.TryGetProperty("error", out JsonElement rpcError))
            return new McpReply(true, default, rpcError.GetProperty("message").GetString() ?? "", r.Value.GetRawText(), rpcError.GetProperty("code").GetInt32());
        JsonElement res = r.Value.GetProperty("result");
        string text = res.GetProperty("content")[0].GetProperty("text").GetString()!;
        return new McpReply(res.GetProperty("isError").GetBoolean(), res.TryGetProperty("structuredContent", out JsonElement s) ? s.Clone() : default, text, r.Value.GetRawText());
    }

    /// <summary>A tools/call that the server answered with a JSON-RPC error (an unknown tool): the error code, or null for a result.</summary>
    public static int? RpcErrorCode(McpTestClient c, string tool) =>
        c.Request("tools/call", "{\"name\":\"" + tool + "\",\"arguments\":{}}") is { } r && r.TryGetProperty("error", out JsonElement e) ? e.GetProperty("code").GetInt32() : null;

    public static string Q(string s) => JsonSerializer.Serialize(s);

    public static string Id(string id) => "{\"jobId\":" + Q(id) + "}";

    public static string Enable(string id, bool enabled, string? revision = null) =>
        "{\"jobId\":" + Q(id) + ",\"enabled\":" + (enabled ? "true" : "false") + (revision is null ? "" : ",\"expectedRevision\":\"" + revision + "\"") + "}";

    public static string Delete(string id, string name) => "{\"jobId\":" + Q(id) + ",\"confirmName\":" + Q(name) + "}";

    public static string Create(string name, string cron, string arguments = "/d /c \\\"echo agent-ok\\\"", string extraProgram = "", string extraTop = "") =>
        "{\"name\":" + Q(name) + ",\"schedule\":{\"cron\":[" + Q(cron) + "]},\"program\":{\"path\":\"" + Cmd + "\",\"arguments\":\"" + arguments + "\"" + extraProgram + "}" + extraTop + "}";

    public static string Update(string id, string revision, string body) => "{\"jobId\":" + Q(id) + ",\"expectedRevision\":\"" + revision + "\"," + body + "}";

    /// <summary>"m h D M *" for yesterday: once a year, next year, so it cannot fire during a test.</summary>
    public static string Yesterday(int minuteOffset = 0)
    {
        DateTime y = DateTime.Now.AddDays(-1);
        return string.Create(CultureInfo.InvariantCulture, $"{(y.Minute + minuteOffset) % 60} {y.Hour} {y.Day} {y.Month} *");
    }

    public static string Revision(McpTestClient c, string id) => Call(c, "cadence_get_job", Id(id)).Str("revision");

    /// <summary>Hash of the whole managed folder (every task name and XML).</summary>
    public static string FolderHash(E2E e) =>
        ApplyFx.Sha(string.Join("\n", e.Gw.ListManagedFolder().OrderBy(t => t.Name, StringComparer.Ordinal).Select(t => t.Name + "|" + t.Xml)));

    public static string TaskHash(E2E e, string taskName) => ApplyFx.Sha(e.Gw.Get(taskName)?.Xml ?? "(gone)");

    public static string[] CliArgs(params string[] args) => [.. args, "--folder", Folder];

    public static bool ProductKeyExists() { using Microsoft.Win32.RegistryKey? k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Cadence"); return k is not null; }

    /// <summary>The standard wrapper of a scenario: clean agent state of the test folder before and after, jobs deleted, the product's registry key untouched.</summary>
    public static void Scenario(Action<E2E> body) => E2E.Scenario(e =>
    {
        TestFolder.CleanAgentState();
        bool productKeyBefore = ProductKeyExists();
        try { body(e); }
        finally
        {
            TestFolder.CleanAgentState();
        }
        T.Eq("the product's agent key was never created or removed", productKeyBefore, ProductKeyExists());
    }, spawnsProcesses: true);

    /// <summary>Closes the session, asserts a clean exit and that every stdout line was a JSON-RPC message with the version field.</summary>
    public static void Finish(McpTestClient c, string label)
    {
        bool exited = c.CloseStdinAndWait(15000);
        T.True(label + ": the server exits when stdin closes", exited);
        if (exited) T.Eq(label + ": exit code 0", 0, c.ExitCode);
        T.True(label + ": every stdout line is a JSON-RPC message", c.StdoutLines.All(l => { try { return JsonDocument.Parse(l).RootElement.TryGetProperty("jsonrpc", out _); } catch (JsonException) { return false; } }));
        T.True(label + ": stderr lines are all prefixed", c.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).All(l => l.StartsWith("cadence-mcp: ", StringComparison.Ordinal)));
    }

    // ---- a process that holds the writer lock of the test folder ----

    static readonly object HelperGate = new();
    static readonly List<(int Pid, DateTime Start)> HelperPids = [];

    public static bool AnyHelperAlive()
    {
        (int Pid, DateTime Start)[] all;
        lock (HelperGate) all = [.. HelperPids];
        foreach ((int pid, DateTime start) in all)
        {
            try { using Process p = Process.GetProcessById(pid); if (!p.HasExited && (start == default || p.StartTime == start)) return true; }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
        return false;
    }

    /// <summary>
    /// A separate process (Windows PowerShell, no window) that takes the named mutex <c>Local\CadenceWrite.CadenceTest</c> and keeps it until killed. Killing it leaves the mutex
    /// ABANDONED, which is exactly the "a writer was killed mid-write" case of PLAN 12.1.3: the next writer must proceed at once.
    /// </summary>
    internal sealed class LockHolderProcess : IDisposable
    {
        readonly Process _p;

        public LockHolderProcess()
        {
            string ps = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var psi = new ProcessStartInfo(ps)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                StandardOutputEncoding = new UTF8Encoding(false),
            };
            foreach (string a in new[]
            {
                "-NoProfile", "-NonInteractive", "-Command",
                "$m = New-Object System.Threading.Mutex($false, 'Local\\CadenceWrite." + Folder + "'); [void]$m.WaitOne(); [Console]::Out.WriteLine('held'); [Console]::Out.Flush(); Start-Sleep -Seconds 300",
            }) psi.ArgumentList.Add(a);
            _p = Process.Start(psi) ?? throw new InvalidOperationException("could not start the lock holder");
            DateTime start = default;
            try { start = _p.StartTime; } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            lock (HelperGate) HelperPids.Add((_p.Id, start));
            var line = Task.Run(() => _p.StandardOutput.ReadLine());
            if (!line.Wait(30000) || line.Result != "held") { Kill(); throw new InvalidOperationException("the lock holder did not report that it holds the lock"); }
        }

        public void Kill()
        {
            try { if (!_p.HasExited) _p.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            try { _p.WaitForExit(10000); } catch (InvalidOperationException) { }
        }

        public void Dispose()
        {
            Kill();
            _p.Dispose();
        }
    }
}
