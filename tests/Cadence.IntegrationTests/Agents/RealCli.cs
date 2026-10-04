using System.Diagnostics;
using System.Text;
using Cadence.IntegrationTests.Mcp;

namespace Cadence.IntegrationTests.Agents;

/// <summary>
/// Runs the REAL <c>Cadence.exe</c> with command-line switches (a GUI-subsystem exe: only a <see cref="Process"/> with redirected pipes captures its
/// output) and tracks every PID it starts, so the final check can prove nothing was left behind. Only ever used with <c>--folder CadenceTest</c>.
/// </summary>
internal static class RealCli
{
    static readonly object Gate = new();
    static readonly List<(int Pid, DateTime Start)> Pids = [];

    public sealed record Result(int Exit, string Stdout, string Stderr);

    public static int[] TrackedPids { get { lock (Gate) return [.. Pids.Select(t => t.Pid)]; } }

    /// <summary>True when a process started here is still running (a recycled process id only counts when the process started at the recorded time).</summary>
    public static bool AnyAlive()
    {
        (int Pid, DateTime Start)[] all;
        lock (Gate) all = [.. Pids];
        foreach ((int pid, DateTime start) in all)
        {
            try { using Process p = Process.GetProcessById(pid); if (!p.HasExited && (start == default || p.StartTime == start)) return true; }
            catch (ArgumentException) { /* gone */ }
            catch (InvalidOperationException) { /* gone */ }
            catch (System.ComponentModel.Win32Exception) { /* cannot be inspected: not ours */ }
        }
        return false;
    }

    /// <param name="env">Extra environment variables for the child (a null value removes the variable).</param>
    public static Result Run(string[] args, IDictionary<string, string?>? env = null, int timeoutSeconds = 90)
    {
        if (!args.Contains("CadenceTest", StringComparer.Ordinal)) throw new InvalidOperationException("RealCli runs only against --folder CadenceTest.");
        var psi = new ProcessStartInfo(McpTestClient.ExePath())
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_DISABLE_GUI_ERRORS"] = "1";
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_ROOT")))
        {
            string perUser = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "dotnet");
            if (Directory.Exists(perUser)) psi.Environment["DOTNET_ROOT"] = perUser;
        }
        if (env is not null)
            foreach (KeyValuePair<string, string?> kv in env)
            {
                if (kv.Value is null) psi.Environment.Remove(kv.Key); else psi.Environment[kv.Key] = kv.Value;
            }

        using Process p = Process.Start(psi) ?? throw new InvalidOperationException("could not start Cadence.exe");
        DateTime started = default;
        try { started = p.StartTime; } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { /* already gone */ }
        lock (Gate) Pids.Add((p.Id, started));
        p.StandardInput.Close();
        Task<string> o = p.StandardOutput.ReadToEndAsync();
        Task<string> e = p.StandardError.ReadToEndAsync();
        try
        {
            if (!p.WaitForExit(TimeSpan.FromSeconds(timeoutSeconds)))
            {
                Kill(p);
                throw new TimeoutException("Cadence.exe " + string.Join(' ', args) + " did not finish within " + timeoutSeconds + " s");
            }
            return new Result(p.ExitCode, o.GetAwaiter().GetResult(), e.GetAwaiter().GetResult());
        }
        finally { Kill(p); }
    }

    static void Kill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); p.WaitForExit(10000); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { /* already gone */ }
    }
}
