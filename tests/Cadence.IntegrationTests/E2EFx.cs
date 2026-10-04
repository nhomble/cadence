using System.Diagnostics;
using System.Text;
using Cadence.Core.History;
using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.History;
using Cadence.Scheduler;
using Cadence.Scheduler.Apply;
using Cadence.TestHarness;

namespace Cadence.IntegrationTests;

/// <summary>A process found by <see cref="Janitor.Find"/>. <c>StartedUtc</c> is null when Windows did not report it.</summary>
internal sealed record ProcInfo(int Pid, int ParentPid, string CommandLine, DateTime? StartedUtc = null);

/// <summary>
/// Finds and kills the processes the end-to-end tests may leave behind: the wrapper's own processes (their command line holds
/// <c>$tp='\CadenceTest\...</c>), anything running a file under %LOCALAPPDATA%\CadenceTest\work, and the marked <c>ping</c> children the
/// tests start on purpose (<c>ping -n 9|71|72 127.0.0.1</c>).
/// [Review R] A command-line match alone is NOT enough to kill: <c>ping -n 9 127.0.0.1</c> is a common "sleep" idiom in the owner's own batch
/// files. <see cref="KillMarked"/> only kills a match that started after this test run began AND either is a wrapper process itself
/// (<c>$tp='\CadenceTest\</c>), descends from one, or is an orphan whose parent is gone (what the timeout and spawn tests leave behind).
/// </summary>
internal static class Janitor
{
    /// <summary>The counts the tests give ping (unique per test so a leftover can be attributed): overlap 9, timeout 71, spawn 72.</summary>
    public const string Pattern = @"\$tp='\\CadenceTest\\|CadenceTest\\work|ping\s+-n\s+(9|71|72)\s+127\.0\.0\.1";

    const string WrapperMarker = @"\$tp='\\CadenceTest\\";

    /// <summary>When this test process started: nothing older can be one of its leftovers.</summary>
    static readonly DateTime RunStartUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime();

    public static List<ProcInfo> Find(string regex)
    {
        // The query travels as -EncodedCommand, so its own command line (base64) can never match the pattern.
        string script = "Get-CimInstance Win32_Process | Where-Object { $_.ProcessId -ne $PID -and $_.CommandLine -match '" + regex.Replace("'", "''", StringComparison.Ordinal)
            + "' } | ForEach-Object { [string]$_.ProcessId + [char]9 + [string]$_.ParentProcessId + [char]9 + $(if ($_.CreationDate) { $_.CreationDate.ToUniversalTime().ToString('o') } else { '' }) + [char]9 + $_.CommandLine }";
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-EncodedCommand");
        psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        using Process p = Process.Start(psi)!;
        Task<string> err = p.StandardError.ReadToEndAsync();
        string text = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        err.Wait();
        var list = new List<ProcInfo>();
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] f = line.TrimEnd('\r').Split('\t', 4);
            if (f.Length == 4 && int.TryParse(f[0], out int pid) && int.TryParse(f[1], out int ppid))
            {
                DateTime? started = DateTime.TryParse(f[2], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime s) ? s.ToUniversalTime() : null;
                list.Add(new ProcInfo(pid, ppid, f[3], started));
            }
        }
        return list;
    }

    /// <summary>
    /// [Review R] Whether a process that matched <see cref="Pattern"/> is provably one of this run's leftovers: it started after the run began
    /// (unknown start time: no), and it is a wrapper itself, has a wrapper among its matched ancestors, or its parent is gone (an orphan).
    /// A match whose parent is alive and is not a wrapper (the owner's batch file running <c>ping -n 9 127.0.0.1</c>) is never killed.
    /// </summary>
    internal static bool IsOurs(ProcInfo p, IReadOnlyList<ProcInfo> matched, Func<int, bool> isAlive, DateTime runStartUtc)
    {
        if (p.StartedUtc is not { } started || started < runStartUtc) return false;
        if (System.Text.RegularExpressions.Regex.IsMatch(p.CommandLine, WrapperMarker)) return true;
        ProcInfo cur = p;
        for (int depth = 0; depth < 4; depth++)
        {
            ProcInfo? parent = matched.FirstOrDefault(m => m.Pid == cur.ParentPid);
            if (parent is null) return !isAlive(cur.ParentPid);   // orphan: the wrapper (or its cmd) that started it is gone
            if (System.Text.RegularExpressions.Regex.IsMatch(parent.CommandLine, WrapperMarker)) return true;
            cur = parent;
        }
        return false;
    }

    /// <summary>Kills every process <see cref="Find"/> reports for <see cref="Pattern"/> that <see cref="IsOurs"/> accepts (with its children) and returns what it killed.</summary>
    public static List<ProcInfo> KillMarked()
    {
        List<ProcInfo> matched = Find(Pattern);
        List<ProcInfo> found = matched.Where(p => IsOurs(p, matched, IsAlive, RunStartUtc)).ToList();
        foreach (ProcInfo skipped in matched.Except(found)) Fx.Info("janitor left alone (not provably a leftover of this run): " + skipped.Pid + " " + E2E.Short(skipped.CommandLine));
        foreach (ProcInfo p in found)
        {
            try
            {
                using Process k = Process.Start(new ProcessStartInfo("taskkill.exe", "/PID " + p.Pid + " /T /F")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
                k.StandardOutput.ReadToEnd();
                k.StandardError.ReadToEnd();
                k.WaitForExit();
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { /* already gone */ }
        }
        return found;
    }

    public static bool IsAlive(int pid)
    {
        try { using Process p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}

/// <summary>
/// One end-to-end scenario's world: an empty \CadenceTest, the real Applier/gateway, and the wrapper's runs root pointed at
/// %LOCALAPPDATA%\CadenceTest\runs (the composed wrapper's <c>$rr</c> is "CadenceTest", never "Cadence"). Everything is removed afterwards.
/// </summary>
internal sealed class E2E
{
    public TaskSchedulerGateway Gw { get; }
    public ManagedJobs Jobs { get; }
    public Applier Applier { get; }
    public string Work { get; }
    public RunRecordStore Store { get; }
    public HistoryService History { get; }

    E2E(TaskSchedulerGateway gw, ManagedJobs jobs, Applier applier)
    {
        Gw = gw;
        Jobs = jobs;
        Applier = applier;
        Work = Path.Combine(ApplyFx.LocalDataDir, "work");
        Directory.CreateDirectory(Work);
        Store = new RunRecordStore(Path.Combine(ApplyFx.LocalDataDir, "runs"));
        History = new HistoryService(Store);
    }

    /// <summary>Runs <paramref name="body"/> on an empty \CadenceTest; the janitor kills marked leftovers (also when the body throws).</summary>
    public static void Scenario(Action<E2E> body, bool spawnsProcesses = false) => ApplyFx.Isolated(() =>
    {
        var (gw, jobs, applier) = ApplyFx.Stack();
        if (gw.Options.RunsRootName != TestFolder.Name) throw new InvalidOperationException("The runs root must be CadenceTest.");
        var env = new E2E(gw, jobs, applier);
        try { body(env); }
        finally
        {
            if (spawnsProcesses)
            {
                List<ProcInfo> killed = Janitor.KillMarked();
                if (killed.Count > 0) Fx.Info("janitor killed " + killed.Count + " leftover process(es): " + string.Join("; ", killed.Select(k => k.Pid + " " + Short(k.CommandLine))));
            }
        }
    });

    public static string Short(string s) => s.Length <= 90 ? s : s[..90] + "...";

    /// <summary>A job that cannot fire in this century and is enabled (so Run() works). Hidden by default.</summary>
    public static JobDefinition Job(string name, string exe, string args, Func<RunSpec, RunSpec>? run = null)
    {
        JobDefinition j = ApplyFx.RunnableJob(name, exe, args);
        return run is null ? j : j with { Run = run(j.Run) };
    }

    /// <summary>Saves the job through the real Applier and returns the classified task (InSync is asserted).</summary>
    public ClassifiedTask Save(JobDefinition job)
    {
        ApplyResult r = Applier.Save(job, null);
        T.True("save '" + job.Name + "': " + r.Message, r.Success);
        ClassifiedTask c = Jobs.Load(false).Single(x => x.Job?.Id == job.Id);
        T.Eq("saved job is InSync", SyncStatus.InSync, c.Sync);
        return c;
    }

    public IReadOnlyList<RunRecord> Records(ClassifiedTask c) => Store.ReadTail(c.Job!.Id, c.Observed.Path, 5000);

    /// <summary>Run() the task and wait until the wrapper has written <paramref name="count"/> records (it writes in `finally`, so a run in progress has none).</summary>
    public IReadOnlyList<RunRecord> RunAndWait(ClassifiedTask c, int count, TimeSpan timeout, string label)
    {
        Gw.Run(c.Observed.Name);
        return WaitRecords(c, count, timeout, label);
    }

    public IReadOnlyList<RunRecord> WaitRecords(ClassifiedTask c, int count, TimeSpan timeout, string label)
    {
        bool ok = Fx.WaitFor(() => Records(c).Count >= count, timeout);
        T.True(label + ": " + count + " run record(s) within " + (int)timeout.TotalSeconds + " s", ok, "got " + Records(c).Count);
        if (!ok)
        {
            RegisteredTaskInfo? i = Gw.Get(c.Observed.Name);
            Fx.Info(label + ": task state " + i?.State + ", last run " + Fx.Iso(i?.LastRunTime) + ", last result 0x" + (i?.LastTaskResult ?? 0).ToString("X", System.Globalization.CultureInfo.InvariantCulture)
                + "; wrapper processes: " + string.Join("; ", Janitor.Find(@"\$tp='\\CadenceTest\\").Select(p => p.Pid + " " + Short(p.CommandLine))));
            if (i?.LastTaskResult == unchecked((int)0x80070005))
                Fx.Info(label + ": 0x80070005 and no wrapper process: the launch was refused, not a wrapper bug. On this machine Windows Defender reports it as a detection of the wrapper command line (Get-MpThreatDetection: Trojan:Win32/Commando.A!ml).");
        }
        return Records(c);
    }

    public const int AccessDenied = unchecked((int)0x80070005);

    /// <summary>
    /// As <see cref="RunAndWait"/>, except that it returns null (and checks nothing) when Windows REFUSED to start the task's action:
    /// Task Scheduler reports 0x80070005 for this Run(), the task is not running, and for 3 s more there is no run record, no
    /// <c>_unknown</c> record and no wrapper process. That is the known Windows Defender false positive (README Safety notes) (Windows Defender blocks some wrapper
    /// command lines); the caller reports it with <see cref="T.KnownIssue"/>. Any other outcome, including a record that never comes for
    /// another reason, goes through the normal, failing checks of <see cref="WaitRecords"/>.
    /// </summary>
    public IReadOnlyList<RunRecord>? RunAndWaitUnlessBlocked(ClassifiedTask c, int count, TimeSpan timeout, string label, Action? start = null)
    {
        string name = c.Observed.Name;
        DateTime? lastBefore = Gw.Get(name)?.LastRunTime;
        int unknownBefore = Store.ReadTail(RunRecordStore.UnknownFolder).Count;
        DateTime end = DateTime.UtcNow + timeout;
        if (start is null) Gw.Run(name); else start();   // `start` lets a test begin the run through another door (the CLI) with the same detection
        bool refused = false;
        Fx.WaitFor(() =>
        {
            if (Records(c).Count >= count) return true;
            refused = Gw.Get(name) is { State: not TaskState.Running, LastTaskResult: AccessDenied } i && i.LastRunTime != lastBefore;
            return refused;
        }, timeout);
        if (refused)
        {
            Thread.Sleep(TimeSpan.FromSeconds(3));   // a wrapper that did start writes its record within 1-2 s
            bool stillNothing = Records(c).Count < count
                && Store.ReadTail(RunRecordStore.UnknownFolder).Count == unknownBefore
                && Janitor.Find(@"\$tp='\\CadenceTest\\").Count == 0
                && Gw.Get(name) is { State: not TaskState.Running, LastTaskResult: AccessDenied };
            if (stillNothing)
            {
                Fx.Info(label + ": Task Scheduler reports 0x80070005 and no wrapper ran (launch refused by Windows; see the README Safety notes).");
                return null;
            }
        }
        TimeSpan left = end - DateTime.UtcNow;
        return WaitRecords(c, count, left > TimeSpan.Zero ? left : TimeSpan.Zero, label);
    }

    /// <summary>Waits for the previous instance to end (so IgnoreNew cannot drop this Run()), then as <see cref="RunAndWait"/>.</summary>
    public IReadOnlyList<RunRecord> RunAndWaitAfterIdle(ClassifiedTask c, int count, TimeSpan timeout, string label)
    {
        WaitIdle(c.Observed.Name, TimeSpan.FromSeconds(30));
        return RunAndWait(c, count, timeout, label);
    }

    public bool WaitIdle(string taskName, TimeSpan timeout) =>
        Fx.WaitFor(() => Gw.Get(taskName) is { State: not TaskState.Running }, timeout);

    public ClassifiedTask Fresh(ClassifiedTask c) => Jobs.Classify(Gw.Get(c.Observed.Name)!);

    /// <summary>The local date the wrapper will use (it asks the clock itself; a run straddling midnight is the accepted, unlikely race).</summary>
    public static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);
}
