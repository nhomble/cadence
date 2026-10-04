using System.Globalization;
using Cadence.Agent.Audit;
using Cadence.Agent.Policy;
using Cadence.App.UI.Logic;
using Cadence.App.UI.Services;
using Cadence.Core.Execution;
using Cadence.Core.History;
using Cadence.Core.Holidays;
using Cadence.Core.Metadata;
using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.Core.Text;
using Cadence.Core.TaskXml;
using Cadence.History;
using Cadence.Scheduler.Apply;

namespace Cadence.App.Demo;

/// <summary>What the demo folder contains.</summary>
public enum DemoSet
{
    /// <summary>No schedules: the first-run screen.</summary>
    Empty,

    /// <summary>The seven sample schedules (PLAN 7.7 plus a failing and a blocked one).</summary>
    Normal,

    /// <summary>The normal set plus a damaged and a newer-version task.</summary>
    WithProblems,

    /// <summary>The normal set plus "Nightly photo sync", whose history has every kind of row (for the editor's recent-runs render).</summary>
    RunsShowcase,

    /// <summary>
    /// The normal set with every state of Phase 15: "Sync notes" is running right now, plus "Month-end export" (runs as another user), "Stock ingest (Dad) (copy)"
    /// (a copy of another job), "Archive photos" (outdated) and a damaged and a newer-version task.
    /// </summary>
    Showcase,
}

/// <summary>
/// Deterministic sample data for the unit tests and the dev-only screenshot tool (tools\Cadence.Render; PLAN 7.7). The clock is fixed at 2026-11-27T09:00-05:00 in a fixed UTC-5 zone,
/// so renders are identical between runs and machines. Nothing here touches Task Scheduler or the disk. Edits made in the demo UI
/// (delete, enable/disable) change only this in-memory copy.
/// </summary>
public sealed class DemoServices : IAppServices
{
    public static readonly DateTimeOffset FixedNow = new(2026, 11, 27, 9, 0, 0, TimeSpan.FromHours(-5));
    public static readonly TimeZoneInfo FixedZone = TimeZoneInfo.CreateCustomTimeZone("CadenceDemo", TimeSpan.FromHours(-5), "Demo", "Demo");

    readonly object _gate = new();
    readonly List<ManagedEntry> _entries = [];
    readonly List<ClassifiedTask> _foreign = [];
    readonly List<ClassifiedTask> _foreignInFolder = [];

    AgentPolicy _policy;
    readonly List<AuditEntry> _log = [];

    /// <param name="set">What the folder contains.</param>
    /// <param name="policy">The agent switch and limits (default: agents allowed); renders pass a suspended or unreadable one.</param>
    /// <param name="log">The agent log (default: none, so "Agent activity" is empty); renders pass <see cref="SampleLog"/>.</param>
    public DemoServices(DemoSet set, AgentPolicy? policy = null, IReadOnlyList<AuditEntry>? log = null)
    {
        _policy = policy ?? AgentPolicy.Defaults;
        if (log is not null) _log.AddRange(log);
        Settings = new MemorySettings();
        if (set == DemoSet.Empty) return;

        _entries.Add(Make("d4dst0ck", "Stock ingest (Dad)", "0 18 * * 1-5", HolidayCalendar.Nyse, new RunSpec { Exe = @"C:\Windows\py.exe", Args = @"-u ""C:\Users\Dad\stocks\ingest.py""" }, StockHistory()));
        _entries.Add(Make("y0utub3f", "YouTube feed download", "0 7 * * *", HolidayCalendar.None, new RunSpec { Exe = @"C:\Tools\yt-dlp.exe", Args = "--cookies-from-browser firefox --flat-playlist -J :ythome" }, DailyOk(7, 0, 27, 5, 9)));
        _entries.Add(Make("g00dm0rn", "Good morning", "0 8 * * 1-5", HolidayCalendar.None, new RunSpec { Exe = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", Args = @"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""C:\Users\tester\scripts\morning.ps1""", SourcePath = @"C:\Users\tester\scripts\morning.ps1" }, WeekdayOk(8, 0, 3)));
        _entries.Add(Make("n1ghtbak", "Nightly backup", "0 2 * * *", HolidayCalendar.None, new RunSpec { Exe = @"C:\Tools\restic.exe", Args = @"backup C:\Users\tester\Documents" }, BackupHistory()));
        _entries.Add(Make("syncn0te", "Sync notes", "*/15 * * * *", HolidayCalendar.None, new RunSpec { Exe = @"C:\Users\tester\scripts\sync.bat" }, SyncHistory()));
        _entries.Add(Make("w33kly01", "Weekly report", "0 9 * * 1", HolidayCalendar.None, new RunSpec { Exe = "node.exe", Args = @"C:\Users\tester\scripts\report.js" }, WeeklyHistory(),
            drift: [new FieldDiff("Time", "9:00", "10:00")]));
        _entries.Add(Make("ph0t0bak", "Photo backup", "0 23 * * *", HolidayCalendar.None, new RunSpec { Exe = @"C:\Tools\rclone.exe", Args = @"copy D:\Photos remote:photos" }, PhotoHistory()));
        _entries.Add(Make("d1skcln1", "Disk cleanup", "0 3 * * 0", HolidayCalendar.None, new RunSpec { Exe = @"C:\Windows\System32\cleanmgr.exe", Args = "/sagerun:1" }, BlockedHistory()));

        if (set == DemoSet.RunsShowcase)
        {
            _entries.Add(Make("ph0t0syn", "Nightly photo sync", "30 22 * * *", HolidayCalendar.UsFederal,
                new RunSpec { Exe = @"C:\Tools\rclone.exe", Args = @"sync D:\Photos remote:photos", Timeout = TimeSpan.FromHours(1) }, ShowcaseHistory()));
        }

        if (set == DemoSet.Showcase)
        {
            int sync = _entries.FindIndex(e => e.Task.Job?.Name == "Sync notes");
            _entries[sync] = _entries[sync] with { History = RunningNow(_entries[sync].History) };
            _entries.Add(Make("m0nthend", "Month-end export", "0 6 L * *", HolidayCalendar.UsFederal, new RunSpec { Exe = @"C:\Tools\export.exe", Args = "--all --format csv" }, MonthEndHistory(),
                drift: [new FieldDiff("User", @"TESTPC\tester", @"CORP\svc-reports")], problem: "Runs as a different user"));
            ManagedEntry stock = _entries.First(e => e.Task.Job?.Name == "Stock ingest (Dad)");
            _entries.Add(stock with
            {
                Task = stock.Task with
                {
                    Observed = new ObservedTask("Stock ingest (Dad) (copy)", @"\Cadence\Stock ingest (Dad) (copy)", "", 3, null, null, null),
                    Sync = SyncStatus.Drifted,
                    Problem = DuplicateJobIds.ProblemFormat.Replace("{0}", "Stock ingest (Dad)", StringComparison.Ordinal),
                    DuplicateOf = "Stock ingest (Dad)",
                },
                History = JobHistory.Empty,
            });
            _entries.Add(Make("4rch1v3p", "Archive photos", "0 4 * * 6", HolidayCalendar.UsFederal, new RunSpec { Exe = @"C:\Tools\robocopy.exe", Args = @"D:\Photos E:\Archive /E" }, SaturdayHistory(),
                outdated: "Cadence will update this task when you save (holiday list expires soon)."));
            _entries.Add(new ManagedEntry(Problem(@"Old report (damaged)", Ownership.CorruptMetadata, "This task's Cadence job data is damaged. It can only be deleted."), JobHistory.Empty, null));
            _entries.Add(new ManagedEntry(Problem("Photo sync", Ownership.NewerVersion, "This task was created by a newer version of Cadence. Update Cadence to edit it."), JobHistory.Empty, null));
        }

        if (set == DemoSet.WithProblems)
        {
            _entries.Add(new ManagedEntry(Problem(@"Old report (damaged)", Ownership.CorruptMetadata, "This task's Cadence job data is damaged. It can only be deleted."), JobHistory.Empty, null));
            _entries.Add(new ManagedEntry(Problem("Photo sync", Ownership.NewerVersion, "This task was created by a newer version of Cadence. Update Cadence to edit it."), JobHistory.Empty, null));
        }

        DateTime d(int month, int day, int h, int m) => new(2026, month, day, h, m, 0);
        _foreign.Add(Foreign(@"\Microsoft\Windows\Defrag\ScheduledDefrag", 3, d(11, 25, 1, 0), 0, d(12, 2, 1, 0),
            ForeignXml("Microsoft Corporation", "This task optimizes local storage drives.", @"%windir%\system32\defrag.exe", "-c -h -o -$")));
        _foreign.Add(Foreign(@"\GoogleUpdateTaskMachineCore", 3, d(11, 27, 8, 31), 0, d(11, 27, 14, 31)));
        _foreign.Add(Foreign(@"\MicrosoftEdgeUpdateTaskMachineUA", 3, d(11, 27, 8, 5), 0, d(11, 27, 9, 5)));
        _foreign.Add(Foreign(@"\OneDrive Reporting Task", 3, d(11, 22, 14, 11), unchecked((int)0x80070005), d(11, 29, 14, 11)));
        _foreign.Add(Foreign(@"\Microsoft\Windows\Maintenance\WinSAT", 1, null, null, null));
    }

    /// <summary>Tests only: one more task outside the managed folder (state 1 is disabled), without a definition.</summary>
    public void AddForeign(string path, int state)
    {
        lock (_gate) _foreign.Add(Foreign(path, state, null, null, null));
    }

    public string FolderName => "Cadence";

    public DateTimeOffset Now => FixedNow;

    public TimeZoneInfo Zone => FixedZone;

    public IUserSettings Settings { get; }

    public IFileProbe FileProbe { get; } = new DemoFileProbe();

    public string RunsFolderPath => "";

    public Task<ManagedSnapshot> LoadManagedAsync(CancellationToken cancellation = default)
    {
        lock (_gate) return Task.FromResult(new ManagedSnapshot(_entries.ToList(), _foreignInFolder.ToList(), FixedNow));
    }

    public Task<IReadOnlyList<ClassifiedTask>> LoadOthersAsync(bool refresh, CancellationToken cancellation = default)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<ClassifiedTask>>(_foreign.ToList());
    }

    public Task<ApplyResult> SaveAsync(JobDefinition job, string? existingTaskName, string? expectedRevision = null)
    {
        // Demo data lives in memory only: a save inserts or replaces the entry so the UI behaves, and nothing is written anywhere.
        lock (_gate)
        {
            int i = existingTaskName is null ? -1 : _entries.FindIndex(e => e.Task.Observed.Name == existingTaskName);
            JobHistory history = i >= 0 ? _entries[i].History : JobHistory.Empty;
            var observed = new ObservedTask(job.Name, @"\Cadence\" + job.Name, "", 3, null, null, null);
            CadenceMetadata? before = i >= 0 ? _entries[i].Task.Metadata : null;
            var meta = new CadenceMetadata(1, 1, 1, job, null, "demo", before?.Created ?? FixedNow, FixedNow, ValueList<string>.Empty, before?.CreatedBy ?? "app", "app");
            var task = new ClassifiedTask(observed, Ownership.Managed, SyncStatus.InSync, null, meta, job, ValueList<FieldDiff>.Empty, null);
            var entry = new ManagedEntry(task, history, ListFormat.NextRun(job.Schedule, job.Enabled, FixedNow, FixedZone));
            if (i >= 0) _entries[i] = entry; else _entries.Add(entry);
        }
        return Task.FromResult(Done("Saved (demo data: nothing is written)."));
    }

    public Task<SavePlan> PlanSaveAsync(JobDefinition job, string? existingTaskName)
    {
        lock (_gate)
        {
            JobDefinition? before = existingTaskName is null ? null : _entries.FirstOrDefault(e => e.Task.Observed.Name == existingTaskName)?.Task.Job;
            return Task.FromResult(new SavePlan(true, ChangeSummary(before, job, FolderName)));
        }
    }

    public Task<JobHistory> LoadHistoryAsync(ClassifiedTask task, int max)
    {
        lock (_gate)
        {
            if (_liveRuns.TryGetValue(task.Observed.Name, out int looks))
            {
                if (looks >= 1) { _liveRuns.Remove(task.Observed.Name); FinishDemoRun(task.Observed.Name); }
                else _liveRuns[task.Observed.Name] = looks + 1;
            }
            ManagedEntry? e = _entries.FirstOrDefault(x => x.Task.Observed.Name == task.Observed.Name);
            return Task.FromResult(e is null ? JobHistory.Empty : new JobHistory(e.History.Recent.Take(max).ToList(), e.History.Summary));
        }
    }

    public Task<string?> ReadOutputAsync(string jobId, string runId, bool stderr)
    {
        lock (_gate)
        {
            RunView? run = _entries.Where(e => e.Task.Job?.Id == jobId).SelectMany(e => e.History.Recent).FirstOrDefault(r => r.RunId == runId);
            if (run is null || !run.HasOutput) return Task.FromResult<string?>(null);
            if (stderr)
                return Task.FromResult<string?>(run.Status is RunStatus.Failed or RunStatus.TimedOut ? (run.Message ?? "") + "\r\n" : null);
            string name = _entries.First(e => e.Task.Job?.Id == jobId).Task.Job!.Name;
            string text = "[" + run.When.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "] " + name + " started\r\n"
                + "fetching 3 sources...\r\n"
                + (run.Status == RunStatus.Ok ? "done: 3 of 3 sources up to date\r\n" : "stopped before the last source\r\n");
            return Task.FromResult<string?>(text);
        }
    }

    public Task<ApplyResult> DeleteAsync(ClassifiedTask task)
    {
        lock (_gate) _entries.RemoveAll(e => e.Task.Observed.Name == task.Observed.Name);
        return Task.FromResult(Done("Deleted."));
    }

    public Task<ApplyResult> ReapplyAsync(string taskName)
    {
        // Demo data lives in memory only: the job simply reads as in sync again.
        lock (_gate)
        {
            int i = _entries.FindIndex(e => e.Task.Observed.Name == taskName);
            if (i < 0) return Task.FromResult(new ApplyResult(false, "The task '" + taskName + "' no longer exists in Task Scheduler.", ValueList<FieldDiff>.Empty));
            ManagedEntry old = _entries[i];
            if (old.Task.DuplicateOf is not null) return Task.FromResult(new ApplyResult(false, "'" + taskName + "' is a copy, so Cadence will not re-apply it.", ValueList<FieldDiff>.Empty));
            _entries[i] = old with { Task = old.Task with { Sync = SyncStatus.InSync, DriftDiffs = ValueList<FieldDiff>.Empty, Problem = null } };
        }
        return Task.FromResult(Done("Re-applied (demo)."));
    }

    public Task<ApplyResult> UntrackAsync(string taskName)
    {
        lock (_gate) _entries.RemoveAll(e => e.Task.Observed.Name == taskName);
        return Task.FromResult(Done("Untracked (demo)."));
    }

    public Task<ApplyResult> SetEnabledAsync(ClassifiedTask task, bool enabled)
    {
        lock (_gate)
        {
            int i = _entries.FindIndex(e => e.Task.Observed.Name == task.Observed.Name);
            if (i >= 0 && _entries[i].Task.Job is { } job)
            {
                ManagedEntry old = _entries[i];
                JobDefinition j = job with { Enabled = enabled };
                DateTimeOffset? next = ListFormat.NextRun(j.Schedule, enabled, FixedNow, FixedZone);
                _entries[i] = old with { Task = old.Task with { Job = j }, Next = next };
            }
        }
        return Task.FromResult(Done(enabled ? "Enabled." : "Disabled."));
    }

    // Demo "Run now": the job shows a run that started two seconds ago; after two looks at its history (the editor's 3 s timer) it ends Ok, so the render shows the whole live flow.
    readonly Dictionary<string, int> _liveRuns = new(StringComparer.OrdinalIgnoreCase);

    public Task<string?> RunNowAsync(string taskName)
    {
        lock (_gate)
        {
            int i = _entries.FindIndex(e => e.Task.Observed.Name == taskName);
            if (i >= 0)
            {
                _entries[i] = _entries[i] with { History = RunningNow(_entries[i].History) };
                _liveRuns[taskName] = 0;
            }
        }
        return Task.FromResult<string?>(null);
    }

    void FinishDemoRun(string taskName)
    {
        int i = _entries.FindIndex(e => e.Task.Observed.Name == taskName);
        if (i < 0) return;
        ManagedEntry e = _entries[i];
        var recent = e.History.Recent.Select(r => r.Status == RunStatus.Running ? Run(r.When, 3.2) : r).ToList();
        _entries[i] = e with { History = new JobHistory(recent, RunHistory.Summarize(recent)) };
    }

    public Task<ApplyResult> DeleteAllManagedAsync()
    {
        lock (_gate) _entries.Clear();
        return Task.FromResult(Done("Deleted all (demo)."));
    }

    public Task PruneOrphansAsync(IReadOnlyCollection<string> liveJobIds) => Task.CompletedTask;

    // ---- the agent surface (in memory only) ----

    public Task<AgentPolicy> LoadAgentPolicyAsync()
    {
        lock (_gate) return Task.FromResult(_policy);
    }

    public Task SetAgentsSuspendedAsync(bool suspended)
    {
        lock (_gate)
        {
            _policy = suspended
                ? _policy with { Suspended = true, FailedClosed = false, ReadProblem = null, SuspendedAt = FixedNow, SuspendedBy = "app", ChangedBy = "app", ChangedAt = FixedNow, ChangedReason = null, ChangedFrom = null }
                : _policy with { Suspended = false, FailedClosed = false, ReadProblem = null, ChangedBy = "app", ChangedAt = FixedNow, ChangedReason = null, ChangedFrom = null };
        }
        return Task.CompletedTask;
    }

    public Task SetAgentLimitsAsync(int? minIntervalMinutes, int? maxAgentJobs)
    {
        lock (_gate)
            _policy = _policy with { MinIntervalMinutes = minIntervalMinutes ?? _policy.MinIntervalMinutes, MaxAgentJobs = maxAgentJobs ?? _policy.MaxAgentJobs };
        return Task.CompletedTask;
    }

    public Task<ApplyResult> SetAgentLockAsync(ClassifiedTask task, bool locked)
    {
        lock (_gate)
        {
            int i = _entries.FindIndex(e => e.Task.Observed.Name == task.Observed.Name);
            if (i >= 0 && _entries[i].Task.Job is { } job)
            {
                ManagedEntry old = _entries[i];
                JobDefinition j = job with { AgentLock = locked };
                CadenceMetadata? m = old.Task.Metadata is { } om ? om with { Job = j, Updated = FixedNow, UpdatedBy = "app" } : null;
                _entries[i] = old with { Task = old.Task with { Job = j, Metadata = m } };
            }
        }
        return Task.FromResult(Done(locked ? "Locked." : "Unlocked."));
    }

    public Task<IReadOnlyList<AuditEntry>> LoadAgentLogAsync(int max)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<AuditEntry>>(_log.AsEnumerable().Reverse().Take(max).ToList());
    }

    public string AgentLogPath => @"C:\Users\tester\AppData\Local\Cadence\agents\audit.jsonl";

    public string AgentLogFolder => @"C:\Users\tester\AppData\Local\Cadence\agents";

    public bool AgentLogExists => _log.Count > 0;

    static ApplyResult Done(string message) => new(true, message, ValueList<FieldDiff>.Empty);

    // ---- data ----

    /// <summary>[P23] Who created and last changed a demo job: most in Cadence, "Sync notes" by Claude Code, "Weekly report" from the command line, "Nightly backup" by nobody known (a v0.1.0 job), "Photo backup" locked.</summary>
    sealed record Attribution(DateTimeOffset Created, string? CreatedBy, DateTimeOffset Updated, string? UpdatedBy, bool Locked);

    static readonly Dictionary<string, Attribution> Attributions = new()
    {
        ["d4dst0ck"] = new(At(10, 12, 18, 40), "app", At(11, 20, 17, 5), "app", false),
        ["y0utub3f"] = new(At(9, 3, 7, 10), "app", At(11, 22, 9, 15), "app", false),
        ["g00dm0rn"] = new(At(9, 8, 7, 55), "app", At(11, 26, 7, 40), "app", false),
        ["n1ghtbak"] = new(At(9, 1, 1, 30), null, At(10, 12, 1, 45), null, false),
        ["syncn0te"] = new(At(10, 1, 11, 20), "agent:Claude Code", At(11, 27, 7, 0), "agent:Claude Code", false),
        ["w33kly01"] = new(At(10, 5, 8, 50), "app", At(11, 24, 8, 50), "cli", false),
        ["ph0t0bak"] = new(At(9, 20, 22, 30), "app", At(11, 14, 22, 40), "app", true),
        ["d1skcln1"] = new(At(9, 14, 2, 50), "app", At(10, 30, 3, 0), "app", false),
    };

    /// <summary>The agent log of the Agent activity render: a first session (created a job, was refused, suspended agents), the owner resuming, and a second session.</summary>
    public static IReadOnlyList<AuditEntry> SampleLog { get; } =
    [
        new(At(11, 25, 14, 2, 11), "a1b2c3d4", "Claude Code", "1.0.4", "session", null, null, "connected", null, "MCP session started"),
        new(At(11, 25, 14, 3, 40), "a1b2c3d4", "Claude Code", "1.0.4", "cadence_save_job", "syncn0te", "Sync notes", "ok", null, "Created the job"),
        new(At(11, 25, 14, 5, 2), "a1b2c3d4", "Claude Code", "1.0.4", "cadence_save_job", "syncn0te", "Sync notes", "refused", "schedule_too_frequent", "Runs every minute; agents may not make schedules more often than every 5 minutes."),
        new(At(11, 25, 14, 9, 30), "a1b2c3d4", "Claude Code", "1.0.4", "cadence_suspend_agents", null, null, "suspended", null, "Suspended by the agent: tidying up for the day"),
        new(At(11, 25, 14, 31, 5), "e5f6a7b8", "Cadence (the app)", null, "app", null, null, "resumed", null, "Resumed in the Cadence app"),
        new(At(11, 27, 6, 58, 20), "c9d0e1f2", "Claude Code", "1.0.4", "session", null, null, "connected", null, "MCP session started"),
        new(At(11, 27, 7, 0, 3), "c9d0e1f2", "Claude Code", "1.0.4", "cadence_save_job", "syncn0te", "Sync notes", "ok", null, "Updated the schedule: every 15 minutes"),
        new(At(11, 27, 7, 0, 41), "c9d0e1f2", "Claude Code", "1.0.4", "cadence_set_job_enabled", "ph0t0bak", "Photo backup", "refused", "locked", "The owner locked this job against changes by AI agents."),
        new(At(11, 27, 7, 4, 12), "c9d0e1f2", "Claude Code", "1.0.4", "session", null, null, "disconnected", null, "MCP session ended"),
    ];

    static ManagedEntry Make(string id, string name, string cron, HolidayCalendar skip, RunSpec run, IReadOnlyList<RunView> runs, FieldDiff[]? drift = null,
        string? problem = null, string? outdated = null)
    {
        var job = new JobDefinition
        {
            Id = id,
            Name = name,
            Schedule = new ScheduleSpec(ValueList.Of(cron), skip, new DateOnly(2026, 9, 1)),
            Run = run,
        };
        Attribution a = Attributions.TryGetValue(id, out Attribution? known) ? known : new Attribution(At(11, 10, 9, 30), "app", At(11, 10, 9, 30), "app", false);
        job = job with { AgentLock = a.Locked };
        var meta = new CadenceMetadata(1, 1, 1, job, null, "demo", a.Created, a.Updated, ValueList<string>.Empty, a.CreatedBy, a.UpdatedBy);
        var observed = new ObservedTask(name, @"\Cadence\" + name, "", 3, null, null, null);
        SyncStatus sync = drift is not null ? SyncStatus.Drifted : outdated is not null ? SyncStatus.Outdated : SyncStatus.InSync;
        var task = new ClassifiedTask(observed, Ownership.Managed, sync, null, meta, job,
            drift is null ? ValueList<FieldDiff>.Empty : new ValueList<FieldDiff>(drift),
            drift is not null ? problem ?? "Changed in Task Scheduler: time 9:00 -> 10:00" : outdated);
        var history = new JobHistory(runs.Take(20).ToList(), RunHistory.Summarize(runs));
        return new ManagedEntry(task, history, ListFormat.NextRun(job.Schedule, job.Enabled, FixedNow, FixedZone));
    }

    static ClassifiedTask Problem(string name, Ownership ownership, string text) =>
        new(new ObservedTask(name, @"\Cadence\" + name, "", 3, null, null, null), ownership, SyncStatus.NotApplicable, null, null, null, ValueList<FieldDiff>.Empty, text);

    static ClassifiedTask Foreign(string path, int state, DateTime? last, int? result, DateTime? next, string? xml = null) =>
        new(new ObservedTask(path[(path.LastIndexOf('\\') + 1)..], path, xml ?? "", state, last, result, next), Ownership.Foreign, SyncStatus.NotApplicable,
            xml is null ? null : TaskXmlReader.Read(xml), null, null, ValueList<FieldDiff>.Empty, null);

    /// <summary>A small task definition of someone else's task (demo only), so the read-only detail can show what it runs.</summary>
    static string ForeignXml(string author, string description, string command, string args) =>
        "<?xml version=\"1.0\" encoding=\"UTF-16\"?><Task version=\"1.4\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">"
        + "<RegistrationInfo><Author>" + System.Security.SecurityElement.Escape(author) + "</Author><Description>" + System.Security.SecurityElement.Escape(description) + "</Description></RegistrationInfo>"
        + "<Triggers><CalendarTrigger><StartBoundary>2026-01-01T01:00:00</StartBoundary><ScheduleByWeek><WeeksInterval>1</WeeksInterval><DaysOfWeek><Wednesday /></DaysOfWeek></ScheduleByWeek></CalendarTrigger></Triggers>"
        + "<Principals><Principal id=\"Author\"><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>"
        + "<Settings><Enabled>true</Enabled></Settings>"
        + "<Actions Context=\"Author\"><Exec><Command>" + System.Security.SecurityElement.Escape(command) + "</Command><Arguments>" + System.Security.SecurityElement.Escape(args) + "</Arguments></Exec></Actions></Task>";

    /// <summary>The history with a run that started two seconds before the demo clock and has not ended (the editor's live indicator).</summary>
    static JobHistory RunningNow(JobHistory history)
    {
        var running = new RunView("20261127T085958", At(11, 27, 8, 59, 58), null, RunStatus.Running, null, null, HasOutput: false);
        var recent = new List<RunView> { running };
        recent.AddRange(history.Recent.Where(r => r.When < running.When).Take(19));
        return new JobHistory(recent, history.Summary);
    }

    static DateTimeOffset At(int month, int day, int hour, int minute, int second = 0) => new(2026, month, day, hour, minute, second, TimeSpan.FromHours(-5));

    static RunView Run(DateTimeOffset when, double seconds, RunStatus status = RunStatus.Ok, int? exit = 0, string? message = null) =>
        new(when.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture), when,
            status is RunStatus.Skipped or RunStatus.NotReported or RunStatus.LaunchError ? null : TimeSpan.FromSeconds(seconds),
            status, status is RunStatus.Skipped or RunStatus.NotReported ? null : exit, message, HasOutput: status != RunStatus.Skipped);

    static List<RunView> StockHistory()
    {
        var runs = new List<RunView> { Run(At(11, 26, 18, 0), 0, RunStatus.Skipped, null, "Thanksgiving Day") };
        double[] secs = [31, 24, 38, 27, 22, 35, 29, 33, 26, 40];
        int[][] days = [[25], [24], [23], [20], [19], [18], [17], [16], [13], [12]];
        for (int i = 0; i < days.Length; i++)
        {
            int day = days[i][0];
            runs.Add(day == 17
                ? Run(At(11, day, 18, 0, 2), 2, RunStatus.Failed, 1, "HTTPError: 429 Too Many Requests")
                : Run(At(11, day, 18, 0, 1 + i % 3), secs[i]));
        }
        return runs;
    }

    static List<RunView> DailyOk(int hour, int minute, int fromDay, double minSeconds, double maxSeconds)
    {
        var runs = new List<RunView>();
        for (int i = 0; i < 14; i++)
        {
            DateTime day = new DateTime(2026, 11, fromDay).AddDays(-i);
            runs.Add(Run(At(day.Month, day.Day, hour, minute, i % 4), minSeconds + (maxSeconds - minSeconds) * ((i * 7) % 5) / 4.0));
        }
        return runs;
    }

    /// <summary>[CP-C] A run at the demo's fixed UTC-5 clock (like <see cref="At"/>, for any day).</summary>
    static DateTimeOffset Local(DateTime day, int hour, int minute, int second) =>
        new(day.Year, day.Month, day.Day, hour, minute, second, TimeSpan.FromHours(-5));

    /// <summary>[CP-C] "Month-end export" ("0 6 L * *"): its runs are on the last day of each month (it used to show a run every day).</summary>
    static List<RunView> MonthEndHistory()
    {
        var runs = new List<RunView>();
        for (int i = 0; i < 6; i++)
        {
            DateTime first = new DateTime(2026, 11, 1).AddMonths(-i);
            runs.Add(Run(Local(first.AddDays(-1), 6, 0, i % 3), 20 + (i * 7) % 21));
        }
        return runs;
    }

    /// <summary>[CP-C] "Archive photos" ("0 4 * * 6"): one run each Saturday (it used to show a run every day).</summary>
    static List<RunView> SaturdayHistory()
    {
        var runs = new List<RunView>();
        DateTime day = new(2026, 11, 21);
        for (int i = 0; i < 8; i++, day = day.AddDays(-7)) runs.Add(Run(Local(day, 4, 0, i % 2), 60 + (i * 7) % 31));
        return runs;
    }
    static List<RunView> WeekdayOk(int hour, int minute, double seconds)
    {
        var runs = new List<RunView>();
        DateTime day = new(2026, 11, 27);
        while (runs.Count < 10)
        {
            if (day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) runs.Add(Run(At(day.Month, day.Day, hour, minute, 1), seconds));
            day = day.AddDays(-1);
        }
        return runs;
    }

    static List<RunView> BackupHistory()
    {
        var runs = new List<RunView> { Run(At(11, 27, 2, 0, 3), 3600, RunStatus.TimedOut, null, "Stopped after the 1 h time limit.") };
        for (int i = 1; i < 10; i++)
        {
            DateTime day = new DateTime(2026, 11, 27).AddDays(-i);
            runs.Add(Run(At(day.Month, day.Day, 2, 0, 1), 840 + i * 17));
        }
        return runs;
    }

    static List<RunView> SyncHistory()
    {
        var runs = new List<RunView>();
        DateTimeOffset t = At(11, 27, 8, 45);
        for (int i = 0; i < 20; i++) runs.Add(Run(t.AddMinutes(-15 * i).AddSeconds(1), 1 + (i % 3) * 0.4));
        return runs;
    }

    static List<RunView> ShowcaseHistory() =>
    [
        Run(At(11, 26, 22, 30, 1), 0, RunStatus.Skipped, null, "Thanksgiving Day"),
        Run(At(11, 25, 22, 30, 2), 2.4, RunStatus.Failed, 3, "Directory not found: remote:photos"),
        Run(At(11, 24, 22, 30, 1), 186),
        Run(At(11, 23, 22, 30, 3), 0, RunStatus.NotReported, null, TaskResultCodes.AccessDeniedLaunch),
        Run(At(11, 22, 22, 30, 1), 201),
        Run(At(11, 21, 22, 30, 1), 3600, RunStatus.TimedOut, null, "Stopped after the 1 h time limit."),
        Run(At(11, 20, 22, 30, 2), 175),
        Run(At(11, 19, 22, 30, 1), 192),
    ];

    static List<RunView> WeeklyHistory() => [Run(At(11, 23, 9, 0, 1), 4.2), Run(At(11, 16, 9, 0, 2), 3.9), Run(At(11, 9, 9, 0, 1), 4.6)];

    static List<RunView> PhotoHistory() =>
    [
        Run(At(11, 26, 23, 0, 2), 2.1, RunStatus.Failed, 3, "Directory not found: remote:photos"),
        Run(At(11, 25, 23, 0, 1), 188), Run(At(11, 24, 23, 0, 1), 201), Run(At(11, 23, 23, 0, 2), 175),
    ];

    static List<RunView> BlockedHistory() =>
    [
        Run(At(11, 22, 3, 0, 1), 0, RunStatus.NotReported, null, TaskResultCodes.AccessDeniedLaunch),
        Run(At(11, 15, 3, 0, 1), 64), Run(At(11, 8, 3, 0, 2), 61),
    ];

    /// <summary>"Will create ..." for a new job, else "Will update: name, schedule ..." naming what differs; "No changes to save" when nothing does.</summary>
    static string ChangeSummary(JobDefinition? before, JobDefinition after, string folderName)
    {
        ArgumentNullException.ThrowIfNull(after);
        if (before is null) return "Will create \\" + folderName + "\\" + after.Name.Trim();
        var parts = new List<string>();
        if (before.Name != after.Name) parts.Add("name \"" + before.Name + "\" → \"" + after.Name + "\"");
        if (!before.Schedule.CronLines.Equals(after.Schedule.CronLines) || before.Schedule.SkipHolidays != after.Schedule.SkipHolidays)
            parts.Add("schedule \"" + EnglishRenderer.Describe(before.Schedule) + "\" → \"" + EnglishRenderer.Describe(after.Schedule) + "\"");
        if (before.Run.Exe != after.Run.Exe) parts.Add("program " + before.Run.Exe + " → " + after.Run.Exe);
        if (before.Run.Args != after.Run.Args) parts.Add("arguments");
        if (before.Run.WorkingDirectory != after.Run.WorkingDirectory) parts.Add("working folder");
        if (before.Run.Hidden != after.Run.Hidden) parts.Add(after.Run.Hidden ? "now runs hidden" : "now runs visible");
        if (before.Run.Timeout != after.Run.Timeout) parts.Add("timeout");
        if (before.Run.Encoding != after.Run.Encoding) parts.Add("output encoding");
        if (!before.Run.Env.Equals(after.Run.Env)) parts.Add("environment variables");
        if (before.Enabled != after.Enabled) parts.Add(after.Enabled ? "enabled" : "disabled");
        return parts.Count == 0 ? "No changes to save" : "Will update: " + string.Join(", ", parts);
    }

    sealed class MemorySettings : IUserSettings
    {
        public bool ShowForeign { get; set; }

        public string? WindowPlacement { get; set; }

        public DateTimeOffset? AgentNoteSeenAt { get; set; }

        public string? ListSort { get; set; }

        public string? ListHiddenColumns { get; set; }
    }
}
