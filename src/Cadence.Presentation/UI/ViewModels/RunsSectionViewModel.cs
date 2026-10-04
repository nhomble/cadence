using System.Windows.Input;
using Cadence.App.UI.Logic;
using Cadence.App.UI.Services;
using Cadence.Mvvm;
using Cadence.Core.History;
using Cadence.Core.Model;
using Cadence.Core.TaskXml;
using Cadence.Core.Text;
using Cadence.History;

namespace Cadence.App.UI.ViewModels;

/// <summary>
/// The runs panel of the editor (a saved job only): the recent runs and their numbers, "Show more", the live refresh while a run goes on, Run now and the
/// line under it, and the could-not-start notice. Owned by <see cref="EditorViewModel"/> (<c>Runs</c>), which also supplies the saved entry, the busy flag
/// and the status line; everything that reaches Task Scheduler or the disk goes through <see cref="IAppServices"/>.
/// </summary>
public sealed class RunsSectionViewModel : ObservableObject
{
    public const int PageSize = 20;

    readonly EditorViewModel _root;
    readonly AsyncRelayCommand _runNowCommand;
    readonly AsyncRelayCommand _showMoreCommand;
    int _runVersion;
    int _loadedRuns = PageSize;
    bool _refreshingRuns;
    int _watchTicksLeft;
    DateTimeOffset? _runStartedAt;
    string? _runFeedback;
    bool _runFeedbackIsError;
    IReadOnlyList<RunRowViewModel> _runs = [];
    JobSummary _summary = JobSummary.Empty;

    internal RunsSectionViewModel(EditorViewModel root)
    {
        _root = root;
        _runNowCommand = new AsyncRelayCommand(RunNowAsync, () => CanRunNow, root.ShowError);
        _showMoreCommand = new AsyncRelayCommand(ShowMoreAsync, () => HasMoreRuns, root.ShowError);
        OpenLogFolderCommand = new RelayCommand(() => { if (!root.Host.OpenFolder(root.Services.RunsFolderPath)) root.Host.Toast("No run history yet."); });
        CopyRunCommand = new RelayCommand(o => { if (o is RunRowViewModel r) root.Host.CopyText(r.CopyText); });
        OpenSecurityCommand = new RelayCommand(() => root.Host.OpenLink?.Invoke(WindowsSecurityLink), () => true);
    }

    public IReadOnlyList<RunRowViewModel> RunRows => _runs;

    public bool HasRunRows => _runs.Count > 0;

    public bool NoRuns => _runs.Count == 0;

    public string NoRunsText => _root.Existing?.Task.DuplicateOf is not null
        ? "A copy has no run history of its own: the original keeps it."
        : "No runs yet - use Run now to try it.";

    public string AverageText => EditorFormat.AverageText(_summary);

    public string SuccessText => EditorFormat.SuccessText(_summary);

    public string SuccessDetail => EditorFormat.SuccessDetail(_summary);

    public string AverageTooltip => "The average time the program took, over the Ok and Failed runs among the last 50. Time-outs and runs that couldn't start are left out.";

    public string SuccessTooltip => "Runs that finished Ok, as a share of the last 50 runs that counted. Holiday skips don't count; time-outs, failures and runs that couldn't start do.";

    public bool HasMoreRuns => _runs.Count >= _loadedRuns;

    public ICommand RunNowCommand => _runNowCommand;

    public ICommand ShowMoreCommand => _showMoreCommand;

    public ICommand OpenLogFolderCommand { get; }

    public ICommand CopyRunCommand { get; }

    public ICommand OpenSecurityCommand { get; }

    public bool CanRunNow => !_root.IsBusy && _root.IsExisting && !_root.Notice.IsLocked && !IsRunning;

    internal void RaiseCommands()
    {
        _runNowCommand.RaiseCanExecuteChanged();
        _showMoreCommand.RaiseCanExecuteChanged();
    }

    internal void ApplyHistory(JobHistory history)
    {
        EditorViewModel r = _root;
        _summary = history.Summary;
        _runs = history.Recent.Select(x => new RunRowViewModel(x, EditorFormat.Row(x, r.Now, r.Zone), EditorFormat.Detail(x, r.Now, r.Zone), r.Id, r.Services, () => r.Host.OpenLink?.Invoke(WindowsSecurityLink))).ToList();
        OnPropertyChanged(nameof(RunRows));
        OnPropertyChanged(nameof(HasRunRows));
        OnPropertyChanged(nameof(NoRuns));
        OnPropertyChanged(nameof(AverageText));
        OnPropertyChanged(nameof(SuccessText));
        OnPropertyChanged(nameof(SuccessDetail));
        OnPropertyChanged(nameof(HasMoreRuns));
        r.RaiseHasRunsPanel();
        RaiseLive();
        r.RaiseBanner();
        UpdateRunFeedback();
        r.RaiseCommands();
    }

    async Task ShowMoreAsync()
    {
        if (_root.Existing is not { } entry) return;
        _loadedRuns += PageSize;
        JobHistory h = await _root.Services.LoadHistoryAsync(entry.Task, _loadedRuns);
        ApplyHistory(h);
    }

    /// <summary>Reloads the recent runs and the task's state (after Run now, on the live timer, or on demand).</summary>
    public async Task RefreshRunsAsync()
    {
        if (_root.Existing is null) return;
        int version = ++_runVersion;
        ManagedSnapshot snapshot = await _root.Services.LoadManagedAsync();
        ManagedEntry? fresh = _root.FindOwn(snapshot);
        if (fresh is null || version != _runVersion) return;
        _root.Existing = fresh;
        JobHistory h = await _root.Services.LoadHistoryAsync(fresh.Task, _loadedRuns);
        if (version == _runVersion) ApplyHistory(h);
    }

    // ---- live refresh and Run now ----

    /// <summary>How often the view asks <see cref="LiveTickAsync"/> to look again while a run is going.</summary>
    public static readonly TimeSpan LivePollInterval = TimeSpan.FromSeconds(3);

    /// <summary>After "Run now" the list is watched for this many ticks (about 30 s) waiting for the new row to appear and finish.</summary>
    public const int WatchTicks = 10;

    public const string WindowsSecurityLink = "windowsdefender://";

    /// <summary>The newest row says Running: the live indicator shows and Run now is off (Task Scheduler ignores a second start).</summary>
    public bool IsRunning => _runs.Count > 0 && _runs[0].Run.Status == RunStatus.Running;

    /// <summary>The view's 3 s timer should look again: a run is going, or Run now was just pressed and its row has not shown up yet.</summary>
    public bool NeedsLivePoll => _root.IsExisting && (IsRunning || _watchTicksLeft > 0);

    /// <summary>One line under the Run now button: "Started...", "Running now...", "Finished: Ok · 12 s", "Windows refused to start this job: access denied (0x80070005). ...".</summary>
    public string? RunFeedback { get => _runFeedback; private set { if (Set(ref _runFeedback, value)) OnPropertyChanged(nameof(HasRunFeedback)); } }

    public bool HasRunFeedback => _runFeedback is not null;

    public bool RunFeedbackIsError { get => _runFeedbackIsError; private set => Set(ref _runFeedbackIsError, value); }

    /// <summary>One timer tick: refreshes the runs when <see cref="NeedsLivePoll"/> (and no refresh is already in flight). Safe to call at any time.</summary>
    public async Task LiveTickAsync()
    {
        if (!NeedsLivePoll || _refreshingRuns) return;
        _refreshingRuns = true;
        try
        {
            await RefreshRunsAsync();
            if (IsRunning) _watchTicksLeft = 0;
            else if (_watchTicksLeft > 0) _watchTicksLeft--;
            UpdateRunFeedback();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _watchTicksLeft = 0;
            RunFeedback = "Couldn't refresh the runs: " + ex.Message;
            RunFeedbackIsError = true;
        }
        finally
        {
            _refreshingRuns = false;
            RaiseLive();
        }
    }

    void RaiseLive()
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(NeedsLivePoll));
        OnPropertyChanged(nameof(HasBlockedNotice));
        OnPropertyChanged(nameof(BlockedTitle));
        OnPropertyChanged(nameof(BlockedBody));
        OnPropertyChanged(nameof(ShowSecurityLink));
        OnPropertyChanged(nameof(BlockedWhen));
        _runNowCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Works out what the "I pressed Run now" line should say from the rows that are there now.</summary>
    void UpdateRunFeedback()
    {
        if (_runStartedAt is not { } started) return;
        RunRowViewModel? mine = _runs.FirstOrDefault(r => r.Run.When >= started - TimeSpan.FromSeconds(10));
        if (mine is null)
        {
            RunFeedback = _watchTicksLeft > 0
                ? "Started. Waiting for Task Scheduler to start it..."
                : "Started, but Task Scheduler hasn't reported a run yet. Reopen this schedule in a moment to look again.";
            RunFeedbackIsError = false;
            return;
        }
        if (mine.Run.Status == RunStatus.Running)
        {
            RunFeedback = "Running now. This list updates by itself.";
            RunFeedbackIsError = false;
            return;
        }
        _runStartedAt = null;
        _watchTicksLeft = 0;
        RunFeedback = EditorFormat.FinishedText(mine.Run, mine.Text);
        RunFeedbackIsError = mine.Tone is BadgeKind.Failed or BadgeKind.TimedOut or BadgeKind.NotReported;
    }

    // ---- the could-not-start notice (Checkpoint B, Finding A; owner rule: state the code, never guess the cause) ----

    /// <summary>What Windows said, from the code alone: "Windows refused to start this job: access denied (0x80070005)."</summary>
    public string BlockedTitle => BlockedRow?.Run.Message ?? "";

    /// <summary>For access denied: the labelled possibilities, then the retry hint. For other launch failures just the retry hint.</summary>
    public string BlockedBody => (BlockedRow is { IsBlocked: true } ? TaskResultCodes.AccessDeniedPossibilities + " " : "") + "The job did not run. Press Run now to try again.";

    /// <summary>A neutral shortcut that only opens the Windows Security app; it does not claim that anything there is the cause.</summary>
    public const string BlockedLinkText = "Open Windows Security";

    /// <summary>The "Open Windows Security" shortcut shows for access denied only.</summary>
    public bool ShowSecurityLink => BlockedRow is { IsBlocked: true };

    /// <summary>The newest run that counted is a launch Windows could not start: the runs panel states it above the list.</summary>
    public bool HasBlockedNotice => BlockedRow is not null;

    RunRowViewModel? BlockedRow
    {
        get
        {
            RunRowViewModel? newest = _runs.FirstOrDefault(r => r.Run.Status is not (RunStatus.Skipped or RunStatus.Running));
            return newest is not null && EditorFormat.IsLaunchFailureRun(newest.Run) ? newest : null;
        }
    }

    public string BlockedWhen => BlockedRow is { } r ? "Last tried " + r.Text.When : "";

    public async Task RunNowAsync()
    {
        if (_root.Existing is not { } entry || !CanRunNow) return;
        _root.IsMenuOpen = false;
        _runStartedAt = _root.Services.Now;
        RunFeedback = "Starting...";
        RunFeedbackIsError = false;
        string? error = await _root.Services.RunNowAsync(entry.Task.Observed.Name);
        if (error is not null)
        {
            _runStartedAt = null;
            RunFeedback = null;
            _root.SetStatus(error, isError: true);
            return;
        }
        _root.Host.Toast("Started \"" + _root.Name.Trim() + "\".");
        _watchTicksLeft = WatchTicks;
        RunFeedback = "Started. Waiting for Task Scheduler to start it...";
        // One look right away; the view's timer (LiveTickAsync) keeps looking while the run goes on.
        await RefreshRunsAsync();
        RaiseLive();
    }
}
