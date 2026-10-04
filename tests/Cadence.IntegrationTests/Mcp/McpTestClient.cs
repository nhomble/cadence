using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Cadence.IntegrationTests.Mcp;

/// <summary>
/// A minimal MCP client for tests (PLAN 12.8 rule 8): it spawns the REAL Cadence.exe --mcp and talks to it over redirected pipes. It uses
/// only System.Text.Json and System.Diagnostics.Process, never the server's own protocol types, so a bug shared by both sides cannot pass.
/// Writes explicit UTF-8 bytes without a BOM to stdin (a default .NET Process would use the console code page and garble non-ASCII, S4),
/// reads stdout and stderr on separate threads, tracks every PID it ever started, and kills the child on Dispose.
/// </summary>
internal sealed class McpTestClient : IDisposable
{
    static readonly object TrackLock = new();
    static readonly List<(int Pid, DateTime Start)> Tracked = [];

    /// <summary>Every PID started by any client in this run (for the final "no stray process" check).</summary>
    public static int[] TrackedPids { get { lock (TrackLock) return [.. Tracked.Select(t => t.Pid)]; } }

    /// <summary>
    /// True when a process that a client started is still running. Windows recycles process ids quickly, so a tracked id only counts when the process that has it now
    /// STARTED at the recorded time (an unrelated newer process with the same id is not "ours").
    /// </summary>
    public static bool AnyTrackedAlive() => AliveDescriptions().Count > 0;

    /// <summary>"pid name (started ...)" of every tracked process that is still running (empty when none); for a failure message.</summary>
    public static List<string> AliveDescriptions()
    {
        var alive = new List<string>();
        (int Pid, DateTime Start)[] all;
        lock (TrackLock) all = [.. Tracked];
        foreach ((int pid, DateTime start) in all)
        {
            try { using Process p = Process.GetProcessById(pid); if (!p.HasExited && (start == default || p.StartTime == start)) alive.Add(pid + " " + p.ProcessName + " (started " + p.StartTime.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + ")"); }
            catch (ArgumentException) { /* gone */ }
            catch (InvalidOperationException) { /* gone */ }
            catch (System.ComponentModel.Win32Exception) { /* cannot be inspected: not ours */ }
        }
        return alive;
    }

    /// <summary>The server under test: env CADENCE_MCP_EXE, else the build output of Cadence.App, else bin\Cadence.exe.</summary>
    public static string ExePath()
    {
        string? env = Environment.GetEnvironmentVariable("CADENCE_MCP_EXE");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Cadence.slnx"))) dir = Path.GetDirectoryName(dir);
        if (dir is null) throw new InvalidOperationException("Cannot find the repository root from " + AppContext.BaseDirectory);
        string built = Path.Combine(dir, "obj", "artifacts", "bin", "Cadence.App", "release_win-x64", "Cadence.exe");
        if (File.Exists(built)) return built;
        string published = Path.Combine(dir, "bin", "Cadence.exe");
        if (File.Exists(published)) return published;
        throw new FileNotFoundException("Cadence.exe was not found (looked for " + built + " and " + published + "); build first or set CADENCE_MCP_EXE.");
    }

    readonly Process _process;
    readonly BlockingCollection<string> _stdout = [];
    readonly object _gate = new();
    readonly List<string> _stdoutAll = [];
    readonly StringBuilder _stderr = new();
    readonly Thread _outThread;
    readonly Thread _errThread;
    long _nextId = 1;

    public McpTestClient(IEnumerable<string> args, IDictionary<string, string?>? extraEnv = null)
    {
        var psi = new ProcessStartInfo(ExePath())
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = false,   // the point: a GUI-subsystem server must not show a window either way
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (string a in args) psi.ArgumentList.Add(a);

        // A missing runtime would otherwise pop a modal dialog and hang forever (spike S7).
        psi.Environment["DOTNET_DISABLE_GUI_ERRORS"] = "1";
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_ROOT")))
        {
            string perUser = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "dotnet");
            if (Directory.Exists(perUser)) psi.Environment["DOTNET_ROOT"] = perUser;
        }
        if (extraEnv is not null)
            foreach (KeyValuePair<string, string?> kv in extraEnv)
            {
                if (kv.Value is null) psi.Environment.Remove(kv.Key); else psi.Environment[kv.Key] = kv.Value;
            }
        Env = new Dictionary<string, string?>(psi.Environment, StringComparer.OrdinalIgnoreCase);

        _process = new Process { StartInfo = psi };
        _process.Start();
        DateTime started = default;
        try { started = _process.StartTime; } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { /* already gone */ }
        lock (TrackLock) Tracked.Add((_process.Id, started));
        Pid = _process.Id;

        _outThread = new Thread(() =>
        {
            try { string? l; while ((l = _process.StandardOutput.ReadLine()) is not null) { lock (_gate) _stdoutAll.Add(l); _stdout.Add(l); } }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
            finally { _stdout.CompleteAdding(); }
        }) { IsBackground = true };
        _errThread = new Thread(() =>
        {
            try { string? l; while ((l = _process.StandardError.ReadLine()) is not null) lock (_gate) _stderr.Append(l).Append('\n'); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        }) { IsBackground = true };
        _outThread.Start();
        _errThread.Start();
    }

    public int Pid { get; }

    /// <summary>The environment the child was started with.</summary>
    public IReadOnlyDictionary<string, string?> Env { get; }

    public bool HasExited => _process.HasExited;

    public int ExitCode => _process.ExitCode;

    /// <summary>Every raw stdout line received so far.</summary>
    public string[] StdoutLines { get { lock (_gate) return [.. _stdoutAll]; } }

    public string Stderr { get { lock (_gate) return _stderr.ToString(); } }

    /// <summary>The window handle of the child (0 = no window).</summary>
    public long MainWindowHandle
    {
        get
        {
            try { _process.Refresh(); return _process.MainWindowHandle.ToInt64(); }
            catch (InvalidOperationException) { return 0; }
        }
    }

    /// <summary>Writes one line as UTF-8 bytes (no BOM) and flushes.</summary>
    public void SendRaw(string line)
    {
        byte[] bytes = new UTF8Encoding(false).GetBytes(line + "\n");
        Stream s = _process.StandardInput.BaseStream;
        s.Write(bytes, 0, bytes.Length);
        s.Flush();
    }

    public void SendRawBytes(byte[] bytes)
    {
        Stream s = _process.StandardInput.BaseStream;
        s.Write(bytes, 0, bytes.Length);
        s.Flush();
    }

    public void Notify(string method, string? paramsJson = null) =>
        SendRaw("{\"jsonrpc\":\"2.0\",\"method\":\"" + method + "\"" + (paramsJson is null ? "" : ",\"params\":" + paramsJson) + "}");

    /// <summary>The next stdout line parsed as JSON; null on timeout or end of output.</summary>
    public JsonElement? ReceiveLine(int timeoutMs = 10000)
    {
        if (!_stdout.TryTake(out string? line, timeoutMs)) return null;
        return JsonDocument.Parse(line).RootElement.Clone();
    }

    /// <summary>Sends a request with a fresh integer id and returns the response with that id (null on timeout).</summary>
    public JsonElement? Request(string method, string? paramsJson = null, int timeoutMs = 10000)
    {
        long id = _nextId++;
        SendRaw("{\"jsonrpc\":\"2.0\",\"id\":" + id.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"method\":\"" + method + "\"" + (paramsJson is null ? "" : ",\"params\":" + paramsJson) + "}");
        DateTime end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            int left = (int)(end - DateTime.UtcNow).TotalMilliseconds;
            if (left <= 0) return null;
            JsonElement? r = ReceiveLine(left);
            if (r is null) return null;
            if (r.Value.TryGetProperty("id", out JsonElement rid) && rid.ValueKind == JsonValueKind.Number && rid.GetInt64() == id) return r;
        }
    }

    /// <summary>Closes the child's stdin (the MCP shutdown signal) and waits for it to exit; true when it did within the time.</summary>
    public bool CloseStdinAndWait(int timeoutMs)
    {
        try { _process.StandardInput.Close(); }
        catch (IOException) { }
        bool exited = _process.WaitForExit(timeoutMs);
        if (exited) { _outThread.Join(2000); _errThread.Join(2000); }
        return exited;
    }

    public void Kill()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        try { _process.WaitForExit(5000); } catch (InvalidOperationException) { }
    }

    public void Dispose()
    {
        Kill();
        _process.Dispose();
    }
}
