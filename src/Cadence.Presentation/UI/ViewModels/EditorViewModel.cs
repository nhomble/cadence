using System.Windows.Input;
using Cadence.App.UI.Logic;
using Cadence.App.UI.Services;
using Cadence.Mvvm;
using Cadence.Core.Cron;
using Cadence.Core.History;
using Cadence.Core.Metadata;
using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.Core.TaskXml;
using Cadence.Core.Text;
using Cadence.History;
using Cadence.Scheduler.Apply;

namespace Cadence.App.UI.ViewModels;

/// <summary>What the editor needs from the window around it: leaving, refreshing the list, a toast, opening a folder, the clipboard.</summary>
public sealed record EditorHost(Action Close, Func<Task> RefreshList, Action<string> Toast, Func<string, bool> OpenFolder, Action<string> CopyText,
    Action<JobDefinition>? OpenDraft = null, Action<string>? OpenInTaskScheduler = null, Action<string>? OpenLink = null,
    Action<EditorViewModel>? PickPreset = null, Action<EditorViewModel>? Replace = null, Func<string, bool>? RevealFile = null)
{
    public static EditorHost None { get; } = new(() => { }, () => Task.CompletedTask, _ => { }, _ => false, _ => { });
}

/// <summary>
/// The schedule editor (PLAN 7.2 B and C, Phase 13). It edits a draft of one <see cref="JobDefinition"/> and is the DataContext of the editor view. It owns
/// the name, Enabled, the dirty check and the problems (the notes under the fields), the inline Planner summary, Save / Cancel / Delete and the ⋯ menu, and
/// composes four section view models: <see cref="Program"/> (the program card and Advanced), <see cref="Schedule"/> (Simple | Cron, Skip holidays, the live
/// preview), <see cref="Runs"/> (the recent runs of a saved job) and <see cref="Notice"/> (drift and the lock against AI agents). Each section tells this
/// class about an edit through <c>Changed</c>. Everything that reaches Task Scheduler or the disk goes through <see cref="IAppServices"/>; nothing here
/// uses WPF types, so the whole thing is unit tested.
/// </summary>
public sealed class EditorViewModel : ObservableObject
{
    public static readonly TimeSpan PlanDebounce = TimeSpan.FromMilliseconds(350);

    readonly IAppServices _services;
    readonly IDialogs _dialogs;
    readonly EditorHost _host;
    readonly string _id;
    readonly DateTimeOffset _now;
    readonly TimeZoneInfo _zone;
    readonly JobSettings _settings;
    bool _agentLock;            // PLAN 12.4.4: the owner's lock against AI agents; carried through every save, changed only by the ⋯ menu item (a save of its own)
    string? _revision;          // JobRevision of the task as this editor last loaded or wrote it; Save refuses when the task changed since (12.1.3)
    readonly bool _nameIsPlaceholder;
    readonly AsyncRelayCommand _saveCommand;
    readonly AsyncRelayCommand _deleteCommand;

    ManagedEntry? _existing;
    JobDefinition _baseline;
    bool _loading = true;
    bool _nameTouched;
    int _planVersion;
    bool _menuOpen;

    // identity and action
    string _name;
    bool _enabled;

    // results
    IReadOnlyList<EditorProblem> _problems = [];
    string? _planText;
    bool _planOk = true;
    bool _busy;
    string? _status;
    bool _statusIsError;

    public EditorViewModel(IAppServices services, IDialogs dialogs, EditorHost host, JobDefinition draft, ManagedEntry? existing,
        bool nameIsPlaceholder = false, DateTimeOffset? nowOverride = null, JobDefinition? baseline = null)
    {
        _services = services;
        _dialogs = dialogs;
        _host = host;
        _existing = existing;
        _id = draft.Id;
        _settings = draft.Settings;
        _agentLock = draft.AgentLock;
        _revision = RevisionOf(existing);
        _nameIsPlaceholder = nameIsPlaceholder;
        _now = nowOverride ?? services.Now;
        _zone = services.Zone;
        _name = draft.Name;
        _enabled = draft.Enabled;
        Program = new ProgramSectionViewModel(this, draft.Run);
        Schedule = new ScheduleSectionViewModel(this, draft.Schedule);
        _baseline = baseline ?? BuildJob();

        BackCommand = new AsyncRelayCommand(RequestCloseAsync);
        _saveCommand = new AsyncRelayCommand(SaveAsync, () => CanSave, ShowError);
        _deleteCommand = new AsyncRelayCommand(DeleteAsync, () => CanDelete, ShowError);
        ToggleMenuCommand = new RelayCommand(() => IsMenuOpen = !IsMenuOpen);
        CloseMenuCommand = new RelayCommand(() => IsMenuOpen = false);
        OpenInTaskSchedulerCommand = new RelayCommand(OpenInTaskScheduler);

        Runs = new RunsSectionViewModel(this);
        Notice = new EditorNoticeViewModel(this);

        _loading = false;
        Revalidate();
        if (existing is not null) Runs.ApplyHistory(existing.History);
        _ = SchedulePlanAsync();
    }

    // ============ creation ============

    /// <summary>A new job from a template: its time (and Skip holidays) pre-filled, the program still to be chosen.</summary>
    public static EditorViewModel ForTemplate(IAppServices services, IDialogs dialogs, EditorHost host, Template template, DateTimeOffset? nowOverride = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        DateTimeOffset now = nowOverride ?? services.Now;
        var draft = new JobDefinition
        {
            Id = JobId.New(),
            Name = template.SuggestedName,
            Schedule = new ScheduleSpec(ValueList.Of(template.Cron), template.SuggestedSkip, DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, services.Zone).DateTime)),
            Run = new RunSpec { Exe = "" },
        };
        return new EditorViewModel(services, dialogs, host, draft, null, nameIsPlaceholder: template.Id == "blank", nowOverride);
    }

    /// <summary>An existing managed job.</summary>
    public static EditorViewModel ForEntry(IAppServices services, IDialogs dialogs, EditorHost host, ManagedEntry entry, DateTimeOffset? nowOverride = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        JobDefinition job = entry.Task.Job ?? throw new ArgumentException("The entry has no job.", nameof(entry));
        return new EditorViewModel(services, dialogs, host, job, entry, false, nowOverride);
    }

    // ============ identity ============

    public bool IsNew => _existing is null;

    public bool IsExisting => _existing is not null;

    public string? ExistingTaskName => _existing?.Task.Observed.Name;

    public string Heading => IsNew ? "New schedule" : "Edit schedule";

    public IAppServices Services => _services;

    /// <summary>The program card and the Advanced section (program, arguments, working folder, time limit, environment).</summary>
    public ProgramSectionViewModel Program { get; }

    /// <summary>WHEN: the Simple | Cron schedule, Skip holidays, presets and the live preview.</summary>
    public ScheduleSectionViewModel Schedule { get; }

    /// <summary>The notice above the form (drift, copy, newer version, outdated), its actions, the attribution line and the lock against AI agents.</summary>
    public EditorNoticeViewModel Notice { get; }

    /// <summary>The runs panel (recent runs, live refresh, Run now, could-not-start notice).</summary>
    public RunsSectionViewModel Runs { get; }

    internal EditorHost Host => _host;

    internal IDialogs Dialogs => _dialogs;

    /// <summary>True while the editor is being built or a whole program choice is applied: edits then don't re-validate one by one.</summary>
    internal bool Loading { get => _loading; set => _loading = value; }

    /// <summary>A program choice may name the job (a dropped file that started it, or a still-placeholder name nobody has typed over).</summary>
    internal void SuggestName(string? suggested, bool nameFromProgram)
    {
        if ((nameFromProgram || (_nameIsPlaceholder && !_nameTouched)) && suggested is { Length: > 0 })
        {
            _name = suggested;
            OnPropertyChanged(nameof(Name));
        }
    }

    internal string Id => _id;

    /// <summary>The owner's lock against AI agents; carried through every save and changed only by the ⋯ menu item (a save of its own).</summary>
    internal bool AgentLock { get => _agentLock; set => _agentLock = value; }

    /// <summary>The job as this editor last loaded or wrote it; "dirty" means the form differs from it.</summary>
    internal JobDefinition Baseline { get => _baseline; set => _baseline = value; }

    internal DateTimeOffset Now => _now;

    internal TimeZoneInfo Zone => _zone;

    /// <summary>The saved task as this editor last saw it (null for a new job); the sections refresh it after a write or a live look.</summary>
    internal ManagedEntry? Existing { get => _existing; set => _existing = value; }

    internal void RaiseHasRunsPanel() => OnPropertyChanged(nameof(HasRunsPanel));

    internal void SetStatus(string? text, bool isError)
    {
        Status = text;
        StatusIsError = isError;
    }

    public string Name
    {
        get => _name;
        set { if (Set(ref _name, value)) { _nameTouched = true; Changed(); } }
    }

    public string NamePlaceholder => "Name this schedule";

    public bool Enabled
    {
        get => _enabled;
        set { if (Set(ref _enabled, value)) Changed(); }
    }

    // ============ the job, problems, dirty, save ============

    /// <summary>The draft as a job definition (what Save would write).</summary>
    public JobDefinition BuildJob()
    {
        return new JobDefinition
        {
            Id = _id,
            Name = _name.Trim(),
            Schedule = Schedule.State.ToSpec(),
            Run = Program.BuildRun(),
            Settings = _settings,
            Enabled = _enabled,
            AgentLock = _agentLock,
        };
    }

    internal static string? RevisionOf(ManagedEntry? entry) => entry?.Task.Actual is null ? null : JobRevision.Of(entry.Task);

    /// <summary>True when the form differs from what was loaded (or last saved).</summary>
    public bool IsDirty => !BuildJob().Equals(_baseline);

    public IReadOnlyList<EditorProblem> Problems => _problems;

    public FieldNote? NameNote => NoteFor("Name");

    public FieldNote? ProgramNote => ProgramNoteValue();

    public FieldNote? ArgsNote => NoteFor("Args");

    public FieldNote? FolderNote => NoteFor("WorkingDirectory");

    public FieldNote? TimeoutNote => Program.TimeoutProblem ?? NoteFor("Timeout");

    public FieldNote? EnvNote => NoteFor("Env");

    FieldNote? NoteFor(string field)
    {
        EditorProblem? p = _problems.Where(x => x.Field == field).OrderBy(x => x.Severity).FirstOrDefault();
        return p is null ? null : new FieldNote(p.Message, p.IsError);
    }

    FieldNote? ProgramNoteValue()
    {
        if (Program.ProgramMessage is { } m) return new FieldNote(m, true);
        // An empty program is the drop zone's job to explain; only real problems show here.
        EditorProblem? p = _problems.Where(x => x.Field == "Program" && !(x.IsError && !Program.HasProgram)).OrderBy(x => x.Severity).FirstOrDefault();
        return p is null ? null : new FieldNote(p.Message, p.IsError);
    }

    /// <summary>The first thing that stops Save, as a sentence; null when Save is possible.</summary>
    public string? BlockingProblem
    {
        get
        {
            if (!Program.HasProgram) return "Choose a program to run, then save.";
            EditorProblem? p = _problems.FirstOrDefault(x => x.IsError);
            if (p is not null) return p.Message;
            if (Program.TimeoutProblem is { } t) return t.Text;
            if (Schedule.TimesProblem is { } tm) return tm.Text;
            if (Schedule.Preview.HasErrors || !Schedule.Preview.IsValid) return Schedule.Preview.Diagnostics.FirstOrDefault(d => d.Severity == CronSeverity.Error)?.Message ?? "The schedule isn't complete.";
            return null;
        }
    }

    public bool HasErrors => BlockingProblem is not null;

    public bool CanSave => !_busy && !Notice.IsLocked && !HasErrors && (IsNew || IsDirty);

    public bool CanDelete => !_busy && IsExisting;

    public bool HasRunsPanel => IsExisting;

    public ICommand ToggleMenuCommand { get; }

    public ICommand CloseMenuCommand { get; }

    public ICommand OpenInTaskSchedulerCommand { get; }

    /// <summary>The ⋯ menu (Run now, Duplicate, Stop managing, Open in Task Scheduler) is open. It is drawn inside the editor, so it also shows in renders.</summary>
    public bool IsMenuOpen { get => _menuOpen; set => Set(ref _menuOpen, value); }

    public bool HasMenu => IsExisting;

    /// <summary>Escape: closes the ⋯ menu when it is open. Returns true when it did.</summary>
    public bool HandleEscape()
    {
        if (!IsMenuOpen) return false;
        IsMenuOpen = false;
        return true;
    }

    /// <summary>
    /// [CP-C] The footer's Delete. Hidden while the banner offers its own Delete (a copy, another user, damaged), so one screen never shows two
    /// Delete buttons: the banner is where that decision is asked.
    /// </summary>
    public bool ShowFooterDelete => IsExisting && !Notice.ShowBannerDelete;

    internal void RaiseBanner()
    {
        Notice.Raise();
        OnPropertyChanged(nameof(HasMenu));
        OnPropertyChanged(nameof(ShowFooterDelete));
    }

    public string FooterText => Notice.IsLocked ? "Saving is off until you decide what to do about the notice above." : BlockingProblem ?? (_planText ?? "");

    public bool FooterIsError => BlockingProblem is not null && Program.HasProgram || !_planOk;

    public string? Status { get => _status; private set { if (Set(ref _status, value)) OnPropertyChanged(nameof(HasStatus)); } }

    public bool HasStatus => _status is not null;

    public bool StatusIsError { get => _statusIsError; private set => Set(ref _statusIsError, value); }

    public bool IsBusy { get => _busy; internal set { if (Set(ref _busy, value)) RaiseCommands(); } }

    public ICommand BackCommand { get; }

    public ICommand SaveCommand => _saveCommand;

    public ICommand DeleteCommand => _deleteCommand;

    /// <summary>Called by every edit: re-checks the form, refreshes the preview and schedules the Planner summary.</summary>
    internal void Changed()
    {
        if (_loading) return;
        Status = null;
        Revalidate();
        _ = SchedulePlanAsync();
    }

    void Revalidate()
    {
        Program.CheckTimeout();
        Schedule.CheckTimes();
        _problems = EditorValidation.Validate(BuildJob());
        Schedule.RefreshPreview();
        RaiseResults();
    }

    void RaiseResults()
    {
        OnPropertyChanged(nameof(Problems));
        OnPropertyChanged(nameof(NameNote));
        OnPropertyChanged(nameof(ProgramNote));
        OnPropertyChanged(nameof(ArgsNote));
        OnPropertyChanged(nameof(FolderNote));
        OnPropertyChanged(nameof(TimeoutNote));
        OnPropertyChanged(nameof(EnvNote));
        OnPropertyChanged(nameof(BlockingProblem));
        OnPropertyChanged(nameof(HasErrors));
        OnPropertyChanged(nameof(FooterText));
        OnPropertyChanged(nameof(FooterIsError));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(CanSave));
        RaiseBanner();
        OnPropertyChanged(nameof(IsExisting));
        OnPropertyChanged(nameof(IsNew));
        OnPropertyChanged(nameof(Heading));
        Notice.RaiseMeta();
        RaiseCommands();
    }

    internal void RaiseCommands()
    {
        _saveCommand.RaiseCanExecuteChanged();
        _deleteCommand.RaiseCanExecuteChanged();
        Runs.RaiseCommands();
        Notice.RaiseCommands();
    }

    // ---- the Planner's inline summary ----

    async Task SchedulePlanAsync()
    {
        int version = ++_planVersion;
        try
        {
            await Task.Delay(PlanDebounce);
            if (version != _planVersion) return;
            await RefreshPlanAsync(version);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (version == _planVersion) SetPlan(false, "Couldn't check the change: " + ex.Message);
        }
    }

    /// <summary>Asks the Planner what Save would do and shows it in the footer (no debounce; tests and Save call this).</summary>
    public Task RefreshPlanAsync() => RefreshPlanAsync(++_planVersion);

    async Task RefreshPlanAsync(int version)
    {
        if (HasErrors) { SetPlan(true, null); return; }
        if (IsExisting && !IsDirty) { SetPlan(true, "No changes to save"); return; }
        SavePlan plan = await _services.PlanSaveAsync(BuildJob(), ExistingTaskName);
        if (version != _planVersion) return;
        SetPlan(plan.Ok, plan.Summary);
    }

    void SetPlan(bool ok, string? text)
    {
        _planOk = ok;
        _planText = text;
        OnPropertyChanged(nameof(FooterText));
        OnPropertyChanged(nameof(FooterIsError));
    }

    // ---- commands ----

    /// <summary>Saves through the services (the Applier), then reloads the saved job. Stays in the editor with an inline confirmation.</summary>
    public async Task SaveAsync()
    {
        if (!CanSave) return;
        IsBusy = true;
        try
        {
            JobDefinition job = BuildJob();
            ApplyResult result = await _services.SaveAsync(job, ExistingTaskName, _revision);
            if (!result.Success)
            {
                if (result.Kind == ApplyFailure.Stale)
                {
                    await HandleStaleAsync(job, result);
                    return;
                }
                Status = result.Message;
                StatusIsError = true;
                return;
            }
            ManagedSnapshot snapshot = await _services.LoadManagedAsync();
            ManagedEntry? saved = FindOwn(snapshot);
            _existing = saved ?? _existing;
            _revision = RevisionOf(saved) ?? _revision;
            _baseline = BuildJob();
            Status = SavedText(job, saved);
            StatusIsError = false;
            if (saved is not null) Runs.ApplyHistory(saved.History);
            Revalidate();
            await RefreshPlanAsync();
            await _host.RefreshList();
        }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// [P23] The job changed while this editor was open (an agent, the command line, another window). A panel inside the window says who and when and offers
    /// "Reload and re-apply my change" (the new version loads and only what I changed is put back on top; both-changed parts say so) or "Keep editing"
    /// (nothing changes; Save refuses again until reloaded). There is no overwrite: that is exactly what the revision prevents.
    /// </summary>
    async Task HandleStaleAsync(JobDefinition mine, ApplyResult result)
    {
        ManagedSnapshot snapshot = await _services.LoadManagedAsync();
        ManagedEntry? live = FindOwn(snapshot);
        string message = live is null ? result.Message : AgentText.StaleMessage(live.Task, _services.Now, _zone);
        int pick = await _dialogs.ChooseAsync(AgentText.StaleTitle, message + "\n\n" + AgentText.StaleExplanation,
            [new PanelChoice(AgentText.KeepEditingButton), new PanelChoice(AgentText.ReloadButton, IsPrimary: true)]);
        if (pick != 1 || live?.Task.Job is not { } liveJob)
        {
            Status = message + " Reload it to save your change.";
            StatusIsError = true;
            return;
        }

        // The live job goes through the same normalization as the form (a throwaway editor), so "what I changed" is compared like with like.
        JobDefinition liveNormalized = new EditorViewModel(_services, _dialogs, EditorHost.None, liveJob, live, false, _now).BuildJob();
        (JobDefinition merged, IReadOnlyList<string> conflicts) = AgentText.Merge(_baseline, mine, liveNormalized);
        var next = new EditorViewModel(_services, _dialogs, _host, merged, live, false, _now, liveNormalized);
        next.Status = AgentText.ReloadedStatus(AgentText.ChangedBy(live.Task), conflicts, !merged.Equals(liveNormalized));
        next.StatusIsError = false;
        if (_host.Replace is { } replace) replace(next);
        else _host.Toast("Reloaded.");
    }

    string SavedText(JobDefinition job, ManagedEntry? saved)
    {
        if (!job.Enabled) return "Saved - this schedule is switched off, so it won't run.";
        DateTimeOffset? next = saved?.Next ?? Cadence.App.UI.Logic.ListFormat.NextRun(job.Schedule, job.Enabled, _now, _zone);
        if (next is { } at) return "Saved - next run " + Cadence.App.UI.Logic.ListFormat.When(at, _now, _zone);
        return Schedule.Preview.RunsAtSignIn ? "Saved - runs when you sign in." : "Saved.";
    }

    /// <summary>Deletes the job after a confirmation, then leaves the editor.</summary>
    public async Task DeleteAsync()
    {
        if (_existing is not { } entry) return;
        bool copy = entry.Task.DuplicateOf is not null;
        bool yes = await _dialogs.ConfirmAsync("Delete \"" + _name.Trim() + "\"?",
            copy ? "This removes this copy from Task Scheduler. The original schedule and its run history are not touched."
                 : entry.Task.Ownership == Ownership.CorruptMetadata ? "This removes the damaged task from Task Scheduler. The program itself is not touched."
                 : "This removes the schedule from Task Scheduler and deletes its run history. The program itself is not touched.", "Delete", danger: true);
        if (!yes) return;
        IsBusy = true;
        try
        {
            ApplyResult result = await _services.DeleteAsync(entry.Task);
            if (!result.Success)
            {
                Status = result.Message;
                StatusIsError = true;
                return;
            }
            await _host.RefreshList();
            _host.Toast("Deleted \"" + _name.Trim() + "\".");
            _host.Close();
        }
        finally { IsBusy = false; }
    }

    /// <summary>Back, Cancel and Escape: asks "Discard changes?" when there are unsaved edits.</summary>
    public async Task RequestCloseAsync()
    {
        try
        {
            if (IsDirty)
            {
                bool discard = await _dialogs.ConfirmAsync("Discard changes?", "You have changes that are not saved.", "Discard", danger: true);
                if (!discard) return;
            }
            _host.Close();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowError(ex);
        }
    }

    internal void ShowError(Exception ex)
    {
        Status = "Something went wrong: " + ex.Message;
        StatusIsError = true;
    }

    /// <summary>This job's entry in a fresh snapshot: by task name first, else by job id (a rename moves the task), never a copy that merely shares the id.</summary>
    internal ManagedEntry? FindOwn(ManagedSnapshot snapshot)
    {
        List<ManagedEntry> same = snapshot.Entries.Where(e => e.Task.Job?.Id == _id).ToList();
        if (same.Count <= 1) return same.FirstOrDefault();
        return same.FirstOrDefault(e => string.Equals(e.Task.Observed.Name, ExistingTaskName, StringComparison.OrdinalIgnoreCase))
            ?? same.FirstOrDefault(e => e.Task.DuplicateOf is null) ?? same[0];
    }

    /// <summary>After a write the saved task is read again: its entry, revision and runs, then the form is re-checked.</summary>
    internal async Task ReloadAfterWriteAsync()
    {
        ManagedSnapshot snapshot = await _services.LoadManagedAsync();
        if (FindOwn(snapshot) is { } fresh)
        {
            _existing = fresh;
            _revision = RevisionOf(fresh) ?? _revision;
            Runs.ApplyHistory(fresh.History);
        }
        Revalidate();
    }

    /// <summary>The unsaved copy a Duplicate opens: the same settings under a new id and the name from <see cref="CopyName"/> (also used by the list's context menu).</summary>
    public static JobDefinition DuplicateDraft(JobDefinition job) => job with { Id = JobId.New(), Name = CopyName(job.Name) };

    /// <summary>"Stock ingest (Dad)" gives "Stock ingest (Dad) (copy)".</summary>
    public static string CopyName(string name) => name.Trim() + " (copy)";

    void OpenInTaskScheduler()
    {
        IsMenuOpen = false;
        if (_existing is not { } entry) return;
        if (_host.OpenInTaskScheduler is { } open) open(entry.Task.Observed.Path);
    }
}
