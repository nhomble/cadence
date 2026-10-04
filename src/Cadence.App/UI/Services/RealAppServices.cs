using System.IO;
using Cadence.Agent.Audit;
using Cadence.Agent.Policy;
using Cadence.App.UI.Logic;
using Cadence.Core.Execution;
using Cadence.Core.Metadata;
using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.Core.Scheduling;
using Cadence.History;
using Cadence.Scheduler;
using Cadence.Scheduler.Apply;
using Microsoft.Win32;

namespace Cadence.App.UI.Services;

/// <summary>"Show other Task Scheduler tasks" and friends, in HKCU. Reading a missing key returns the default and creates nothing.</summary>
internal sealed class RegistrySettings(string folderName) : IUserSettings
{
    // The dev folder gets its own key so a CadenceTest run never writes the real product's settings.
    readonly string _key = folderName == Cli.FolderPolicy.Product ? @"Software\Cadence" : @"Software\" + folderName;

    public bool ShowForeign
    {
        get
        {
            try
            {
                using RegistryKey? k = Registry.CurrentUser.OpenSubKey(_key, writable: false);
                return k?.GetValue("ShowForeign") is int v && v != 0;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return false;
            }
        }
        set
        {
            try
            {
                using RegistryKey k = Registry.CurrentUser.CreateSubKey(_key);
                k.SetValue("ShowForeign", value ? 1 : 0, RegistryValueKind.DWord);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // A setting that can't be saved is not worth an error dialog; it simply resets next start.
            }
        }
    }

    public DateTimeOffset? AgentNoteSeenAt
    {
        get
        {
            try
            {
                using RegistryKey? k = Registry.CurrentUser.OpenSubKey(_key, writable: false);
                return k?.GetValue("AgentNoteSeenAt") is string text && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTimeOffset at) ? at : null;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return null;
            }
        }
        set
        {
            if (value is not { } at) return;
            try
            {
                using RegistryKey k = Registry.CurrentUser.CreateSubKey(_key);
                k.SetValue("AgentNoteSeenAt", AgentPolicy.Stamp(at), RegistryValueKind.String);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // Not worth an error dialog: the note simply shows again next start.
            }
        }
    }

    public string? WindowPlacement
    {
        get
        {
            try
            {
                using RegistryKey? k = Registry.CurrentUser.OpenSubKey(_key, writable: false);
                return k?.GetValue("Window") as string;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return null;
            }
        }
        set
        {
            if (value is null) return;
            try
            {
                using RegistryKey k = Registry.CurrentUser.CreateSubKey(_key);
                k.SetValue("Window", value, RegistryValueKind.String);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // Not worth an error dialog: the window simply opens in its default place next time.
            }
        }
    }

    public string? ListSort { get => ReadText("ListSort"); set => WriteText("ListSort", value); }

    public string? ListHiddenColumns { get => ReadText("ListHiddenColumns"); set => WriteText("ListHiddenColumns", value); }

    string? ReadText(string name)
    {
        try
        {
            using RegistryKey? k = Registry.CurrentUser.OpenSubKey(_key, writable: false);
            return k?.GetValue(name) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    void WriteText(string name, string? value)
    {
        if (value is null) return;
        try
        {
            using RegistryKey k = Registry.CurrentUser.CreateSubKey(_key);
            k.SetValue(name, value, RegistryValueKind.String);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Not worth an error dialog: the table simply starts with its default look next time.
        }
    }
}

/// <summary>
/// The real services over <see cref="TaskSchedulerGateway"/>, <see cref="ManagedJobs"/>, <see cref="Applier"/> and <see cref="HistoryService"/>.
/// Every Task Scheduler or disk call runs on the thread pool (the gateway opens its own COM session per call and returns plain records),
/// so the UI thread never waits on COM. Writes go through one semaphore so two clicks cannot interleave a live check and a write.
/// Classification is cached by task path and (XML text, local date), so a refresh re-parses only tasks that changed.
/// </summary>
internal sealed class RealAppServices : IAppServices
{
    readonly TaskSchedulerGateway _gateway;
    readonly ManagedJobs _jobs;
    readonly Applier _applier;
    readonly HistoryService _history;
    readonly RunRecordStore _store;
    readonly SemaphoreSlim _writes = new(1, 1);
    readonly SemaphoreSlim _othersLock = new(1, 1);
    readonly Dictionary<string, (string Xml, DateOnly Today, ClassifiedTask Task)> _cache = new(StringComparer.OrdinalIgnoreCase);
    IReadOnlyList<ClassifiedTask>? _others;
    readonly RegistryAgentPolicyStore _policy;
    readonly FileAuditLog _audit;
    readonly string _session = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

    /// <param name="folder">Already validated by <see cref="Cli.FolderPolicy"/>: Cadence or CadenceTest.</param>
    public RealAppServices(string folder)
    {
        FolderName = folder;
        _gateway = new TaskSchedulerGateway(new SchedulerOptions(folder, folder));
        _jobs = new ManagedJobs(_gateway, null, Actor.App);
        _applier = new Applier(_jobs);
        _store = new RunRecordStore(RunRecordStore.RootFor(folder));
        _history = new HistoryService(_store);
        Settings = new RegistrySettings(folder);
        // Constructing these reads and creates nothing: the registry key and the log folder appear on the first write.
        _policy = new RegistryAgentPolicyStore(folder);
        _audit = new FileAuditLog(folder);
    }

    public string FolderName { get; }


    public DateTimeOffset Now => DateTimeOffset.Now;

    public TimeZoneInfo Zone => TimeZoneInfo.Local;

    public IUserSettings Settings { get; }

    public IFileProbe FileProbe { get; } = new Cadence.App.Interop.SystemFileProbe();

    public string RunsFolderPath => _store.Root;

    public Task<ManagedSnapshot> LoadManagedAsync(CancellationToken cancellation = default) => Task.Run(() =>
    {
        cancellation.ThrowIfCancellationRequested();
        IReadOnlyList<RegisteredTaskInfo> infos = _gateway.ListManagedFolder();
        DateTimeOffset now = Now;
        var today = DateOnly.FromDateTime(now.DateTime);

        var classified = new List<ClassifiedTask>(infos.Count);
        lock (_cache)
        {
            foreach (RegisteredTaskInfo info in infos)
            {
                ClassifiedTask c;
                if (_cache.TryGetValue(info.Path, out var hit) && hit.Today == today && string.Equals(hit.Xml, info.Xml, StringComparison.Ordinal))
                    c = hit.Task with { Observed = ManagedJobs.Observe(info) };
                else
                    c = _jobs.Classify(info);
                _cache[info.Path] = (info.Xml, today, c);
                classified.Add(c);
            }
            var live = new HashSet<string>(infos.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
            foreach (string gone in _cache.Keys.Where(k => !live.Contains(k)).ToList()) _cache.Remove(gone);
        }

        // R21: a task that shares its job id with an older one is a copy (Drifted, "This is a copy of ..."), whatever its XML says.
        IReadOnlyList<ClassifiedTask> resolved = DuplicateJobIds.Resolve(classified);
        classified = resolved.ToList();
        var managed = classified.Where(c => c.Ownership != Ownership.Foreign).ToList();
        var entries = new ManagedEntry[managed.Count];
        string userId = managed.Count > 0 ? _gateway.ConnectedUserId : "user";
        Parallel.For(0, managed.Count, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellation }, i =>
        {
            ClassifiedTask c = managed[i];
            // A copy shares its original's run folder: it shows no history of its own.
            JobHistory h = c.Job is null || c.DuplicateOf is not null ? JobHistory.Empty : _history.Load(c, 20);
            DateTimeOffset? next = c.Job is { } job ? ListFormat.NextRun(job.Schedule, job.Enabled, now, Zone, userId) : null;
            entries[i] = new ManagedEntry(c, h, next);
        });
        return new ManagedSnapshot(entries, classified.Where(c => c.Ownership == Ownership.Foreign).ToList(), now);
    }, cancellation);

    public async Task<IReadOnlyList<ClassifiedTask>> LoadOthersAsync(bool refresh, CancellationToken cancellation = default)
    {
        await _othersLock.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (_others is not null && !refresh) return _others;
            _others = await Task.Run(() => _jobs.LoadOthers(), cancellation).ConfigureAwait(false);
            return _others;
        }
        finally { _othersLock.Release(); }
    }

    public Task<ApplyResult> SaveAsync(JobDefinition job, string? existingTaskName, string? expectedRevision = null) =>
        Write(() => _applier.Save(job, existingTaskName, expectedRevision));

    public Task<SavePlan> PlanSaveAsync(JobDefinition job, string? existingTaskName) => Task.Run(() =>
    {
        try
        {
            return new SavePlan(true, _jobs.PlanSave(job, existingTaskName).Summary);
        }
        catch (ComposeException ex) { return new SavePlan(false, ex.Message); }
        catch (PlanRefusedException ex) { return new SavePlan(false, ex.Message); }
        catch (SchedulerException ex) { return new SavePlan(false, "Task Scheduler: " + ex.Message); }
        catch (ArgumentException ex) { return new SavePlan(false, ex.Message); }
    });

    public Task<JobHistory> LoadHistoryAsync(ClassifiedTask task, int max) =>
        Task.Run(() => task.DuplicateOf is not null ? JobHistory.Empty : _history.Load(task, max));

    public Task<string?> ReadOutputAsync(string jobId, string runId, bool stderr) => Task.Run(() =>
    {
        try { return _store.ReadOutput(jobId, runId, stderr); }
        catch (ArgumentException) { return null; }
    });

    public Task<ApplyResult> DeleteAsync(ClassifiedTask task) => Write(() =>
    {
        ApplyResult r = _applier.Delete(task.Observed.Name);
        // The run folder belongs to the job id: keep it while another task (the original of a copy, or a copy of the original) still carries that id.
        if (r.Success && task.Job is { } job && !_jobs.Load(false).Any(c => c.Job is { } other && other.Id == job.Id)) _store.DeleteJob(job.Id);
        return r;
    });

    public Task<ApplyResult> ReapplyAsync(string taskName) => Write(() => _applier.Reapply(taskName));

    public Task<ApplyResult> UntrackAsync(string taskName) => Write(() => _applier.Untrack(taskName));

    public Task<ApplyResult> SetEnabledAsync(ClassifiedTask task, bool enabled)
    {
        if (task.Job is not { } job) return Task.FromResult(new ApplyResult(false, "This task can't be changed from Cadence.", ValueList<FieldDiff>.Empty));
        // The row's revision is what the person was looking at: switching a job on or off that changed in the meantime is refused, not overwritten.
        string? revision = task.Actual is null ? null : JobRevision.Of(task);
        return Write(() => _applier.Save(job with { Enabled = enabled }, task.Observed.Name, revision));
    }

    public Task<string?> RunNowAsync(string taskName) => Task.Run(() =>
    {
        try
        {
            _gateway.Run(taskName);
            return (string?)null;
        }
        catch (SchedulerException ex) { return "Task Scheduler: " + ex.Message; }
        catch (ArgumentException ex) { return ex.Message; }
    });

    public Task<ApplyResult> DeleteAllManagedAsync() => Write(() =>
    {
        int deleted = 0;
        var failures = new List<string>();
        foreach (RegisteredTaskInfo info in _gateway.ListManagedFolder())
        {
            ClassifiedTask c = _jobs.Classify(info);
            if (c.Ownership == Ownership.Foreign) continue;
            ApplyResult r = _applier.Delete(info.Name);
            if (r.Success)
            {
                deleted++;
                if (c.Job is { } job) _store.DeleteJob(job.Id);
            }
            else failures.Add(info.Name + ": " + r.Message);
        }
        if (failures.Count == 0) _gateway.DeleteFolderIfEmpty();
        string text = "Deleted " + deleted.ToString(System.Globalization.CultureInfo.InvariantCulture) + " schedule(s).";
        return failures.Count == 0
            ? new ApplyResult(true, text, ValueList<FieldDiff>.Empty)
            : new ApplyResult(false, text + " Not deleted: " + string.Join("; ", failures), ValueList<FieldDiff>.Empty);
    });

    public Task PruneOrphansAsync(IReadOnlyCollection<string> liveJobIds) => Task.Run(() =>
    {
        _store.PruneOrphans(new HashSet<string>(liveJobIds, StringComparer.Ordinal), TimeSpan.FromDays(30), Now);
    });

    // ---- the agent surface ----

    public Task<AgentPolicy> LoadAgentPolicyAsync() => Task.Run(() => _policy.Read());

    public Task SetAgentsSuspendedAsync(bool suspended) => Task.Run(() =>
    {
        _policy.SetSuspended(suspended, "app", Now);
        Audit(suspended ? "suspended" : "resumed", null, null, null, suspended ? "Suspended in the Cadence app" : "Resumed in the Cadence app");
    });

    public Task SetAgentLimitsAsync(int? minIntervalMinutes, int? maxAgentJobs) => Task.Run(() =>
    {
        AgentPolicy before = _policy.Read();
        _policy.SetLimits(minIntervalMinutes, maxAgentJobs);
        var parts = new List<string>();
        if (minIntervalMinutes is { } m) parts.Add("shortest schedule every " + before.MinIntervalMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture) + " -> every " + m.ToString(System.Globalization.CultureInfo.InvariantCulture) + " min");
        if (maxAgentJobs is { } j) parts.Add("most agent jobs " + before.MaxAgentJobs.ToString(System.Globalization.CultureInfo.InvariantCulture) + " -> " + j.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (parts.Count > 0) Audit("ok", null, null, "limits_changed", "Limits changed in the Cadence app: " + string.Join("; ", parts));
    });

    public Task<ApplyResult> SetAgentLockAsync(ClassifiedTask task, bool locked)
    {
        if (task.Job is not { } job || task.Ownership != Ownership.Managed || task.Sync == SyncStatus.Drifted)
            return Task.FromResult(new ApplyResult(false, "This job can't be locked until the change made outside Cadence is dealt with.", ValueList<FieldDiff>.Empty));
        string? revision = task.Actual is null ? null : JobRevision.Of(task);
        return Write(() =>
        {
            ApplyResult r = _applier.Save(job with { AgentLock = locked }, task.Observed.Name, revision);
            if (r.Success) Audit("ok", job.Id, job.Name, locked ? "locked" : "unlocked", locked ? "Locked against AI agents in the Cadence app" : "Unlocked for AI agents in the Cadence app");
            return r;
        });
    }

    public Task<IReadOnlyList<AuditEntry>> LoadAgentLogAsync(int max) => Task.Run(() =>
    {
        try
        {
            // Both log files (the agents' and the owner's) merged by time, newest first.
            return _audit.TailMerged(max, newestFirst: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    });

    public string AgentLogPath => _audit.FilePath;

    public string AgentLogFolder => Path.GetDirectoryName(_audit.FilePath) ?? "";

    public bool AgentLogExists
    {
        get
        {
            try { return Directory.Exists(AgentLogFolder) && Directory.EnumerateFiles(AgentLogFolder, "audit*.jsonl").Any(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
    }

    void Audit(string outcome, string? jobId, string? jobName, string? code, string detail) =>
        _audit.TryAppend(new AuditEntry(Now, _session, Actor.App.Describe(), null, "app", jobId, jobName, outcome, code, detail));

    async Task<ApplyResult> Write(Func<ApplyResult> action)
    {
        await _writes.WaitAsync().ConfigureAwait(false);
        try { return await Task.Run(action).ConfigureAwait(false); }
        finally { _writes.Release(); }
    }
}
