using System.Diagnostics;
using System.Globalization;
using System.Text;
using Cadence.Core.Execution;
using Cadence.Core.History;
using Cadence.Core.Holidays;
using Cadence.Core.Metadata;
using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.Core.TaskXml;
using Cadence.History;
using Cadence.Scheduler;
using Cadence.TestHarness;

namespace Cadence.IntegrationTests;

/// <summary>
/// PLAN 6.3 #8-#12 plus the [CP-A] cases: the REAL wrapper (as TaskComposer writes it) run by the REAL Task Scheduler from \CadenceTest\,
/// with its runs root at %LOCALAPPDATA%\CadenceTest\runs. Tasks are dated 2099 and started with Run() (the one scheduled case is
/// harmless: <c>cmd /c echo</c> one minute ahead). The programs are cmd, powershell and ping 127.0.0.1 only and write only below
/// %LOCALAPPDATA%\CadenceTest\work. Source files stay ASCII: non-ASCII text is written as \u escapes.
/// </summary>
public static class WrapperE2ETests
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(90);
    const string Cmd = "cmd.exe";

    // ---- #8: failure, success, captured output ----

    public static void FailingAndSucceedingJobs() => E2E.Scenario(e =>
    {
        // Exit code 3, stdout "hello", stderr "oops".
        JobDefinition failing = E2E.Job("fail three", Cmd, "/d /c \"echo hello& >&2 echo oops& exit /b 3\"");
        ClassifiedTask c = e.Save(failing);
        IReadOnlyList<RunRecord> recs = e.RunAndWait(c, 1, Wait, "failing job");
        if (recs.Count > 0)
        {
            RunRecord r = recs[^1];
            T.Eq("failing: status", RunStatus.Failed, r.Status);
            T.Eq("failing: exit code", 3, r.ExitCode);
            T.Eq("failing: task path", "\\CadenceTest\\fail three", r.TaskPath);
            T.True("failing: has pid, start and end", r.Pid is > 0 && r.Start is not null && r.End is not null);
            T.True("failing: no message", string.IsNullOrEmpty(r.Message), r.Message);
            T.Eq("failing: stdout", "hello", e.Store.ReadOutput(failing.Id, r.RunId, stderr: false)?.Trim());
            T.Eq("failing: stderr", "oops", e.Store.ReadOutput(failing.Id, r.RunId, stderr: true)?.Trim());
            T.True("failing: counts are characters of the captured text", r.OutBytes is 7 or 6 && r.ErrBytes is 6 or 5, r.OutBytes + "/" + r.ErrBytes);
            T.True("failing: program duration is short", r.Duration is { } d && d < TimeSpan.FromSeconds(10), r.Duration?.ToString());

            e.WaitIdle(c.Observed.Name, TimeSpan.FromSeconds(20));
            ClassifiedTask fresh = e.Fresh(c);
            JobHistory h = e.History.Load(fresh, 10);
            T.Eq("failing: history shows one row", 1, h.Recent.Count);
            T.Eq("failing: row status", RunStatus.Failed, h.Recent[0].Status);
            T.True("failing: row has output", h.Recent[0].HasOutput);
            T.Eq("failing: success rate 0", 0.0, h.Summary.SuccessRate ?? -1);
            T.Eq("failing: last result", RunStatus.Failed, h.Summary.LastResult);
            // Hidden jobs run behind conhost --headless, which swallows the real exit code (documented in 2.7).
            T.Eq("failing: hidden mode reports 0 to Task Scheduler (the record holds the real code)", 0, Gw(e, c).LastTaskResult);
            Fx.Info("hidden failing job: LastTaskResult=" + Gw(e, c).LastTaskResult + ", wrapper duration " + r.Duration);
        }

        JobDefinition fine = E2E.Job("fine one", Cmd, "/d /c echo all good");
        ClassifiedTask f = e.Save(fine);
        IReadOnlyList<RunRecord> ok = e.RunAndWait(f, 1, Wait, "succeeding job");
        if (ok.Count > 0)
        {
            T.Eq("success: status", RunStatus.Ok, ok[0].Status);
            T.Eq("success: exit code", 0, ok[0].ExitCode);
            T.Eq("success: stdout", "all good", e.Store.ReadOutput(fine.Id, ok[0].RunId, stderr: false)?.Trim());
            T.True("success: no stderr file", e.Store.ReadOutput(fine.Id, ok[0].RunId, stderr: true) is null);
            T.True("success: runId is the wrapper's start time", ok[0].RunId == ok[0].WrapperStart.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture), ok[0].RunId);
            JobHistory h = e.History.Load(e.Fresh(f), 10);
            T.Eq("success: success rate 1", 1.0, h.Summary.SuccessRate ?? -1);
        }
    });

    static RegisteredTaskInfo Gw(E2E e, ClassifiedTask c) => e.Gw.Get(c.Observed.Name)!;

    // ---- #8: timeout and tree kill ----

    public static void TimeoutKillsTheWholeProcessTree() => E2E.Scenario(e =>
    {
        // cmd starts ping: killing only cmd would leave ping behind, so this proves the /T of the wrapper's taskkill.
        JobDefinition job = E2E.Job("slow job", Cmd, "/d /c \"ping -n 71 127.0.0.1 >nul\"", r => r with { Timeout = TimeSpan.FromSeconds(3) });
        ClassifiedTask c = e.Save(job);
        var before = Process.GetProcessesByName("ping").Select(p => p.Id).ToHashSet();
        e.Gw.Run(c.Observed.Name);

        var started = new HashSet<int>();
        Fx.WaitFor(() =>
        {
            foreach (Process p in Process.GetProcessesByName("ping"))
            {
                using (p) if (!before.Contains(p.Id)) started.Add(p.Id);
            }
            return started.Count > 0 || e.Records(c).Count > 0;
        }, TimeSpan.FromSeconds(30));
        T.True("the ping child really started (so the kill below is not vacuous)", started.Count > 0);

        IReadOnlyList<RunRecord> recs = e.WaitRecords(c, 1, Wait, "timeout job");
        if (recs.Count == 0) return;
        RunRecord r = recs[^1];
        T.Eq("timeout: status", RunStatus.TimedOut, r.Status);
        T.True("timeout: the program ran for about the timeout", r.Duration is { } d && d >= TimeSpan.FromSeconds(2.5) && d < TimeSpan.FromSeconds(15), r.Duration?.ToString());
        Fx.Info("timeout record: exit=" + r.ExitCode + " duration=" + r.Duration + " msg=" + r.Message);
        T.True("timeout: no 'not fully captured' note (the pipes closed with the tree)", r.Message is null, r.Message);

        bool gone = Fx.WaitFor(() => started.All(pid => !Janitor.IsAlive(pid)) && (r.Pid is not { } cp || !Janitor.IsAlive(cp)), TimeSpan.FromSeconds(10));
        T.True("timeout: the ping child and the cmd process are really gone", gone,
            "alive: " + string.Join(",", started.Where(Janitor.IsAlive)));
        T.Eq("timeout: no process left with the marked command line", 0, Janitor.Find(@"ping -n 71 127\.0\.0\.1").Count);

        JobHistory h = e.History.Load(e.Fresh(c), 5);
        T.Eq("timeout: history row", RunStatus.TimedOut, h.Recent[0].Status);
        T.Eq("timeout: success rate 0", 0.0, h.Summary.SuccessRate ?? -1);
        T.True("timeout: average runtime ignores the timeout", h.Summary.AverageRuntime is null);
    }, spawnsProcesses: true);

    // ---- #8: program not found ----

    public static void MissingProgramIsALaunchError() => E2E.Scenario(e =>
    {
        JobDefinition job = E2E.Job("no such program", @"C:\CadenceTest-does-not-exist\nope.exe", "");
        ClassifiedTask c = e.Save(job);
        IReadOnlyList<RunRecord> recs = e.RunAndWait(c, 1, Wait, "missing exe");
        if (recs.Count == 0) return;
        RunRecord r = recs[^1];
        T.Eq("missing exe: status", RunStatus.LaunchError, r.Status);
        T.True("missing exe: message is the launch error", !string.IsNullOrEmpty(r.Message) && r.Message.Contains("cannot find the file", StringComparison.OrdinalIgnoreCase), r.Message);
        T.True("missing exe: no pid, no exit code", r.Pid is null && r.ExitCode is null);
        T.True("missing exe: no duration", r.Duration is null);
        T.True("missing exe: the record is filed under the job, not _unknown", e.Store.ReadTail(job.Id).Count == 1 && e.Store.ReadTail(RunRecordStore.UnknownFolder).Count == 0);
        JobHistory h = e.History.Load(e.Fresh(c), 5);
        T.Eq("missing exe: row", RunStatus.LaunchError, h.Recent[0].Status);
        T.Eq("missing exe: counts as a failure", 0.0, h.Summary.SuccessRate ?? -1);
    });

    // ---- working directory and environment ----

    public static void WorkingDirectoryAndEnvironmentAreApplied() => E2E.Scenario(e =>
    {
        string cwd = Path.Combine(e.Work, "cwd dir");
        Directory.CreateDirectory(cwd);
        string ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        JobDefinition job = E2E.Job("cwd and env", ps,
            "-NoProfile -NonInteractive -Command \"[Console]::Out.Write((Get-Location).Path + '|' + $env:CADV + '|' + $env:CADV_TWO)\"",
            r => r with
            {
                WorkingDirectory = cwd,
                Env = ValueList.Of(new KeyValuePair<string, string>("CADV", "one"), new KeyValuePair<string, string>("CADV_TWO", "two words")),
            });
        ClassifiedTask c = e.Save(job);
        IReadOnlyList<RunRecord> recs = e.RunAndWait(c, 1, Wait, "cwd/env job");
        if (recs.Count == 0) return;
        T.Eq("cwd/env: status", RunStatus.Ok, recs[0].Status);
        T.Eq("cwd/env: output", cwd + "|one|two words", e.Store.ReadOutput(job.Id, recs[0].RunId, stderr: false));
    });

    // ---- [CP-A] awkward metadata through ConvertFrom-Json under PowerShell 5.1 ----

    public static void AwkwardMetadataRunsCorrectly() => E2E.Scenario(e =>
    {
        const string eAcute = "\u00E9", cjk = "\u65E5\u672C", smile = "\U0001F600", uUml = "\u00DC";
        string name = uUml + "n\u00EF \"q\" & 100% $(x) " + cjk + " " + smile;
        string script = Path.Combine(e.Work, eAcute + "ch o&args.ps1");
        File.WriteAllText(script, """
            $o = [Console]::OpenStandardOutput()
            $u = [char]0xDC
            $t = 'ARGS=' + ($args -join '|') + '|END' + "`n" + 'ENV1=' + $env:CADV1 + "`n" + 'ENV2=' + $env:CADV2 + "`n" + 'ENVU=' + [Environment]::GetEnvironmentVariable('CAD_' + $u + 'NI') + "`n" + 'CWD=' + (Get-Location).Path
            $b = (New-Object Text.UTF8Encoding $false).GetBytes($t)
            $o.Write($b, 0, $b.Length)
            $o.Flush()
            """, new UTF8Encoding(true));

        string a1 = "say \"hi\" & " + eAcute + " " + cjk + " " + smile;
        string a2 = "100%";
        string a4 = "$(whoami)";
        string a5 = "l1\r\nl2";
        string a6 = @"C:\dir with space\";
        string env1 = "v \"1\" & 50% $(whoami) " + eAcute + " " + cjk + "\r\nline2";
        string env2 = "%OS% <>|^!";
        string envU = uUml + smile;
        string args = CommandLine.Join(["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, a1, a2, "%OS%", a4, a5, a6]);
        string exe = @"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe";   // the wrapper expands %VARS% in the exe and the args

        string cwd = Path.Combine(e.Work, "w" + eAcute + "rk dir");
        Directory.CreateDirectory(cwd);
        JobDefinition job = E2E.Job(name, exe, args, r => r with
        {
            Encoding = OutputEncoding.Utf8,
            WorkingDirectory = cwd,
            Env = ValueList.Of(
                new KeyValuePair<string, string>("CADV1", env1),
                new KeyValuePair<string, string>("CADV2", env2),
                new KeyValuePair<string, string>("CAD_" + uUml + "NI", envU)),
        });
        ClassifiedTask c = e.Save(job);
        T.Eq("awkward: the job round-trips through Task Scheduler", job, c.Job);

        IReadOnlyList<RunRecord> recs = e.RunAndWait(c, 1, Wait, "awkward job");
        if (recs.Count == 0) return;
        RunRecord rec = recs[^1];
        T.Eq("awkward: status (an unreadable record would be a launchError with the parser's message)", RunStatus.Ok, rec.Status);
        T.True("awkward: no message", string.IsNullOrEmpty(rec.Message), rec.Message);
        string expected = "ARGS=" + string.Join("|", a1, a2, "Windows_NT", a4, a5, a6) + "|END\n"
            + "ENV1=" + env1 + "\n" + "ENV2=" + env2 + "\n" + "ENVU=" + envU + "\n" + "CWD=" + cwd;
        string? actual = e.Store.ReadOutput(job.Id, rec.RunId, stderr: false);
        T.Eq("awkward: output is byte for byte what was configured", expected, actual ?? "(none)");
        T.True("awkward: the task path of an awkward name is sanitized", c.Observed.Name.Length > 0 && WrapperScript.ValidateTaskPath(c.Observed.Path), c.Observed.Path);
        Fx.Info("awkward job task name: '" + c.Observed.Name + "'");
    });

    // ---- [CP-A] a job that starts a long-lived program ----

    public static void SpawningJobFinishesWithinTheBoundedWait() => E2E.Scenario(e =>
    {
        string bat = Path.Combine(e.Work, "spawn.bat");
        File.WriteAllText(bat, "@echo off\r\necho line one\r\nstart \"\" /b ping -n 72 127.0.0.1 >nul 2>nul\r\nexit /b 0\r\n", new UTF8Encoding(false));
        JobDefinition job = E2E.Job("spawner", bat, "");
        ClassifiedTask c = e.Save(job);

        var sw = Stopwatch.StartNew();
        IReadOnlyList<RunRecord>? first = e.RunAndWaitUnlessBlocked(c, 1, Wait, "spawning job");
        TimeSpan toRecord = sw.Elapsed;
        if (first is null)
        {
            SpawningBlocked(e, c);
            return;
        }
        if (first.Count == 0) return;
        RunRecord r = first[0];
        T.Eq("spawning: status", RunStatus.Ok, r.Status);
        T.Eq("spawning: exit code", 0, r.ExitCode);
        T.True("spawning: message says the output was not fully captured: " + r.Message, r.Message?.StartsWith("output not fully captured: ", StringComparison.Ordinal) == true);
        T.True("spawning: RunRecord.OutputIncomplete and informational", r.OutputIncomplete && r.MessageIsInformational);
        T.True("spawning: end - start under 15 s", r.Duration is { } d && d < TimeSpan.FromSeconds(15), r.Duration?.ToString());
        T.True("spawning: Run() to record under 30 s (10 s bounded wait + start-up): " + toRecord, toRecord < TimeSpan.FromSeconds(30));
        Fx.Info("spawning job: Run() to record " + toRecord.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s, program duration " + r.Duration + ", msg '" + r.Message + "'");

        List<ProcInfo> orphan = Janitor.Find(@"ping\s+-n\s+72\s+127\.0\.0\.1");   // `start "" /b ping -n 72` leaves TWO spaces after "ping" in the child's command line (Phase 19: first run that Defender did not block)
        T.True("spawning: the orphaned ping is still alive (so the pipes really were held)", orphan.Count > 0);
        bool idle = e.WaitIdle(c.Observed.Name, TimeSpan.FromSeconds(20));
        RegisteredTaskInfo? info = e.Gw.Get(c.Observed.Name);
        Fx.Info("spawning job: task state after the wrapper's record = " + info?.State + " (idle within 20 s: " + idle + ")");

        // IgnoreNew must not be blocked by the orphan: a second Run() starts a second instance and writes a second record.
        IReadOnlyList<RunRecord>? second = e.RunAndWaitUnlessBlocked(c, 2, Wait, "second run while the orphan lives");
        if (second is null)
        {
            SpawningBlocked(e, c);
            return;
        }
        if (second.Count >= 2)
        {
            T.True("spawning: the second record is a different run", second[0].RunId != second[1].RunId);
            T.Eq("spawning: second run status", RunStatus.Ok, second[^1].Status);
        }
        JobHistory h = e.History.Load(e.Fresh(c), 10);
        T.True("spawning: the informational note is not a failure", h.Summary.SuccessRate == 1.0, h.Summary.SuccessRate?.ToString(CultureInfo.InvariantCulture));
    }, spawnsProcesses: true);

    /// <summary>
    /// Checkpoint B: on this machine Windows Defender refuses to start the wrapper's command line for the task name "spawner" (Finding A).
    /// The name was NOT changed to dodge that. The spawning checks are reported as a KNOWN-ISSUE instead of a failure, and the product-side
    /// mitigation is verified here for real: the history reads as "Windows refused to start this job: access denied (0x80070005)". As soon as Windows starts the
    /// wrapper, <see cref="E2E.RunAndWaitUnlessBlocked"/> returns records and the spawning checks above run (and can fail) normally.
    /// </summary>
    static void SpawningBlocked(E2E e, ClassifiedTask c)
    {
        T.KnownIssue("spawning job: bounded wait, 'not fully captured' note and the second run were NOT verified",
            "Windows refused to start the wrapper for this task (LastTaskResult 0x80070005, no wrapper process, no run record). On this machine "
            + "Windows Defender reports the wrapper command line as Trojan:Win32/Commando.A!ml (a machine-learning verdict, almost certainly a false "
            + "positive). Documented in the README Safety notes. Not worked around on purpose.");
        e.WaitIdle(c.Observed.Name, TimeSpan.FromSeconds(10));
        JobHistory h = e.History.Load(e.Fresh(c), 10);
        T.True("blocked launch: the history shows one synthesized row", h.Recent.Count >= 1 && h.Recent[0].IsSynthetic, h.Recent.Count.ToString(CultureInfo.InvariantCulture));
        if (h.Recent.Count == 0) return;
        T.Eq("blocked launch: it reads as NotReported (\"Didn't report\")", RunStatus.NotReported, h.Recent[0].Status);
        T.Eq("blocked launch: the plain message states the code only", TaskResultCodes.AccessDeniedLaunch, h.Recent[0].Message);
        T.True("blocked launch: it counts as a failure", h.Summary.SuccessRate is < 1.0 && h.Summary.LastResult == RunStatus.NotReported,
            h.Summary.SuccessRate + " " + h.Summary.LastResult);
    }

    // ---- #10: holiday skip, control, expired list ----

    public static void HolidaySkip() => E2E.Scenario(e =>
    {
        DateOnly today = E2E.Today;
        string marker = "ran.txt";
        Func<RunSpec, RunSpec> inWork = r => r with { WorkingDirectory = e.Work };
        string args = "/d /c \"echo ran>" + marker + "\"";

        // Skipped: the embedded NYSE list is patched so that it contains today (computed now, never a hard-coded date).
        JobDefinition job = E2E.Job("holiday skip", Cmd, args, inWork) with
        {
            Schedule = new ScheduleSpec(ValueList.Of("0 18 * * 1-5"), HolidayCalendar.Nyse, Fx.FarAnchor),
        };
        ClassifiedTask c = e.Save(job);
        T.True("skip: the composed task embeds a long NYSE list", c.Metadata!.Skip is { } s0 && s0.Dates.Count > 50);
        SetSkipDates(e, c, today, include: true);
        string markerPath = Path.Combine(e.Work, marker);
        IReadOnlyList<RunRecord> recs = e.RunAndWait(c, 1, Wait, "holiday skip");
        if (recs.Count > 0)
        {
            RunRecord r = recs[^1];
            T.Eq("skip: status", RunStatus.Skipped, r.Status);
            T.True("skip: message names the holiday: " + r.Message, r.Message?.StartsWith("holiday: Test Holiday", StringComparison.Ordinal) == true);
            T.True("skip: informational", r.MessageIsInformational);
            T.True("skip: no start, no exit code, no pid", r.Start is null && r.ExitCode is null && r.Pid is null);
            T.True("skip: no duration", r.Duration is null);
            T.True("skip: the program did NOT run (no marker file)", !File.Exists(markerPath));
            T.True("skip: no output files", e.Store.ReadOutput(job.Id, r.RunId, false) is null && e.Store.ReadOutput(job.Id, r.RunId, true) is null);
            e.WaitIdle(c.Observed.Name, TimeSpan.FromSeconds(20));
            JobHistory h = e.History.Load(e.Fresh(c), 10);
            T.Eq("skip: history row", RunStatus.Skipped, h.Recent[0].Status);
            T.True("skip: not counted as a failure (no counted run, success rate null)", h.Summary.SuccessRate is null && h.Summary.SampleSize == 0);
            T.True("skip: LastRunAt includes the skip, LastResult does not", h.Summary.LastRunAt is not null && h.Summary.LastResult is null);
        }

        // Control: a list that does not contain today runs the program.
        SetSkipDates(e, c, today, include: false);
        IReadOnlyList<RunRecord> two = e.RunAndWaitAfterIdle(c, 2, Wait, "holiday control");
        if (two.Count >= 2)
        {
            RunRecord r = two[^1];
            T.Eq("control: status", RunStatus.Ok, r.Status);
            T.True("control: the program ran (marker file written)", File.Exists(markerPath));
            T.True("control: no holiday message", r.Message is null, r.Message);
            JobHistory h = e.History.Load(e.Fresh(c), 10);
            T.Eq("control: success rate counts the run, not the skip", 1.0, h.Summary.SuccessRate ?? -1);
            T.Eq("control: sample size", 1, h.Summary.SampleSize);
            File.Delete(markerPath);
        }

        // Expired list: the job runs anyway and says so.
        RegisterWithSkip(e, c, m => m.Skip! with { Until = today.AddDays(-1) });
        IReadOnlyList<RunRecord> three = e.RunAndWaitAfterIdle(c, 3, Wait, "expired list");
        if (three.Count >= 3)
        {
            RunRecord r = three[^1];
            T.Eq("expired: status", RunStatus.Ok, r.Status);
            T.Eq("expired: message", "holiday list expired; ran anyway", r.Message);
            T.True("expired: informational, the program ran", r.MessageIsInformational && File.Exists(markerPath));
        }
    });

    static void SetSkipDates(E2E e, ClassifiedTask c, DateOnly today, bool include) =>
        RegisterWithSkip(e, c, m =>
        {
            IEnumerable<Holiday> others = m.Skip!.Dates.Where(h => h.Date != today);
            IEnumerable<Holiday> all = include ? others.Append(new Holiday(today, "Test Holiday")) : others;
            return m.Skip with { Dates = new ValueList<Holiday>(all.OrderBy(h => h.Date)) };
        });

    static void RegisterWithSkip(E2E e, ClassifiedTask c, Func<CadenceMetadata, SkipList> change) =>
        ApplyFx.RegisterWithEditedMetadata(e.Gw, c.Observed.Name, m => m with { Skip = change(m) });

    // ---- #11: NotReported ----

    public static void NotReportedWhenTheTaskLeavesNoRecord() => E2E.Scenario(e =>
    {
        // A Cadence-marked task whose action is a plain program (no wrapper): Task Scheduler runs it, nothing reports.
        JobDefinition job = E2E.Job("no wrapper", Cmd, "/c exit 0");
        string name = "no wrapper";
        RegisterWithAction(e, job, name, new ExecActionModel(Cmd, "/c exit 0", null));
        ClassifiedTask c0 = e.Jobs.Classify(e.Gw.Get(name)!);
        T.True("notreported: the task is a Cadence task (drifted action)", c0.Job is not null && c0.Ownership == Ownership.Managed, c0.Ownership + " " + c0.Sync);
        e.Gw.Run(name);
        T.True("notreported: Task Scheduler ran it", Fx.WaitFor(() => e.Gw.Get(name) is { LastRunTime: not null, State: not TaskState.Running }, TimeSpan.FromSeconds(40)));
        ClassifiedTask c = e.Fresh(c0);
        JobHistory h = e.History.Load(c, 10);
        T.True("notreported: one synthesized row", h.Recent.Count == 1 && h.Recent[0].IsSynthetic, h.Recent.Count.ToString(CultureInfo.InvariantCulture));
        if (h.Recent.Count > 0)
        {
            T.Eq("notreported: status", RunStatus.NotReported, h.Recent[0].Status);
            T.Eq("notreported: message", RunHistory.NotReportedNoCode, h.Recent[0].Message);
        }
        T.Eq("notreported: counts as a failure", 0.0, h.Summary.SuccessRate ?? -1);
        T.Eq("notreported: last result", RunStatus.NotReported, h.Summary.LastResult);
        T.Eq("notreported: no run record exists", 0, e.Records(c).Count);

        // The wrapper cannot find its own task (wrong $tp): it files its launchError under _unknown, and the job still reads NotReported.
        JobDefinition lost = E2E.Job("lost wrapper", Cmd, "/c exit 0");
        ExecActionModel wrongWrapper = WrapperScript.BuildAction("\\CadenceTest\\no such task", "CadenceTest", hidden: true);
        RegisterWithAction(e, lost, "lost wrapper", wrongWrapper);
        e.Gw.Run("lost wrapper");
        bool unknown = Fx.WaitFor(() => e.Store.ReadTail(RunRecordStore.UnknownFolder).Count >= 1, Wait);
        T.True("lost: the wrapper wrote an _unknown record", unknown);
        if (unknown)
        {
            RunRecord u = e.Store.ReadTail(RunRecordStore.UnknownFolder)[0];
            T.Eq("lost: status", RunStatus.LaunchError, u.Status);
            T.Eq("lost: the record names the task path it tried", "\\CadenceTest\\no such task", u.TaskPath);
            T.True("lost: message is the COM error: " + u.Message, !string.IsNullOrEmpty(u.Message));
            T.Eq("lost: not filed under the job", 0, e.Store.ReadTail(lost.Id).Count);
            e.WaitIdle("lost wrapper", TimeSpan.FromSeconds(20));
            ClassifiedTask lc = e.Jobs.Classify(e.Gw.Get("lost wrapper")!);
            JobHistory lh = e.History.Load(lc, 10);
            T.True("lost: the history reads NotReported", lh.Recent.Count == 1 && lh.Recent[0].Status == RunStatus.NotReported, lh.Recent.Count.ToString(CultureInfo.InvariantCulture));
            T.Eq("lost: the same task path would attribute it", 1, e.Store.ReadTail(lost.Id, "\\CadenceTest\\no such task").Count);
        }
    });

    static void RegisterWithAction(E2E e, JobDefinition job, string taskName, ExecActionModel action)
    {
        TaskDefinitionModel model = TaskComposer.Compose(job, Fx.Ctx(e.Gw, taskName));
        model = model with { ExecActions = new ValueList<ExecActionModel>([action]) };
        e.Gw.Register(taskName, TaskXmlWriter.Write(model));
    }

    // ---- IgnoreNew when a run overlaps ----

    public static void OverlappingRunIsIgnored() => E2E.Scenario(e =>
    {
        JobDefinition job = E2E.Job("overlap", Cmd, "/d /c \"ping -n 9 127.0.0.1 >nul\"");
        ClassifiedTask c = e.Save(job);
        e.Gw.Run(c.Observed.Name);
        bool running = Fx.WaitFor(() => e.Gw.Get(c.Observed.Name) is { State: TaskState.Running }, TimeSpan.FromSeconds(20));
        T.True("overlap: the first run is running", running);
        try
        {
            e.Gw.Run(c.Observed.Name);
            Fx.Info("overlap: a second Run() while running returned without an error");
        }
        catch (Exception ex) when (ex is SchedulerException || SchedulerException.IsComFailure(ex))
        {
            Fx.Info("overlap: a second Run() while running threw " + ex.GetType().Name + ": " + ex.Message);
        }
        IReadOnlyList<RunRecord> recs = e.WaitRecords(c, 1, Wait, "overlap");
        e.WaitIdle(c.Observed.Name, TimeSpan.FromSeconds(30));
        Thread.Sleep(TimeSpan.FromSeconds(6));   // a wrongly queued second instance would have finished by now
        recs = e.Records(c);
        T.Eq("overlap: exactly one run was recorded (IgnoreNew dropped the second)", 1, recs.Count);
        if (recs.Count > 0) T.Eq("overlap: it succeeded", RunStatus.Ok, recs[0].Status);
        T.Eq("overlap: no ping left", 0, Janitor.Find(@"ping -n 9 127\.0\.0\.1").Count);
    }, spawnsProcesses: true);

    // ---- [CP-A] visible mode ----

    public static void VisibleModeReportsTheRealExitCode()
    {
        // A visible job pops a console window on the owner's desktop for a moment, so it is OFF by default and the run says so.
        if (Environment.GetEnvironmentVariable("CADENCE_INTEGRATION_VISIBLE") != "1")
        {
            T.Skipped("a visible-mode run reports the real exit code", "it would flash a console window on your desktop; set CADENCE_INTEGRATION_VISIBLE=1 to run it (the action's shape is covered by the unit tests)");
            return;
        }
        VisibleModeRun();
    }

    static void VisibleModeRun() => E2E.Scenario(e =>
    {
        JobDefinition job = E2E.Job("visible seven", Cmd, "/d /c exit 7", r => r with { Hidden = false });
        ClassifiedTask c = e.Save(job);
        T.True("visible: the action is PowerShell directly, not conhost --headless",
            c.Actual!.ExecActions[0].Command.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase)
            && !c.Actual.ExecActions[0].Arguments!.StartsWith("--headless", StringComparison.Ordinal));
        IReadOnlyList<RunRecord> recs = e.RunAndWait(c, 1, Wait, "visible job");
        if (recs.Count == 0) return;
        T.Eq("visible: status", RunStatus.Failed, recs[0].Status);
        T.Eq("visible: exit code in the record", 7, recs[0].ExitCode);
        T.True("visible: nothing is captured", recs[0].OutBytes == 0 && recs[0].ErrBytes == 0);
        bool seven = Fx.WaitFor(() => e.Gw.Get(c.Observed.Name) is { LastTaskResult: 7 }, TimeSpan.FromSeconds(30));
        T.True("visible: LastTaskResult is 7 (the exit code reaches Task Scheduler)", seven, "LastTaskResult=" + e.Gw.Get(c.Observed.Name)?.LastTaskResult);
        e.WaitIdle(c.Observed.Name, TimeSpan.FromSeconds(20));
        JobHistory h = e.History.Load(e.Fresh(c), 5);
        T.Eq("visible: history row", RunStatus.Failed, h.Recent[0].Status);
    });

    // ---- #9: a real scheduled run ----

    public static void ScheduledRunHappensByItself() => E2E.Scenario(e =>
    {
        // The one test whose trigger is real: cmd /c echo, one minute ahead (the plan asks for it). Everything else is dated 2099.
        // [CP-B] The cron pins the day and month too, so a task left behind by a hard-killed run (no exit hook) fires at most once a year
        // rather than every day; the next integration run's cleanup removes it.
        DateTime now = DateTime.Now;
        DateTime target = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Local).AddMinutes(1);
        if (target - now < TimeSpan.FromSeconds(20)) target = target.AddMinutes(1);   // too close to the minute boundary
        string cron = string.Join(" ", new[] { target.Minute, target.Hour, target.Day, target.Month }.Select(n => n.ToString(CultureInfo.InvariantCulture))) + " *";
        JobDefinition job = E2E.Job("scheduled echo", Cmd, "/d /c echo scheduled") with
        {
            Schedule = new ScheduleSpec(ValueList.Of(cron), HolidayCalendar.None, DateOnly.FromDateTime(target)),
        };
        ClassifiedTask c = e.Save(job);
        Fx.Info("scheduled: cron '" + cron + "' on " + target.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ", now " + now.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
        T.True("scheduled: Task Scheduler's NextRunTime is the target", c.Observed.NextRunTime == target, Fx.Iso(c.Observed.NextRunTime));

        TimeSpan limit = target - DateTime.Now + TimeSpan.FromSeconds(90);
        IReadOnlyList<RunRecord> recs = e.WaitRecords(c, 1, limit, "scheduled run");
        if (recs.Count == 0) return;
        RunRecord r = recs[0];
        T.Eq("scheduled: status", RunStatus.Ok, r.Status);
        T.Eq("scheduled: stdout", "scheduled", e.Store.ReadOutput(job.Id, r.RunId, false)?.Trim());
        T.True("scheduled: it started at the scheduled minute (tolerance 5 s early .. 75 s late)",
            r.WrapperStart.LocalDateTime >= target.AddSeconds(-5) && r.WrapperStart.LocalDateTime <= target.AddSeconds(75), r.WrapperStart.ToString("o", CultureInfo.InvariantCulture));
        e.WaitIdle(c.Observed.Name, TimeSpan.FromSeconds(20));
        RegisteredTaskInfo? info = e.Gw.Get(c.Observed.Name);
        T.True("scheduled: Task Scheduler's LastRunTime matches", info?.LastRunTime is { } lr && Math.Abs((lr - r.WrapperStart.LocalDateTime).TotalSeconds) < 30);
        T.Eq("scheduled: LastTaskResult", 0, info?.LastTaskResult);
        JobHistory h = e.History.Load(e.Fresh(c), 5);
        T.Eq("scheduled: history", 1.0, h.Summary.SuccessRate ?? -1);
        e.Gw.Delete(c.Observed.Name);   // do not let it fire again tomorrow, whatever happens next
    });

    // ---- #12: retention and the CRLF-rewritten journal ----

    public static void RetentionKeepsFourThousandLinesAndFourHundredFiles() => E2E.Scenario(e =>
    {
        JobDefinition job = E2E.Job("retention", Cmd, "/d /c echo retention");
        ClassifiedTask c = e.Save(job);
        string dir = Path.Combine(e.Store.Root, job.Id);
        Directory.CreateDirectory(dir);

        // Seed a journal over the 2 MB cap (LF endings, the wrapper's own record shape) and 450 old output files.
        var sb = new StringBuilder();
        var t0 = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        int lines = 0;
        while (sb.Length < 2_200_000)
        {
            DateTime t = t0.AddMinutes(lines);
            string runId = t.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-000";
            string iso = t.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + ".0000000+00:00";
            sb.Append("{\"v\":1,\"wv\":1,\"task\":\"\\\\CadenceTest\\\\retention\",\"runId\":\"").Append(runId).Append("\",\"wrapperStart\":\"").Append(iso)
              .Append("\",\"start\":\"").Append(iso).Append("\",\"end\":\"").Append(iso).Append("\",\"exit\":0,\"status\":\"ok\",\"pid\":4242,\"outBytes\":5,\"errBytes\":0,\"msg\":null,\"pad\":\"")
              .Append('x', 40).Append("\"}\n");
            lines++;
        }
        string journal = Path.Combine(dir, "runs.jsonl");
        File.WriteAllText(journal, sb.ToString(), new UTF8Encoding(false));
        for (int i = 0; i < 450; i++)
            File.WriteAllText(Path.Combine(dir, "20190101-000000-" + i.ToString("000", CultureInfo.InvariantCulture) + ".out.txt"), "old " + i);
        long seeded = new FileInfo(journal).Length;
        T.True("retention: the seeded journal is over the 2 MB cap (" + seeded + " bytes, " + lines + " lines)", seeded > 2_097_152);

        IReadOnlyList<RunRecord> recs = e.RunAndWait(c, 1, Wait, "retention run");
        // The new record is appended and then the cap check runs: wait for the rewrite to finish (the file shrinks).
        bool shrunk = Fx.WaitFor(() => new FileInfo(journal).Length < 1_500_000, TimeSpan.FromSeconds(60));
        T.True("retention: the journal was rewritten under the cap", shrunk, new FileInfo(journal).Length + " bytes");
        Fx.WaitFor(() => Directory.GetFiles(dir, "*.txt").Length <= 400, TimeSpan.FromSeconds(30));
        T.Eq("retention: 400 output files remain after the first run", 400, Directory.GetFiles(dir, "*.txt").Length);

        string[] kept = File.ReadAllLines(journal);
        T.Eq("retention: 4000 lines remain", 4000, kept.Length);
        byte[] bytes = File.ReadAllBytes(journal);
        int crlf = 0;
        for (int i = 1; i < bytes.Length; i++) if (bytes[i] == '\n' && bytes[i - 1] == '\r') crlf++;
        T.Eq("retention: the rewrite uses CRLF on every line", 4000, crlf);
        T.True("retention: the newest line is the new run", kept[^1].Contains("\"runId\":\"" + DateTime.Now.Year, StringComparison.Ordinal) && kept[^1].Contains("\"status\":\"ok\"", StringComparison.Ordinal), kept[^1]);

        // [CP-A] (d) the CRLF-rewritten journal still parses line by line.
        int parsed = kept.Count(l => RunRecordParser.ParseLine(l, job.Id) is not null);
        T.Eq("retention: every rewritten line parses", 4000, parsed);
        IReadOnlyList<RunRecord> all = e.Store.ReadTail(job.Id, 5000);
        T.Eq("retention: RunRecordStore reads all 4000", 4000, all.Count);
        T.True("retention: the last record is the new ok run", all.Count > 0 && all[^1].Status == RunStatus.Ok && all[^1].WrapperStart.Year >= 2026);

        // A second run appends to the CRLF file (mixed endings are legal): still parses.
        e.WaitIdle(c.Observed.Name, TimeSpan.FromSeconds(20));
        e.RunAndWait(c, 4001, Wait, "run after the rewrite");
        IReadOnlyList<RunRecord> after = e.Store.ReadTail(job.Id, 5000);
        T.True("retention: the record appended after the rewrite parses too", after.Count >= 4001 && after[^1].WrapperStart > all[^1].WrapperStart, after.Count.ToString(CultureInfo.InvariantCulture));

        Fx.WaitFor(() => Directory.GetFiles(dir, "*.txt").Length <= 400, TimeSpan.FromSeconds(30));
        string[] txt = Directory.GetFiles(dir, "*.txt");
        T.Eq("retention: 400 output files remain after the second run too (the cap holds)", 400, txt.Length);
        T.True("retention: the oldest files went", !File.Exists(Path.Combine(dir, "20190101-000000-000.out.txt")) && !File.Exists(Path.Combine(dir, "20190101-000000-049.out.txt")));
        T.True("retention: the newest old file stayed", File.Exists(Path.Combine(dir, "20190101-000000-449.out.txt")));
        T.True("retention: the new runs' output is kept", e.Store.ReadOutput(job.Id, all[^1].RunId, false)?.Trim() == "retention" && e.Store.ReadOutput(job.Id, after[^1].RunId, false)?.Trim() == "retention");
    });
}
