using System.Windows.Input;
using Cadence.App.UI.Logic;
using Cadence.Mvvm;
using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.Scheduler.Apply;

namespace Cadence.App.UI.ViewModels;

/// <summary>
/// The notice above the editor's form and what hangs on it: drift, copy, newer version and outdated (the banner and its Re-apply, Stop managing, Delete,
/// Open in Task Scheduler and Refresh now actions), Duplicate, the "Created / Updated by whom" line and the lock against AI agents. Owned by
/// <see cref="EditorViewModel"/> (<c>Notice</c>), which supplies the saved entry, the busy flag, the status line and the reload after a write.
/// </summary>
public sealed class EditorNoticeViewModel : ObservableObject
{
    readonly EditorViewModel _root;
    readonly AsyncRelayCommand _reapplyCommand;
    readonly AsyncRelayCommand _untrackCommand;
    readonly AsyncRelayCommand _refreshNowCommand;
    readonly AsyncRelayCommand _duplicateCommand;
    readonly AsyncRelayCommand _toggleAgentLockCommand;

    internal EditorNoticeViewModel(EditorViewModel root)
    {
        _root = root;
        _reapplyCommand = new AsyncRelayCommand(ReapplyAsync, () => CanReapply, root.ShowError);
        _untrackCommand = new AsyncRelayCommand(UntrackAsync, () => CanUntrack, root.ShowError);
        _refreshNowCommand = new AsyncRelayCommand(RefreshNowAsync, () => CanRefreshNow, root.ShowError);
        _duplicateCommand = new AsyncRelayCommand(DuplicateAsync, () => CanDuplicate, root.ShowError);
        _toggleAgentLockCommand = new AsyncRelayCommand(ToggleAgentLockAsync, () => CanToggleAgentLock, root.ShowError);
    }

    /// <summary>What the classification says about this job (null for a new job and for one that is in sync).</summary>
    public BannerState? State => _root.Existing is null ? null : DriftText.For(_root.Existing.Task);

    /// <summary>The Yellow warning above the form (changed outside Cadence, a copy, another user, a newer version). Outdated is not a warning: see <see cref="OutdatedLine"/>.</summary>
    public BannerState? Banner => State is { IsQuiet: false } s ? s : null;

    public bool HasBanner => Banner is not null;

    public string? BannerTitle => Banner?.Title;

    public IReadOnlyList<string> BannerLines => Banner?.Lines ?? [];

    public bool HasBannerLines => BannerLines.Count > 0;

    public string? BannerNote => Banner?.Note;

    public bool HasBannerNote => Banner?.Note is not null;

    public bool BannerIsDamage => Banner?.IsDamage == true;

    public bool ShowReapply => Banner?.Has(BannerAction.Reapply) == true;

    public bool ShowUntrack => Banner?.Has(BannerAction.Untrack) == true;

    public bool ShowBannerDelete => Banner?.Has(BannerAction.Delete) == true;

    public bool ShowBannerOpen => Banner?.Has(BannerAction.OpenInTaskScheduler) == true;

    /// <summary>The quiet "This task needs a quick refresh" line (Dim, with a "Refresh now" link).</summary>
    public bool HasOutdated => State is { Kind: BannerKind.Outdated };

    public string? OutdatedLine => HasOutdated ? State!.Title : null;

    /// <summary>A drifted, copied or newer-version job is shown read-only (it shows Cadence's version) until the banner's decision is made.</summary>
    public bool IsLocked => State?.LocksForm == true;

    public bool FormEnabled => !IsLocked;

    public string? LockedNote => Banner?.Title;

    public bool CanReapply => !_root.IsBusy && ShowReapply && _root.Existing is { Task.Ownership: Ownership.Managed };

    public bool CanUntrack => !_root.IsBusy && _root.Existing is { Task.Ownership: Ownership.Managed };

    public bool CanRefreshNow => !_root.IsBusy && HasOutdated;

    public bool CanDuplicate => !_root.IsBusy && _root.Existing?.Task.Job is not null && _root.Existing.Task.Ownership == Ownership.Managed;

    public ICommand ReapplyCommand => _reapplyCommand;

    public ICommand UntrackCommand => _untrackCommand;

    public ICommand RefreshNowCommand => _refreshNowCommand;

    public ICommand DuplicateCommand => _duplicateCommand;

    internal void Raise()
    {
        foreach (string n in new[]
        {
            nameof(State), nameof(Banner), nameof(HasBanner), nameof(BannerTitle), nameof(BannerLines), nameof(HasBannerLines), nameof(BannerNote), nameof(HasBannerNote),
            nameof(BannerIsDamage), nameof(ShowReapply), nameof(ShowUntrack), nameof(ShowBannerDelete), nameof(ShowBannerOpen), nameof(HasOutdated), nameof(OutdatedLine),
            nameof(IsLocked), nameof(FormEnabled), nameof(LockedNote),
        }) OnPropertyChanged(n);
    }

    internal void RaiseCommands()
    {
        _reapplyCommand.RaiseCanExecuteChanged();
        _untrackCommand.RaiseCanExecuteChanged();
        _refreshNowCommand.RaiseCanExecuteChanged();
        _duplicateCommand.RaiseCanExecuteChanged();
    }

    // ============ [P23] attribution and the lock against AI agents ============

    /// <summary>"Created 1 Oct 2026 by Claude Code · Updated 2 h ago in the app" (+ changed in Task Scheduler since, + Locked for AI agents). Null for a new schedule.</summary>
    public string? MetaLine => AgentText.EditorMetaLine(_root.Existing?.Task, _root.AgentLock, _root.Now, _root.Zone);

    public bool HasMetaLine => MetaLine is not null;

    /// <summary>The owner's lock: AI agents can't change, run or delete this job.</summary>
    public bool IsAgentLocked => _root.AgentLock;

    /// <summary>The ⋯ menu's check item. A save of its own, so only a saved, in-sync, own job (not a copy, a drifted or a damaged one).</summary>
    public bool CanToggleAgentLock => !_root.IsBusy && _root.Existing?.Task is { Ownership: Ownership.Managed, Job: not null, DuplicateOf: null } t && t.Sync != SyncStatus.Drifted && !IsLocked;

    public ICommand ToggleAgentLockCommand => _toggleAgentLockCommand;

    /// <summary>Locks or unlocks the saved job against AI agents. Your unsaved edits stay in the form untouched.</summary>
    public async Task ToggleAgentLockAsync()
    {
        if (!CanToggleAgentLock || _root.Existing is not { } entry) return;
        _root.IsMenuOpen = false;
        bool target = !_root.AgentLock;
        _root.IsBusy = true;
        try
        {
            ApplyResult result = await _root.Services.SetAgentLockAsync(entry.Task, target);
            if (!result.Success)
            {
                _root.SetStatus(result.Message, isError: true);
                return;
            }
            _root.AgentLock = target;
            _root.Baseline = _root.Baseline with { AgentLock = target };
            await _root.ReloadAfterWriteAsync();
            _root.SetStatus(target ? "Locked. AI agents can't change this schedule." : "Unlocked. AI agents may change this schedule again.", isError: false);
            RaiseMeta();
            await _root.Host.RefreshList();
        }
        finally { _root.IsBusy = false; }
    }

    internal void RaiseMeta()
    {
        OnPropertyChanged(nameof(MetaLine));
        OnPropertyChanged(nameof(HasMetaLine));
        OnPropertyChanged(nameof(IsAgentLocked));
        OnPropertyChanged(nameof(CanToggleAgentLock));
        _toggleAgentLockCommand.RaiseCanExecuteChanged();
    }

    // ============ banner actions ============

    /// <summary>Re-apply Cadence's version of a drifted job, after a confirmation that lists what will be overwritten.</summary>
    public async Task ReapplyAsync()
    {
        if (!CanReapply || _root.Existing is not { } entry) return;
        ConfirmText ask = DriftText.ReapplyConfirm(entry.Task);
        if (!await _root.Dialogs.ConfirmAsync(ask.Title, ask.Message, ask.Confirm, danger: false)) return;
        _root.IsBusy = true;
        try
        {
            ApplyResult result = await _root.Services.ReapplyAsync(entry.Task.Observed.Name);
            if (!result.Success)
            {
                _root.SetStatus(result.Message, isError: true);
                return;
            }
            await _root.ReloadAfterWriteAsync();
            _root.SetStatus("Re-applied. The schedule is back to what Cadence saved.", isError: false);
            await _root.Host.RefreshList();
        }
        finally { _root.IsBusy = false; _root.RaiseBanner(); _root.RaiseCommands(); }
    }

    /// <summary>Stop managing (Untrack): the task stays in Task Scheduler as a plain task. Leaves the editor on success.</summary>
    public async Task UntrackAsync()
    {
        if (!CanUntrack || _root.Existing is not { } entry) return;
        _root.IsMenuOpen = false;
        ConfirmText ask = DriftText.UntrackConfirm(entry.Task);
        if (!await _root.Dialogs.ConfirmAsync(ask.Title, ask.Message, ask.Confirm, danger: true)) return;
        _root.IsBusy = true;
        try
        {
            ApplyResult result = await _root.Services.UntrackAsync(entry.Task.Observed.Name);
            if (!result.Success)
            {
                _root.SetStatus(result.Message, isError: true);
                return;
            }
            await _root.Host.RefreshList();
            _root.Host.Toast("Cadence no longer manages \"" + _root.Name.Trim() + "\". The task stays in Task Scheduler.");
            _root.Host.Close();
        }
        finally { _root.IsBusy = false; }
    }

    /// <summary>"Refresh now" on the Outdated line: writes the task again in this version's format (the job itself stays the same).</summary>
    public async Task RefreshNowAsync()
    {
        if (!CanRefreshNow || _root.Existing is not { } entry || entry.Task.Job is not { } job) return;
        _root.IsBusy = true;
        try
        {
            ApplyResult result = await _root.Services.SaveAsync(job, entry.Task.Observed.Name, EditorViewModel.RevisionOf(entry));
            if (!result.Success)
            {
                _root.SetStatus(result.Message, isError: true);
                return;
            }
            await _root.ReloadAfterWriteAsync();
            _root.SetStatus("Refreshed. The task is up to date.", isError: false);
            await _root.RefreshPlanAsync();
            await _root.Host.RefreshList();
        }
        finally { _root.IsBusy = false; _root.RaiseBanner(); _root.RaiseCommands(); }
    }

    /// <summary>Duplicate: opens a new, unsaved editor with this schedule's saved settings under a new name and id.</summary>
    public async Task DuplicateAsync()
    {
        if (!CanDuplicate || _root.Existing?.Task.Job is not { } job) return;
        _root.IsMenuOpen = false;
        if (_root.IsDirty && !await _root.Dialogs.ConfirmAsync("Duplicate the saved version?",
                "You have changes that are not saved. The copy is made from the saved version of this schedule, and your changes here are discarded.", "Duplicate", danger: false))
            return;
        JobDefinition copy = EditorViewModel.DuplicateDraft(job);
        if (_root.Host.OpenDraft is { } open) open(copy);
        else _root.Host.Toast("Duplicating isn't available here.");
    }
}
