using Cadence.Agent.Audit;
using Cadence.Agent.Policy;
using Cadence.App.Demo;
using Cadence.App.UI.Logic;
using Cadence.App.UI.Services;
using Cadence.App.UI.ViewModels;
using Cadence.Core.Execution;
using Cadence.Core.History;
using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.History;
using Cadence.Scheduler.Apply;

namespace Cadence.Tests;

/// <summary>
/// A scripted <see cref="IAppServices"/> for the editor and window view-model tests (Phase 15): the test sets <see cref="Entries"/> (what Task Scheduler
/// would report), decides what each write answers, and reads <see cref="Calls"/> afterwards. The clock is fixed. Nothing here touches Task Scheduler or the disk.
/// Not a test class (the name does not end in "Tests").
/// </summary>
internal sealed class ScriptedServices : IAppServices
{
    public List<ManagedEntry> Entries { get; } = [];

    public List<string> Calls { get; } = [];

    public ApplyResult ReapplyResult { get; set; } = Ok("Re-applied.");
    public ApplyResult UntrackResult { get; set; } = Ok("Untracked.");
    public ApplyResult SaveResult { get; set; } = Ok("Saved.");
    public ApplyResult DeleteResult { get; set; } = Ok("Deleted.");
    public string? RunNowError { get; set; }

    /// <summary>What a successful write does to <see cref="Entries"/> (so the next load shows its effect).</summary>
    public Action<string>? AfterReapply { get; set; }
    public Action<string>? AfterUntrack { get; set; }
    public Action<string>? AfterSave { get; set; }
    public Action<string>? AfterRunNow { get; set; }

    /// <summary>When set, <see cref="LoadManagedAsync"/> waits for it (to hold a refresh in flight).</summary>
    public TaskCompletionSource? LoadGate { get; set; }

    /// <summary>When set, <see cref="LoadManagedAsync"/> throws it.</summary>
    public Exception? LoadFails { get; set; }

    public JobDefinition? LastSaved { get; private set; }

    public int LoadCount { get; private set; }

    public string FolderName => "Cadence";
    public DateTimeOffset Now { get; set; } = DemoServices.FixedNow;
    public TimeZoneInfo Zone => DemoServices.FixedZone;
    public IUserSettings Settings { get; } = new Memory();
    public IFileProbe FileProbe { get; } = new DemoFileProbe();
    public string RunsFolderPath => "";

    sealed class Memory : IUserSettings
    {
        public bool ShowForeign { get; set; }
        public string? WindowPlacement { get; set; }
        public DateTimeOffset? AgentNoteSeenAt { get; set; }
    }

    // ---- the agent surface (Phase 23): the test sets what the registry would say and reads what the app wrote ----

    /// <summary>What the registry holds (change it between refreshes).</summary>
    public AgentPolicy Policy { get; set; } = AgentPolicy.Defaults;

    /// <summary>When set, <see cref="LoadAgentPolicyAsync"/> throws it (the app must then show "unreadable").</summary>
    public Exception? PolicyFails { get; set; }

    /// <summary>When set, <see cref="SetAgentsSuspendedAsync"/> throws it.</summary>
    public Exception? SwitchFails { get; set; }

    public int PolicyReads { get; private set; }

    public List<AuditEntry> AgentLog { get; } = [];

    public ApplyResult LockResult { get; set; } = Ok("Locked.");

    public Action<ClassifiedTask, bool>? AfterLock { get; set; }

    public Task<AgentPolicy> LoadAgentPolicyAsync()
    {
        PolicyReads++;
        if (PolicyFails is { } fails) throw fails;
        return Task.FromResult(Policy);
    }

    public Task SetAgentsSuspendedAsync(bool suspended)
    {
        Calls.Add(suspended ? "suspend" : "resume");
        if (SwitchFails is { } fails) throw fails;
        Policy = suspended
            ? Policy with { Suspended = true, FailedClosed = false, ReadProblem = null, SuspendedAt = Now, SuspendedBy = "app", ChangedBy = "app", ChangedAt = Now, ChangedReason = null }
            : Policy with { Suspended = false, FailedClosed = false, ReadProblem = null, ChangedBy = "app", ChangedAt = Now, ChangedReason = null };
        return Task.CompletedTask;
    }

    public Task SetAgentLimitsAsync(int? minIntervalMinutes, int? maxAgentJobs)
    {
        Calls.Add("limits " + minIntervalMinutes + " " + maxAgentJobs);
        Policy = Policy with { MinIntervalMinutes = minIntervalMinutes ?? Policy.MinIntervalMinutes, MaxAgentJobs = maxAgentJobs ?? Policy.MaxAgentJobs };
        return Task.CompletedTask;
    }

    public Task<ApplyResult> SetAgentLockAsync(ClassifiedTask task, bool locked)
    {
        Calls.Add((locked ? "lock " : "unlock ") + task.Observed.Name);
        if (LockResult.Success) AfterLock?.Invoke(task, locked);
        return Task.FromResult(LockResult);
    }

    public Task<IReadOnlyList<AuditEntry>> LoadAgentLogAsync(int max) =>
        Task.FromResult<IReadOnlyList<AuditEntry>>(AgentLog.AsEnumerable().Reverse().Take(max).ToList());

    public string AgentLogPath => @"C:\Test\agents\audit.jsonl";

    public string AgentLogFolder => @"C:\Test\agents";

    public bool AgentLogExists => AgentLog.Count > 0;

    public static ApplyResult Ok(string text) => new(true, text, ValueList<FieldDiff>.Empty);

    public static ApplyResult Fail(string text) => new(false, text, ValueList<FieldDiff>.Empty);

    public static ManagedEntry Entry(ClassifiedTask task, params RunView[] runs) =>
        new(task, new JobHistory(runs, RunHistory.Summarize(runs)), null);

    public static RunView Run(DateTimeOffset when, RunStatus status, double? seconds = null, int? exit = null, string? message = null, bool output = false) =>
        new(when.ToString("yyyyMMdd'T'HHmmss", System.Globalization.CultureInfo.InvariantCulture), when,
            seconds is { } s ? TimeSpan.FromSeconds(s) : null, status, exit ?? (status == RunStatus.Ok ? 0 : null), message, output);

    public async Task<ManagedSnapshot> LoadManagedAsync(CancellationToken cancellation = default)
    {
        LoadCount++;
        Calls.Add("load");
        if (LoadGate is { } gate) await gate.Task;
        if (LoadFails is { } fails) throw fails;
        return new ManagedSnapshot(Entries.ToList(), [], Now);
    }

    public Task<IReadOnlyList<ClassifiedTask>> LoadOthersAsync(bool refresh, CancellationToken cancellation = default) =>
        Task.FromResult<IReadOnlyList<ClassifiedTask>>([]);

    /// <summary>The expected revision of the last save (null when the editor passed none).</summary>
    public string? LastExpectedRevision { get; private set; }

    public Task<ApplyResult> SaveAsync(JobDefinition job, string? existingTaskName, string? expectedRevision = null)
    {
        Calls.Add("save " + existingTaskName);
        LastSaved = job;
        LastExpectedRevision = expectedRevision;
        if (SaveResult.Success) AfterSave?.Invoke(existingTaskName ?? "");
        return Task.FromResult(SaveResult);
    }

    public Task<SavePlan> PlanSaveAsync(JobDefinition job, string? existingTaskName) => Task.FromResult(new SavePlan(true, "No changes to save"));

    public Task<JobHistory> LoadHistoryAsync(ClassifiedTask task, int max)
    {
        Calls.Add("history " + task.Observed.Name);
        ManagedEntry? e = Entries.FirstOrDefault(x => x.Task.Observed.Name == task.Observed.Name);
        return Task.FromResult(e?.History ?? JobHistory.Empty);
    }

    public Task<string?> ReadOutputAsync(string jobId, string runId, bool stderr) => Task.FromResult<string?>(null);

    public Task<ApplyResult> DeleteAsync(ClassifiedTask task)
    {
        Calls.Add("delete " + task.Observed.Name);
        if (DeleteResult.Success) Entries.RemoveAll(e => e.Task.Observed.Name == task.Observed.Name);
        return Task.FromResult(DeleteResult);
    }

    public Task<ApplyResult> ReapplyAsync(string taskName)
    {
        Calls.Add("reapply " + taskName);
        if (ReapplyResult.Success) AfterReapply?.Invoke(taskName);
        return Task.FromResult(ReapplyResult);
    }

    public Task<ApplyResult> UntrackAsync(string taskName)
    {
        Calls.Add("untrack " + taskName);
        if (UntrackResult.Success) AfterUntrack?.Invoke(taskName);
        return Task.FromResult(UntrackResult);
    }

    public Task<ApplyResult> SetEnabledAsync(ClassifiedTask task, bool enabled) => Task.FromResult(Ok("ok"));

    public Task<string?> RunNowAsync(string taskName)
    {
        Calls.Add("run " + taskName);
        if (RunNowError is null) AfterRunNow?.Invoke(taskName);
        return Task.FromResult(RunNowError);
    }

    public Task<ApplyResult> DeleteAllManagedAsync() => Task.FromResult(Ok("ok"));

    public Task PruneOrphansAsync(IReadOnlyCollection<string> liveJobIds) => Task.CompletedTask;
}

/// <summary>An editor host that records what the editor asked the window to do.</summary>
internal sealed class RecordingHost
{
    public int Closed;
    public int Refreshed;
    public readonly List<string> Toasts = [];
    public readonly List<string> Copied = [];
    public readonly List<JobDefinition> Drafts = [];
    public readonly List<string> TaskSchedulerPaths = [];
    public readonly List<string> Links = [];
    public readonly List<EditorViewModel> Replaced = [];
    public readonly List<string> Revealed = [];
    public readonly List<string> OpenedFolders = [];

    public EditorHost Value => new(() => Closed++, () => { Refreshed++; return Task.CompletedTask; }, Toasts.Add, p => { OpenedFolders.Add(p); return true; }, Copied.Add, Drafts.Add, TaskSchedulerPaths.Add, Links.Add, null, Replaced.Add,
        p => { Revealed.Add(p); return true; });
}

/// <summary>Dialogs and shell in one: answers are set by the test, every question is recorded, nothing is ever opened.</summary>
internal sealed class RecordingDialogs : IDialogs, IShell
{
    public bool Answer { get; set; }
    public bool ForeignAnswer { get; set; }
    public readonly List<(string Title, string Message, string Confirm, bool Danger, string? Cancel)> Asked = [];
    public readonly List<ForeignDetailText> ForeignShown = [];
    public readonly List<string> Opened = [];
    public readonly List<string> CopiedText = [];
    public readonly List<string> Links = [];
    public readonly List<string> Revealed = [];
    public readonly List<string> OpenedFolders = [];

    /// <summary>The index <see cref="ChooseAsync"/> answers (-1 = dismissed); every in-app question is recorded in <see cref="Chosen"/>.</summary>
    public int Choice { get; set; } = -1;
    public readonly List<(string Title, string Message, string[] Buttons)> Chosen = [];

    public Task<int> ChooseAsync(string title, string message, IReadOnlyList<PanelChoice> choices)
    {
        Chosen.Add((title, message, choices.Select(c => c.Label).ToArray()));
        return Task.FromResult(Choice);
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool danger)
    {
        Asked.Add((title, message, confirmText, danger, null));
        return Task.FromResult(Answer);
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool danger, string cancelText)
    {
        Asked.Add((title, message, confirmText, danger, cancelText));
        return Task.FromResult(Answer);
    }

    public Task<bool> ShowForeignDetailAsync(ForeignDetailText detail)
    {
        ForeignShown.Add(detail);
        return Task.FromResult(ForeignAnswer);
    }

    public Task<bool> ConfirmTypedAsync(string title, string message, string word, string confirmText) => Task.FromResult(Answer);
    public Task InfoAsync(string title, string message) => Task.CompletedTask;
    public void ShowAbout() { }
    public string? PickProgram() => null;
    public void OpenTaskScheduler() => Opened.Add("taskschd.msc");
    public bool OpenFolder(string path) { OpenedFolders.Add(path); return true; }
    public bool RevealFile(string path) { Revealed.Add(path); return true; }
    public void CopyText(string text) => CopiedText.Add(text);
    public void OpenLink(string uri) => Links.Add(uri);
}
