using System.Globalization;
using System.IO;
using System.Windows.Input;
using Cadence.Agent.Audit;
using Cadence.Agent.Policy;
using Cadence.App.UI.Logic;
using Cadence.App.UI.Services;
using Cadence.Mvvm;

namespace Cadence.App.UI.ViewModels;

/// <summary>One button of a <see cref="ChoicePanelViewModel"/>.</summary>
public sealed class ChoiceButtonViewModel(string label, bool primary, bool danger, ICommand command)
{
    public string Label => label;

    public bool IsPrimary => primary;

    public bool IsDanger => danger;

    public bool IsPlain => !primary && !danger;

    public ICommand Command => command;
}

/// <summary>
/// A question drawn INSIDE the window (never a separate window: the owner's tiling window manager would pull one into their layout, ground rule 11).
/// <see cref="Result"/> completes with the index of the chosen button, or -1 when the panel was dismissed (Esc, a click outside).
/// </summary>
public sealed class ChoicePanelViewModel
{
    readonly TaskCompletionSource<int> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ChoicePanelViewModel(string title, string message, IReadOnlyList<PanelChoice> choices)
    {
        Title = title;
        Message = message;
        Buttons = choices.Select((c, i) => new ChoiceButtonViewModel(c.Label, c.IsPrimary, c.IsDanger, new RelayCommand(() => Complete(i)))).ToList();
    }

    public string Title { get; }

    public string Message { get; }

    public IReadOnlyList<ChoiceButtonViewModel> Buttons { get; }

    public Task<int> Result => _done.Task;

    public bool IsDone => _done.Task.IsCompleted;

    public void Complete(int index) => _done.TrySetResult(index);

    public void Dismiss() => Complete(-1);
}

/// <summary>The "Agent activity" panel (opened from the ⋯ menu): the newest agent log lines, newest first, plain words, the file's path and "Open log folder".</summary>
public sealed class AgentActivityViewModel : ObservableObject
{
    readonly IAppServices _services;
    readonly Action _close;
    readonly Func<string, bool> _openFolder;
    IReadOnlyList<ActivityRow> _rows = [];
    string _header = "";
    bool _loaded;
    string? _message;

    public AgentActivityViewModel(IAppServices services, Action close, Func<string, bool> openFolder)
    {
        _services = services;
        _close = close;
        _openFolder = openFolder;
        CloseCommand = new RelayCommand(_close);
        OpenFolderCommand = new RelayCommand(OpenFolder);
    }

    public ICommand CloseCommand { get; }

    public ICommand OpenFolderCommand { get; }

    public string Path => _services.AgentLogPath;

    public IReadOnlyList<ActivityRow> Rows { get => _rows; private set { _rows = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasRows)); OnPropertyChanged(nameof(IsEmpty)); } }

    public bool HasRows => _rows.Count > 0;

    public bool IsEmpty => _loaded && _rows.Count == 0;

    public string EmptyText => ActivityText.EmptyText;

    public string Header { get => _header; private set => Set(ref _header, value); }

    public string? Message { get => _message; private set { if (Set(ref _message, value)) OnPropertyChanged(nameof(HasMessage)); } }

    public bool HasMessage => _message is not null;

    /// <summary>Reads the log on the thread pool (one more line than shown tells whether older entries exist).</summary>
    public async Task LoadAsync()
    {
        IReadOnlyList<AuditEntry> entries = await _services.LoadAgentLogAsync(ActivityText.MaxRows + 1).ConfigureAwait(true);
        IReadOnlyList<ActivityRow> rows = ActivityText.Rows(entries, _services.Now, _services.Zone);
        _loaded = true;
        Header = rows.Count == 0 ? "" : ActivityText.Header(rows.Count, entries.Count);
        Rows = rows;
    }

    void OpenFolder()
    {
        if (!_openFolder(_services.AgentLogFolder)) Message = "There is no agent log folder yet. It appears the first time an agent uses Cadence.";
    }
}

/// <summary>The agent limits (shortest schedule an agent may make, most jobs it may have): edited here or with <c>Cadence.exe --agents config</c>.</summary>
public sealed class AgentLimitsViewModel : ObservableObject
{
    readonly IAppServices _services;
    readonly Action _close;
    readonly Action<string> _toast;
    readonly Func<Task> _refresh;
    string _minInterval;
    string _maxJobs;
    string? _error;
    bool _busy;

    public AgentLimitsViewModel(IAppServices services, AgentPolicy policy, Action close, Action<string> toast, Func<Task> refresh)
    {
        _services = services;
        _close = close;
        _toast = toast;
        _refresh = refresh;
        _minInterval = policy.MinIntervalMinutes.ToString(CultureInfo.InvariantCulture);
        _maxJobs = policy.MaxAgentJobs.ToString(CultureInfo.InvariantCulture);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !_busy);
        CloseCommand = new RelayCommand(_close);
    }

    public ICommand SaveCommand { get; }

    public ICommand CloseCommand { get; }

    public string MinInterval { get => _minInterval; set { if (Set(ref _minInterval, value)) Error = null; } }

    public string MaxJobs { get => _maxJobs; set { if (Set(ref _maxJobs, value)) Error = null; } }

    public string? Error { get => _error; private set { if (Set(ref _error, value)) OnPropertyChanged(nameof(HasError)); } }

    public bool HasError => _error is not null;

    public string CliHint => AgentLimitsText.CliHint;

    async Task SaveAsync()
    {
        if (AgentLimitsText.Validate(_minInterval, _maxJobs, out int min, out int max) is { } problem)
        {
            Error = problem;
            return;
        }
        _busy = true;
        try
        {
            await _services.SetAgentLimitsAsync(min, max).ConfigureAwait(true);
            _toast(AgentLimitsText.SavedToast(min, max));
            _close();
            await _refresh().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            Error = "Cadence couldn't save the limits (" + ex.GetType().Name + "). Nothing was changed.";
        }
        finally { _busy = false; }
    }
}

/// <summary>The words and checks of the limits panel (pure).</summary>
public static class AgentLimitsText
{
    public const string Title = "What AI agents may do";
    public const string MinLabel = "Shortest schedule an agent may make";
    public const string MinHelp = "Agents can't make a schedule that runs more often than this (in minutes, 1 to 1440).";
    public const string MaxLabel = "Most schedules an agent may have";
    public const string MaxHelp = "Counts the schedules agents created. 0 means they can't create any (0 to 500).";
    public const string CliLead = "The command line sets the same two things:";
    public const string CliCommand = "Cadence.exe --agents config --min-interval 5 --max-jobs 20";
    public const string CliHint = CliLead + " " + CliCommand;

    /// <summary>Null when both numbers are fine, else a plain sentence.</summary>
    public static string? Validate(string? minText, string? maxText, out int min, out int max)
    {
        min = 0;
        max = 0;
        if (!int.TryParse(minText?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out min) || !AgentPolicy.IsValidMinInterval(min))
            return "The shortest schedule has to be a whole number of minutes from " + AgentPolicy.MinIntervalLowest.ToString(CultureInfo.InvariantCulture) + " to " + AgentPolicy.MinIntervalHighest.ToString(CultureInfo.InvariantCulture) + ".";
        if (!int.TryParse(maxText?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out max) || !AgentPolicy.IsValidMaxAgentJobs(max))
            return "The most schedules has to be a whole number from " + AgentPolicy.MaxAgentJobsLowest.ToString(CultureInfo.InvariantCulture) + " to " + AgentPolicy.MaxAgentJobsHighest.ToString(CultureInfo.InvariantCulture) + ".";
        return null;
    }

    public static string SavedToast(int min, int max) =>
        "Saved. Agents: every " + min.ToString(CultureInfo.InvariantCulture) + " min at the shortest, up to " + max.ToString(CultureInfo.InvariantCulture) + " schedules.";
}
