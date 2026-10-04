using System.IO;
using System.Collections.ObjectModel;
using System.Globalization;
using Cadence.App.UI.Logic;
using Cadence.App.UI.Services;
using Cadence.Mvvm;
using Cadence.Core.Planning;
using Cadence.Core.Text;
using Cadence.Scheduler;

namespace Cadence.App.UI.ViewModels;

/// <summary>What the table's context menu and toolbar ask the window to do beyond the list itself.</summary>
public sealed record ListActions(Action<ClassifiedTask>? Duplicate = null, Action<string>? OpenInTaskScheduler = null, Action<string>? CopyText = null,
    Action<Cadence.Core.Model.RunSpec>? RevealProgram = null);

/// <summary>
/// The home screen: the schedule table, its sort, filter and selection, the empty state and the foreign ("other tasks") table. Owns loading and refreshing:
/// the managed folder is read on a background thread (never on the UI thread), rows are described (and their rate counted) on the thread pool, the other
/// tasks only when the toggle is on and then cached. The selection is kept by task name, so a refresh does not lose it.
/// </summary>
public sealed class ListPageViewModel : ObservableObject
{
    readonly IAppServices _services;
    readonly IDialogs _dialogs;
    readonly Action<ScheduleRowViewModel> _open;
    readonly Action<string> _toast;
    readonly Action<ClassifiedTask>? _openForeign;
    readonly ListActions _actions;
    readonly RateCache _rates = new();

    bool _busy;
    bool _pending;
    bool _pendingOthers;
    bool _isLoading = true;
    bool _hasLoaded;
    bool _showForeign;
    bool _foreignExpanded;
    bool _foreignLoading;
    string? _error;
    string _foreignHeader = "";
    string _filter = "";
    SortSpec _sort;
    IReadOnlySet<GridColumn> _hidden;
    List<(string Task, RowText Text)> _signature = [];
    List<(string Path, ForeignText Text)> _foreignSignature = [];
    List<ScheduleRowViewModel> _allRows = [];
    List<(ForeignRowViewModel Row, ClassifiedTask Task)> _foreignAll = [];
    HashSet<string> _selectedNames = new(StringComparer.OrdinalIgnoreCase);
    List<ScheduleRowViewModel> _selected = [];
    bool _rebuilding;
    int _foreignGeneration;
    IReadOnlyList<ClassifiedTask> _foreignInFolder = [];
    bool _prunedThisStart;

    public ListPageViewModel(IAppServices services, IDialogs dialogs, Action<ScheduleRowViewModel> open, Action<string> toast, Action<ClassifiedTask>? openForeign = null,
        ListActions? actions = null)
    {
        _services = services;
        _dialogs = dialogs;
        _open = open;
        _toast = toast;
        _openForeign = openForeign;
        _actions = actions ?? new ListActions();
        _showForeign = services.Settings.ShowForeign;
        _foreignExpanded = _showForeign;
        _sort = SortSpec.Parse(services.Settings.ListSort);
        _hidden = GridLogic.ParseHidden(services.Settings.ListHiddenColumns);
        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(refreshOthers: true));
        SortCommand = new RelayCommand(o => { if (o is GridColumn column) SortBy(column); });
        OpenSelectedCommand = new RelayCommand(OpenSelected, () => _selected.Count == 1);
        RunSelectedCommand = new AsyncRelayCommand(() => RunRowsAsync(_selected.Where(r => r.CanRun).ToList()), () => _selected.Any(r => r.CanRun));
        EnableSelectedCommand = new AsyncRelayCommand(() => SetEnabledRowsAsync(_selected.Where(r => r.CanToggle && !r.IsEnabled).ToList(), true), () => _selected.Any(r => r.CanToggle && !r.IsEnabled));
        DisableSelectedCommand = new AsyncRelayCommand(() => SetEnabledRowsAsync(_selected.Where(r => r.CanToggle && r.IsEnabled).ToList(), false), () => _selected.Any(r => r.CanToggle && r.IsEnabled));
        DeleteSelectedCommand = new AsyncRelayCommand(() => DeleteRowsAsync(_selected.ToList()), () => _selected.Count > 0);
        DuplicateSelectedCommand = new RelayCommand(DuplicateSelected, () => _selected.Count == 1 && _selected[0].CanDuplicate && _actions.Duplicate is not null);
        OpenInTaskSchedulerSelectedCommand = new RelayCommand(() => { if (_selected.Count == 1) _actions.OpenInTaskScheduler?.Invoke(_selected[0].TaskPath); }, () => _selected.Count == 1 && _actions.OpenInTaskScheduler is not null);
        CopyPathSelectedCommand = new RelayCommand(() => { if (_selected.Count == 1) _actions.CopyText?.Invoke(_selected[0].TaskPath); }, () => _selected.Count == 1 && _actions.CopyText is not null);
        OpenFolderSelectedCommand = new RelayCommand(() => { if (_selected.Count == 1 && _selected[0].Classified.Job is { } job) _actions.RevealProgram?.Invoke(job.Run); },
            () => _selected.Count == 1 && _selected[0].Classified.Job is not null && _actions.RevealProgram is not null);
        ClearFilterCommand = new RelayCommand(() => FilterText = "");
        ToggleColumnCommand = new RelayCommand(o => { if (o is GridColumn column) ToggleColumn(column); });
    }

    /// <summary>The rows shown: the schedules that match the filter, in the sort order. (All rows, whatever the filter, are counted in <see cref="StatusText"/>.)</summary>
    public ObservableCollection<ScheduleRowViewModel> Rows { get; } = [];

    public ObservableCollection<ForeignRowViewModel> ForeignRows { get; } = [];

    public System.Windows.Input.ICommand RefreshCommand { get; }

    /// <summary>The template cards (set by the owner; the + sheet shows them until Phase 25.4 replaces it).</summary>
    public IReadOnlyList<TemplateCardViewModel> Cards { get; set; } = [];

    /// <summary>Set by the owner: opens Task Scheduler (the foreign section's one action).</summary>
    public System.Windows.Input.ICommand? OpenTaskSchedulerCommand { get; set; }

    // ---- state of the screen ----

    /// <summary>True until the first load finished (the table body says "Reading Task Scheduler…").</summary>
    public bool IsLoading { get => _isLoading; private set { if (Set(ref _isLoading, value)) RaiseState(); } }

    public bool HasLoaded { get => _hasLoaded; private set { if (Set(ref _hasLoaded, value)) RaiseState(); } }

    /// <summary>Loaded and no schedule at all: the table shows one line and the New button.</summary>
    public bool IsEmpty => HasLoaded && _allRows.Count == 0;

    public bool HasRows => _allRows.Count > 0;

    /// <summary>A filter hides every schedule: the table body says so and offers to clear it.</summary>
    public bool NoMatches => HasLoaded && _allRows.Count > 0 && Rows.Count == 0;

    public string? Error { get => _error; private set { if (Set(ref _error, value)) OnPropertyChanged(nameof(HasError)); } }

    public bool HasError => _error is not null;

    void RaiseState()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(NoMatches));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(Counts));
        OnPropertyChanged(nameof(NoMatchText));
    }

    // ---- counts ----

    /// <summary>Total, problems and disabled over all schedules (not only the ones the filter shows).</summary>
    public (int Total, int Problems, int Disabled) Counts => GridLogic.Counts(_allRows.Select(r => r.Text));

    /// <summary>The status bar's left text ("8 schedules · 4 problems · 1 disabled · showing 3").</summary>
    public string StatusText
    {
        get
        {
            (int total, int problems, int disabled) = Counts;
            return GridLogic.StatusText(HasLoaded, total, problems, disabled, _filter.Trim().Length > 0 ? Rows.Count : null);
        }
    }

    /// <summary>How many rates were really counted so far (test hook; tests: a refresh with nothing new counts nothing).</summary>
    public int RateComputations => _rates.Computations;

    public string NoMatchText => "No schedules match \"" + _filter.Trim() + "\".";

    // ---- sort, filter, columns ----

    public SortSpec Sort => _sort;

    public GridColumn SortColumn => _sort.Column;

    public bool SortDescending => _sort.Descending;

    public System.Windows.Input.ICommand SortCommand { get; }

    /// <summary>A header click: the same column flips, another column starts in its own direction. Remembered for the next start.</summary>
    public void SortBy(GridColumn column)
    {
        _sort = GridLogic.Toggle(_sort, column);
        _services.Settings.ListSort = _sort.Format();
        OnPropertyChanged(nameof(Sort));
        OnPropertyChanged(nameof(SortColumn));
        OnPropertyChanged(nameof(SortDescending));
        Reapply();
        ReapplyForeign();
    }

    public string FilterText
    {
        get => _filter;
        set
        {
            value ??= "";
            if (!Set(ref _filter, value)) return;
            Reapply();
            ReapplyForeign();
            OnPropertyChanged(nameof(NoMatchText));
        }
    }

    public System.Windows.Input.ICommand ClearFilterCommand { get; }

    /// <summary>Ctrl+F: the view moves the keyboard focus into the filter box.</summary>
    public event EventHandler? FocusFilterRequested;

    public void RequestFocusFilter() => FocusFilterRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>The columns the user hid from the header menu (Name can't be hidden).</summary>
    public IReadOnlySet<GridColumn> HiddenColumns => _hidden;

    public System.Windows.Input.ICommand ToggleColumnCommand { get; }

    public event EventHandler? ColumnsChanged;

    public void ToggleColumn(GridColumn column)
    {
        if (column == GridColumn.Name) return;
        var set = new HashSet<GridColumn>(_hidden);
        if (!set.Add(column)) set.Remove(column);
        _hidden = set;
        _services.Settings.ListHiddenColumns = GridLogic.FormatHidden(set);
        OnPropertyChanged(nameof(HiddenColumns));
        ColumnsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- selection ----

    /// <summary>The selected rows, in table order. The view reports a change with <see cref="SetSelection"/>; a refresh restores it by task name.</summary>
    public IReadOnlyList<ScheduleRowViewModel> SelectedRows => _selected;

    public int SelectedCount => _selected.Count;

    /// <summary>Raised just before the rows are rebuilt (the view notes whether the keyboard focus is in the table, to put it back).</summary>
    public event EventHandler? RowsRebuilding;

    /// <summary>Esc or Down in the filter box: the view moves the keyboard focus into the table.</summary>
    public event EventHandler? FocusTableRequested;

    public void RequestFocusTable() => FocusTableRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Raised after the rows were rebuilt: the view re-selects <see cref="SelectedRows"/> (the selection survives a refresh, a sort and a filter change).</summary>
    public event EventHandler? SelectionRestoreRequested;

    /// <summary>The view reports its selection. Ignored while the rows are being rebuilt (the view's own selection changes then are not the user's).</summary>
    public void SetSelection(IEnumerable<ScheduleRowViewModel> rows)
    {
        if (_rebuilding) return;
        var chosen = new HashSet<ScheduleRowViewModel>(rows);
        _selected = Rows.Where(chosen.Contains).ToList();
        _selectedNames = new HashSet<string>(_selected.Select(r => r.Classified.Observed.Name), StringComparer.OrdinalIgnoreCase);
        RaiseSelection();
    }

    /// <summary>Selects the rows with these task names (render mode and tests); the view follows through <see cref="SelectionRestoreRequested"/>.</summary>
    public void Select(IEnumerable<string> taskNames)
    {
        _selectedNames = new HashSet<string>(taskNames, StringComparer.OrdinalIgnoreCase);
        _selected = Rows.Where(r => _selectedNames.Contains(r.Classified.Observed.Name)).ToList();
        RaiseSelection();
        SelectionRestoreRequested?.Invoke(this, EventArgs.Empty);
    }

    void RaiseSelection()
    {
        OnPropertyChanged(nameof(SelectedRows));
        OnPropertyChanged(nameof(SelectedCount));
        foreach (System.Windows.Input.ICommand c in new[] { OpenSelectedCommand, RunSelectedCommand, EnableSelectedCommand, DisableSelectedCommand, DeleteSelectedCommand,
                     DuplicateSelectedCommand, OpenInTaskSchedulerSelectedCommand, CopyPathSelectedCommand, OpenFolderSelectedCommand })
        {
            if (c is RelayCommand r) r.RaiseCanExecuteChanged();
            else if (c is AsyncRelayCommand a) a.RaiseCanExecuteChanged();
        }
    }

    // ---- commands on the selection ----

    /// <summary>Enter or double-click: opens the one selected schedule.</summary>
    public System.Windows.Input.ICommand OpenSelectedCommand { get; }

    public System.Windows.Input.ICommand RunSelectedCommand { get; }

    public System.Windows.Input.ICommand EnableSelectedCommand { get; }

    public System.Windows.Input.ICommand DisableSelectedCommand { get; }

    public System.Windows.Input.ICommand DeleteSelectedCommand { get; }

    public System.Windows.Input.ICommand DuplicateSelectedCommand { get; }

    public System.Windows.Input.ICommand OpenInTaskSchedulerSelectedCommand { get; }

    public System.Windows.Input.ICommand CopyPathSelectedCommand { get; }

    /// <summary>Open folder: Explorer with the one selected schedule's script (or program) highlighted.</summary>
    public System.Windows.Input.ICommand OpenFolderSelectedCommand { get; }

    void OpenSelected()
    {
        if (_selected.Count == 1) _open(_selected[0]);
    }

    void DuplicateSelected()
    {
        if (_selected.Count == 1 && _selected[0].CanDuplicate) _actions.Duplicate?.Invoke(_selected[0].Classified);
    }

    // ---- foreign section ----

    public bool ShowForeign
    {
        get => _showForeign;
        set
        {
            if (!Set(ref _showForeign, value)) return;
            _services.Settings.ShowForeign = value;
            if (value)
            {
                ForeignExpanded = true;
                _ = RefreshForeignAsync(refresh: false);
            }
        }
    }

    public bool ForeignExpanded { get => _foreignExpanded; set => Set(ref _foreignExpanded, value); }

    /// <summary>The rates of the other tasks are still being counted on the thread pool (their cells show "-" until then).</summary>
    public bool ForeignRatesPending { get => _foreignRatesPending; private set => Set(ref _foreignRatesPending, value); }

    bool _foreignRatesPending;

    public bool ForeignLoading { get => _foreignLoading; private set => Set(ref _foreignLoading, value); }

    public string ForeignHeader { get => _foreignHeader; private set => Set(ref _foreignHeader, value); }

    public Task InitialLoad { get; private set; } = Task.CompletedTask;

    /// <summary>Starts the first load (and the foreign load when the toggle is on). Safe to call once.</summary>
    public void Start()
    {
        InitialLoad = RefreshAsync();
    }

    /// <summary>
    /// Reads the managed folder (a background thread) and updates the table when something changed. Calls that overlap are merged into one more
    /// pass. <paramref name="refreshOthers"/> (F5) also re-reads the cached other tasks when they are shown.
    /// </summary>
    public async Task RefreshAsync(bool refreshOthers = false)
    {
        if (_busy)
        {
            _pending = true;
            _pendingOthers |= refreshOthers;
            return;
        }
        _busy = true;
        try
        {
            bool others = refreshOthers;
            do
            {
                _pending = false;
                _pendingOthers = false;
                await LoadOnce(others).ConfigureAwait(true);
                others = _pendingOthers;
            }
            while (_pending);
        }
        finally { _busy = false; }
    }

    async Task LoadOnce(bool refreshOthers)
    {
        try
        {
            ManagedSnapshot snap = await _services.LoadManagedAsync().ConfigureAwait(true);
            Error = null;
            await ApplySnapshotAsync(snap).ConfigureAwait(true);
            if (_showForeign) await RefreshForeignAsync(refreshOthers).ConfigureAwait(true);
        }
        catch (SchedulerException ex)
        {
            Error = "Couldn't read Task Scheduler: " + ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Error = "Couldn't read your schedules: " + Describe(ex);
        }
        finally
        {
            HasLoaded = true;
            IsLoading = false;
        }

        // Once per start, after the first frame: drop run folders of jobs that no longer exist (never blocks the UI).
        if (!_prunedThisStart && Error is null)
        {
            _prunedThisStart = true;
            IReadOnlyCollection<string> live = _allRows.Select(r => r.Classified.Job?.Id).Where(id => id is not null).Select(id => id!).ToList();
            _ = Task.Run(async () =>
            {
                try { await _services.PruneOrphansAsync(live).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* housekeeping only */ }
            });
        }
    }

    /// <summary>
    /// Test hook. The text of a failure for the banner. A wrapper exception (a sort reports an exception thrown inside its comparer as "Failed to compare two elements") says nothing, so the
    /// innermost exception's type and message follow it. Messages only, never data from the folder.
    /// </summary>
    public static string Describe(Exception ex)
    {
        Exception inner = ex;
        while (inner.InnerException is not null) inner = inner.InnerException;
        return ReferenceEquals(inner, ex) ? ex.Message : ex.Message + " (" + inner.GetType().Name + ": " + inner.Message + ")";
    }

    /// <summary>Describes every row (and counts its rate) on the thread pool; only the assignment of the rows happens here.</summary>
    async Task ApplySnapshotAsync(ManagedSnapshot snap)
    {
        _foreignInFolder = snap.ForeignInFolder;
        DateTimeOffset now = _services.Now;
        TimeZoneInfo zone = _services.Zone;
        List<(ManagedEntry Entry, RowText Text)> described = await Task.Run(() => snap.Entries
            .Select(e => (Entry: e, Text: ListFormat.Describe(e.Task, e.History, e.Next, now, zone, e.Task.Ownership is Ownership.CorruptMetadata or Ownership.NewerVersion ? null : RateFormat.ForTask(e.Task, now, zone, _rates))))
            .ToList()).ConfigureAwait(true);
        var signature = described.Select(d => (d.Entry.Task.Observed.Name, d.Text)).ToList();
        if (!signature.SequenceEqual(_signature))
        {
            _signature = signature;
            _allRows = described.Select(d => new ScheduleRowViewModel(d.Entry, d.Text, _open, row => RunRowsAsync([row]), row => SetEnabledRowsAsync([row], !row.IsEnabled), row => DeleteRowsAsync([row]))).ToList();
            Reapply();
            RaiseState();
        }
        if (_showForeign) RebuildForeign(_lastOthers);
    }

    /// <summary>Filters and sorts <see cref="_allRows"/> into <see cref="Rows"/> and restores the selection.</summary>
    void Reapply()
    {
        IComparer<RowText> comparer = GridLogic.Compare(_sort.Column, _sort.Descending);
        List<ScheduleRowViewModel> visible = _allRows.Where(r => GridLogic.Matches(r.Text, _filter)).OrderBy(r => r.Text, comparer).ToList();
        if (!visible.SequenceEqual(Rows))
        {
            RowsRebuilding?.Invoke(this, EventArgs.Empty);
            _rebuilding = true;
            try
            {
                Rows.Clear();
                foreach (ScheduleRowViewModel row in visible) Rows.Add(row);
            }
            finally { _rebuilding = false; }
        }
        _selected = Rows.Where(r => _selectedNames.Contains(r.Classified.Observed.Name)).ToList();
        _selectedNames = new HashSet<string>(_selected.Select(r => r.Classified.Observed.Name), StringComparer.OrdinalIgnoreCase);   // a deleted or filtered-out row leaves the selection
        RaiseSelection();
        OnPropertyChanged(nameof(NoMatches));
        OnPropertyChanged(nameof(StatusText));
        SelectionRestoreRequested?.Invoke(this, EventArgs.Empty);
    }

    IReadOnlyList<ClassifiedTask> _lastOthers = [];

    async Task RefreshForeignAsync(bool refresh)
    {
        ForeignLoading = true;
        try
        {
            _lastOthers = await _services.LoadOthersAsync(refresh).ConfigureAwait(true);
            RebuildForeign(_lastOthers);
        }
        catch (SchedulerException ex)
        {
            Error = "Couldn't read Task Scheduler: " + ex.Message;
        }
        finally { ForeignLoading = false; }
    }

    void RebuildForeign(IReadOnlyList<ClassifiedTask> others)
    {
        DateTimeOffset now = _services.Now;
        TimeZoneInfo zone = _services.Zone;
        var rows = _foreignInFolder.Concat(others)
            .Select(c => (Path: c.Observed.Path, Text: ListFormat.DescribeForeign(c, now, zone), Task: c))
            .OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        ForeignHeader = "Other Task Scheduler tasks (not managed by Cadence) · " + rows.Count.ToString(CultureInfo.InvariantCulture);
        var signature = rows.Select(r => (r.Path, r.Text)).ToList();
        if (signature.SequenceEqual(_foreignSignature)) return;
        _foreignSignature = signature;
        _foreignAll = rows.Select(r => (Row: new ForeignRowViewModel(r.Text, r.Path, () => _openForeign?.Invoke(r.Task)), r.Task)).ToList();
        ReapplyForeign();
        ForeignRatesPending = true;
        _ = ComputeForeignRatesAsync(_foreignAll.ToList(), now, zone, ++_foreignGeneration);
    }

    /// <summary>Counts the rates of the other tasks on the thread pool (163 tasks must not block the UI); rows show "-" until then.</summary>
    async Task ComputeForeignRatesAsync(List<(ForeignRowViewModel Row, ClassifiedTask Task)> rows, DateTimeOffset now, TimeZoneInfo zone, int generation)
    {
        try
        {
            List<RateInfo> rates = await Task.Run(() => rows.Select(r => RateFormat.ForForeign(r.Task, now, zone, _rates)).ToList()).ConfigureAwait(true);
            if (generation != _foreignGeneration) return;
            for (int i = 0; i < rows.Count; i++) rows[i].Row.Rate = rates[i];
            ReapplyForeign();
            ForeignRatesPending = false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OverflowException)
        {
            // A task whose triggers can't be counted keeps its "-"; the table itself is unaffected.
            if (generation == _foreignGeneration) ForeignRatesPending = false;
        }
    }

    void ReapplyForeign()
    {
        IComparer<ForeignSortItem> comparer = GridLogic.CompareForeign(_sort.Column, _sort.Descending);
        List<ForeignRowViewModel> visible = _foreignAll.Select(a => a.Row)
            .Where(r => GridLogic.MatchesForeign(r.Text, r.RateText, _filter))
            .OrderBy(r => r.SortItem, comparer)
            .ToList();
        if (!visible.SequenceEqual(ForeignRows))
        {
            ForeignRows.Clear();
            foreach (ForeignRowViewModel row in visible) ForeignRows.Add(row);
        }
    }

    // ---- row actions (the toolbar and menus act on the selection; a row's own commands act on that row) ----

    async Task RunRowsAsync(IReadOnlyList<ScheduleRowViewModel> rows)
    {
        if (rows.Count == 0) return;
        string? firstError = null;
        var started = new List<string>();
        foreach (ScheduleRowViewModel row in rows)
        {
            string? error = await _services.RunNowAsync(row.Classified.Observed.Name).ConfigureAwait(true);
            if (error is null) started.Add(row.Name);
            else firstError ??= error;
        }
        if (firstError is not null) await _dialogs.InfoAsync("Couldn't start the job", firstError).ConfigureAwait(true);
        if (started.Count == 1) _toast("Started \"" + started[0] + "\".");
        else if (started.Count > 1) _toast("Started " + started.Count.ToString(CultureInfo.InvariantCulture) + " schedules.");
        await RefreshAsync().ConfigureAwait(true);
    }

    async Task SetEnabledRowsAsync(IReadOnlyList<ScheduleRowViewModel> rows, bool enable)
    {
        if (rows.Count == 0) return;
        string? firstError = null;
        int done = 0;
        foreach (ScheduleRowViewModel row in rows)
        {
            var result = await _services.SetEnabledAsync(row.Classified, enable).ConfigureAwait(true);
            if (result.Success) done++;
            else firstError ??= result.Message;
        }
        if (firstError is not null) await _dialogs.InfoAsync(enable ? "Couldn't enable the job" : "Couldn't disable the job", firstError).ConfigureAwait(true);
        if (done == 1) _toast((enable ? "Enabled \"" : "Disabled \"") + rows[0].Name + "\".");
        else if (done > 1) _toast((enable ? "Enabled " : "Disabled ") + done.ToString(CultureInfo.InvariantCulture) + " schedules.");
        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>One confirmation for the whole selection (names up to five, then "and 2 more"), then each is deleted and one toast says how many.</summary>
    async Task DeleteRowsAsync(IReadOnlyList<ScheduleRowViewModel> rows)
    {
        if (rows.Count == 0) return;
        bool ok = await _dialogs.ConfirmAsync(rows.Count == 1 ? "Delete \"" + rows[0].Name + "\"?" : "Delete " + rows.Count.ToString(CultureInfo.InvariantCulture) + " schedules?",
            DeleteMessage(rows), "Delete", danger: true).ConfigureAwait(true);
        if (!ok) return;
        string? firstError = null;
        int deleted = 0;
        foreach (ScheduleRowViewModel row in rows)
        {
            var result = await _services.DeleteAsync(row.Classified).ConfigureAwait(true);
            if (result.Success) deleted++;
            else firstError ??= result.Message;
        }
        if (firstError is not null) await _dialogs.InfoAsync("Couldn't delete the job", firstError).ConfigureAwait(true);
        if (deleted == 1) _toast("Deleted \"" + rows[0].Name + "\".");
        else if (deleted > 1) _toast("Deleted " + deleted.ToString(CultureInfo.InvariantCulture) + " schedules.");
        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>The body of the delete question: the existing sentence for one schedule; for several, the names and the same sentence in the plural.</summary>
    public static string DeleteMessage(IReadOnlyList<ScheduleRowViewModel> rows)
    {
        if (rows.Count == 1)
            return "This removes the schedule from Task Scheduler and deletes its run history. The program itself is not touched.";
        const int shown = 5;
        string names = string.Join(", ", rows.Take(shown).Select(r => "\"" + r.Name + "\""));
        if (rows.Count > shown) names += " and " + (rows.Count - shown).ToString(CultureInfo.InvariantCulture) + " more";
        return names + ".\n\nThis removes the schedules from Task Scheduler and deletes their run history. The programs themselves are not touched.";
    }

    /// <summary>The "Remove all Cadence schedules…" menu item (typed DELETE confirmation; other tasks are untouched).</summary>
    public async Task RemoveAllAsync()
    {
        bool ok = await _dialogs.ConfirmTypedAsync("Remove all Cadence schedules?",
            "This deletes every schedule Cadence manages, together with its run history. Other Task Scheduler tasks are not touched.",
            "DELETE", "Remove all").ConfigureAwait(true);
        if (!ok) return;
        var result = await _services.DeleteAllManagedAsync().ConfigureAwait(true);
        if (!result.Success) await _dialogs.InfoAsync("Not everything was removed", result.Message).ConfigureAwait(true);
        else _toast(result.Message);
        await RefreshAsync().ConfigureAwait(true);
    }
}
