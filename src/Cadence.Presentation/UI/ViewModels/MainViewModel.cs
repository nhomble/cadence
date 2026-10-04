using System.IO;
using System.Windows.Input;
using Cadence.Agent.Policy;
using Cadence.App.UI.Logic;
using Cadence.App.UI.Services;
using Cadence.Mvvm;
using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.Core.Text;

namespace Cadence.App.UI.ViewModels;

/// <summary>
/// The window's view model: the list (home), the PLUS template sheet, the editor (<see cref="EditorViewModel"/> behind <see cref="CurrentPage"/>),
/// the ⋯ menu commands and the refresh policy (on window activation throttled to 10 s, every 30 s while visible and active, on F5 and after every change).
/// </summary>
public sealed class MainViewModel : ObservableObject, IPanelHost
{
    public static readonly TimeSpan ActivationThrottle = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    readonly IAppServices _services;
    readonly IDialogs _dialogs;
    readonly IShell _shell;
    object _currentPage;
    bool _isSheetOpen;
    bool _windowActive = true;
    string? _toast;
    long _lastRefreshTicks = Environment.TickCount64;
    int _toastVersion;
    readonly IProgramInterpreter _interpreter;
    readonly Queue<DroppedProgram> _queue = new();
    EditorViewModel? _presetTarget;
    bool _dropHover;

    // [P23] the agent surface: the switch as last read, the header pill, the Orange note, the panel layer
    AgentPolicy _policy = AgentPolicy.Defaults;
    bool _policyLoaded;
    PillState _pill = AgentText.Pill(AgentPolicy.Defaults, DateTimeOffset.UnixEpoch, TimeZoneInfo.Utc);
    SwitchNote? _note;
    DateTimeOffset? _seenAt;
    bool _seenLoaded;
    object? _panel;
    ChoicePanelViewModel? _choice;
    Task _policyTask = Task.CompletedTask;

    public MainViewModel(IAppServices services, IDialogs dialogs, IShell shell)
    {
        _services = services;
        _dialogs = dialogs;
        _shell = shell;
        _interpreter = new DropProgramInterpreter(services.FileProbe);
        dialogs.AttachPanels(this);
        List = new ListPageViewModel(services, dialogs, OpenJob, ShowToast, OpenForeign,
            new ListActions(c => { if (c.Job is { } job) OpenDraft(EditorViewModel.DuplicateDraft(job)); }, OpenInTaskScheduler, _shell.CopyText,
                run => ProgramReveal.Reveal(run.Exe, run.Args, run.SourcePath, services.FileProbe, _shell.RevealFile, _shell.OpenFolder, ShowToast)));
        _currentPage = List;
        List.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ListPageViewModel.StatusText) or nameof(ListPageViewModel.HasLoaded) or nameof(ListPageViewModel.Counts))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(WindowTitle));
            }
        };

        DateTimeOffset now = services.Now;
        TimeZoneInfo zone = services.Zone;
        Cards = Templates.All
            .Select((t, i) => new TemplateCardViewModel(t, i, ListFormat.TemplateNext(t, now, zone), PickTemplate))
            .ToList();

        List.Cards = Cards;
        NewCommand = new RelayCommand(OpenSheet);
        FocusFilterCommand = new RelayCommand(() => { if (IsListShowing) List.RequestFocusFilter(); });
        CloseSheetCommand = new RelayCommand(CloseSheet);
        RefreshCommand = new AsyncRelayCommand(() => RefreshNowAsync(refreshOthers: true));
        BackCommand = new RelayCommand(Back);
        OpenTaskSchedulerCommand = new RelayCommand(_shell.OpenTaskScheduler);
        OpenRunsFolderCommand = new RelayCommand(OpenRunsFolder);
        RemoveAllCommand = new AsyncRelayCommand(List.RemoveAllAsync);
        List.OpenTaskSchedulerCommand = OpenTaskSchedulerCommand;
        AboutCommand = new RelayCommand(_dialogs.ShowAbout);
        DismissToastCommand = new RelayCommand(() => Toast = null);
        SaveCurrentCommand = new RelayCommand(SaveCurrent);
        RunNowCurrentCommand = new RelayCommand(RunNowCurrent);
        TogglePillCommand = new AsyncRelayCommand(ToggleAgentsAsync, null, ex => ShowToast("Something went wrong: " + ex.Message));
        OpenActivityCommand = new AsyncRelayCommand(OpenActivityAsync, null, ex => ShowToast("Something went wrong: " + ex.Message));
        OpenLimitsCommand = new RelayCommand(OpenLimits);
        AcknowledgeNoteCommand = new RelayCommand(AcknowledgeAgentNote);
        ClosePanelCommand = new RelayCommand(ClosePanel);
    }

    // ============ [P23] the agent surface ============

    /// <summary>The header pill ("Agents allowed" Green, "Agents suspended" Orange, "Agents unreadable: suspended for safety" Red).</summary>
    public PillState Pill { get => _pill; private set { if (Set(ref _pill, value)) { OnPropertyChanged(nameof(PillText)); OnPropertyChanged(nameof(PillTooltip)); OnPropertyChanged(nameof(PillKind)); } } }

    public string PillText => _pill.Text;

    public string PillTooltip => _pill.Tooltip;

    public PillKind PillKind => _pill.Kind;

    /// <summary>The pill shows once the switch has been read (no flash of "allowed" for a person who suspended agents).</summary>
    public bool IsPillVisible => _policyLoaded;

    /// <summary>The ⋯ menu's check mark: agents are suspended (also when the setting could not be read).</summary>
    public bool AgentsSuspended => _policy.Suspended;

    public AgentPolicy Policy => _policy;

    /// <summary>The Orange "AI agents were turned back on ..." note; null when there is none or the owner acknowledged it.</summary>
    public SwitchNote? AgentNote { get => _note; private set { if (Set(ref _note, value)) { OnPropertyChanged(nameof(HasAgentNote)); OnPropertyChanged(nameof(AgentNoteText)); OnPropertyChanged(nameof(AgentNoteTooltip)); } } }

    public bool HasAgentNote => _note is not null;

    public string AgentNoteText => _note?.Text ?? "";

    public string AgentNoteTooltip => _note?.Tooltip ?? "";

    /// <summary>Render mode only: draws the pill's tooltip as a card under the pill (a real ToolTip is a separate popup and does not appear in an off-screen render).</summary>
    public bool ShowTooltipPreview { get => _tooltipPreview; set => Set(ref _tooltipPreview, value); }

    bool _tooltipPreview;

    public ICommand TogglePillCommand { get; }

    public ICommand OpenActivityCommand { get; }

    public ICommand OpenLimitsCommand { get; }

    public ICommand AcknowledgeNoteCommand { get; }

    public ICommand ClosePanelCommand { get; }

    /// <summary>The panel drawn over the window: a <see cref="ChoicePanelViewModel"/>, <see cref="AgentActivityViewModel"/> or <see cref="AgentLimitsViewModel"/>; null when none.</summary>
    public object? ActivePanel { get => _panel; private set { if (Set(ref _panel, value)) OnPropertyChanged(nameof(HasPanel)); } }

    public bool HasPanel => _panel is not null;

    /// <summary>The first background load: the list and the agent switch.</summary>
    public Task InitialLoad => Task.WhenAll(List.InitialLoad, _policyTask);

    /// <summary>Reads the switch on the thread pool and updates the pill and the note. An unreadable setting (or any failure) shows as "unreadable: suspended for safety".</summary>
    public async Task RefreshPolicyAsync()
    {
        AgentPolicy policy;
        try { policy = await _services.LoadAgentPolicyAsync().ConfigureAwait(true); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { policy = AgentPolicy.Unreadable("it could not be read (" + ex.GetType().Name + ")"); }
        if (!_seenLoaded)
        {
            try { _seenAt = await Task.Run(() => _services.Settings.AgentNoteSeenAt).ConfigureAwait(true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { _seenAt = null; }
            _seenLoaded = true;
        }
        ApplyPolicy(policy);
    }

    void ApplyPolicy(AgentPolicy policy)
    {
        _policy = policy;
        bool first = !_policyLoaded;
        _policyLoaded = true;
        Pill = AgentText.Pill(policy, _services.Now, _services.Zone);
        AgentNote = AgentText.Note(policy, _services.Now, _services.Zone, _seenAt);
        OnPropertyChanged(nameof(AgentsSuspended));
        OnPropertyChanged(nameof(Policy));
        if (first) OnPropertyChanged(nameof(IsPillVisible));
    }

    /// <summary>Click on the pill or the menu item: suspending is immediate (it is the safe direction); turning agents back on asks first, in a panel inside the window.</summary>
    public async Task ToggleAgentsAsync()
    {
        if (!_policyLoaded) await RefreshPolicyAsync().ConfigureAwait(true);
        if (!_policy.Suspended)
        {
            await SetSuspendedAsync(true).ConfigureAwait(true);
            return;
        }
        AgentConfirm ask = AgentText.ResumeConfirmation(_policy);
        int pick = await ShowChoiceAsync(ask.Title, ask.Message, [new PanelChoice("Cancel"), new PanelChoice(ask.Confirm, IsPrimary: true)]).ConfigureAwait(true);
        if (pick == 1) await SetSuspendedAsync(false).ConfigureAwait(true);
    }

    async Task SetSuspendedAsync(bool suspended)
    {
        try
        {
            await _services.SetAgentsSuspendedAsync(suspended).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            ShowToast("Cadence couldn't change the agent setting (" + ex.GetType().Name + "). Nothing was changed.");
            return;
        }
        await RefreshPolicyAsync().ConfigureAwait(true);   // show what is really stored
        ShowToast(suspended
            ? "AI agents are suspended. They can still look at your schedules, but they can't change or run them."
            : "AI agents may change your schedules again.");
    }

    /// <summary>Opening the ⋯ menu (or "Got it") acknowledges the Orange note: it is remembered, so it does not come back at the next start.</summary>
    public void AcknowledgeAgentNote()
    {
        if (_note is not { } note) return;
        _seenAt = note.At;
        AgentNote = null;
        DateTimeOffset at = note.At;
        _ = Task.Run(() =>
        {
            try { _services.Settings.AgentNoteSeenAt = at; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { /* the note simply shows again next start */ }
        });
    }

    /// <summary>A question drawn inside the window. The index of the chosen button, or -1 when dismissed.</summary>
    public Task<int> ShowChoiceAsync(string title, string message, IReadOnlyList<PanelChoice> choices)
    {
        ClosePanel();
        var panel = new ChoicePanelViewModel(title, message, choices);
        _choice = panel;
        ActivePanel = panel;
        return Answer(panel);
    }

    async Task<int> Answer(ChoicePanelViewModel panel)
    {
        int result = await panel.Result.ConfigureAwait(true);
        if (ReferenceEquals(_panel, panel)) ActivePanel = null;
        if (ReferenceEquals(_choice, panel)) _choice = null;
        return result;
    }

    /// <summary>Closes whatever panel is open (a question counts as dismissed).</summary>
    public void ClosePanel()
    {
        _choice?.Dismiss();
        _choice = null;
        ActivePanel = null;
    }

    /// <summary>"Agent activity" in the ⋯ menu: the newest agent log lines in a panel inside the window.</summary>
    public async Task OpenActivityAsync()
    {
        ClosePanel();
        var activity = new AgentActivityViewModel(_services, ClosePanel, _shell.OpenFolder);
        ActivePanel = activity;
        await activity.LoadAsync().ConfigureAwait(true);
    }

    /// <summary>"What agents may do" in the ⋯ menu: the two limits.</summary>
    void OpenLimits()
    {
        ClosePanel();
        ActivePanel = new AgentLimitsViewModel(_services, _policy, ClosePanel, ShowToast, RefreshPolicyAsync);
    }

    public IAppServices Services => _services;

    public ListPageViewModel List { get; }

    public IReadOnlyList<TemplateCardViewModel> Cards { get; }

    /// <summary>The content host's current page: <see cref="List"/> or an <see cref="EditorViewModel"/>.</summary>
    public object CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (!Set(ref _currentPage, value)) return;
            OnPropertyChanged(nameof(IsListShowing));
            OnPropertyChanged(nameof(ShowDropOverlay));
            WatchEditor();
        }
    }

    public bool IsListShowing => ReferenceEquals(_currentPage, List);

    /// <summary>The status bar's left text ("8 schedules · 4 problems · 1 disabled"); the same on the list and in the editor.</summary>
    public string StatusText => List.StatusText;

    /// <summary>The window title ([P25]): the counts on the list ("Cadence - 8 schedules, 4 problems"), the schedule's name in the editor, plus "(unsaved)".</summary>
    public string WindowTitle
    {
        get
        {
            if (_currentPage is EditorViewModel editor) return GridLogic.EditorTitle(editor.IsNew, editor.Name, editor.IsDirty);
            (int total, int problems, _) = List.Counts;
            return GridLogic.ListTitle(List.HasLoaded, total, problems);
        }
    }

    EditorViewModel? _titledEditor;

    /// <summary>Follows the open editor's name and unsaved state, so the title bar says what the window holds.</summary>
    void WatchEditor()
    {
        if (_titledEditor is not null) _titledEditor.PropertyChanged -= OnEditorChanged;
        _titledEditor = _currentPage as EditorViewModel;
        if (_titledEditor is not null) _titledEditor.PropertyChanged += OnEditorChanged;
        OnPropertyChanged(nameof(WindowTitle));
    }

    void OnEditorChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EditorViewModel.IsDirty) or nameof(EditorViewModel.Name)) OnPropertyChanged(nameof(WindowTitle));
    }

    public bool IsSheetOpen { get => _isSheetOpen; set => Set(ref _isSheetOpen, value); }

    /// <summary>
    /// [CP-C] The open editor the sheet is choosing a schedule for ("Use a preset…" in the editor's When section); null for the plain PLUS sheet.
    /// Picking a card then only replaces that editor's schedule (and Skip holidays): its name and program stay.
    /// </summary>
    public EditorViewModel? PresetTarget => _presetTarget;

    public bool IsPresetSheet => _presetTarget is not null;

    /// <summary>The sheet's drop area: not in preset mode (a drop there would replace the program, which is not what that sheet is about).</summary>
    public bool ShowSheetDropArea => _presetTarget is null;

    public string SheetTitle => _presetTarget is { } e ? "When should " + (string.IsNullOrWhiteSpace(e.Name) ? "it" : e.Name.Trim()) + " run?" : "New schedule";

    public string SheetSubtitle => _presetTarget is not null
        ? "Pick a preset for the schedule. The name and the program stay as they are, and you can fine-tune the time after."
        : "Pick a starting point - you can change everything after.";

    /// <summary>A file is being dragged over the window while the list (not an editor) is showing: the "drop to set up a schedule" overlay.</summary>
    public bool ShowDropOverlay => _dropHover && IsListShowing;

    /// <summary>Drag enter / leave from the window. In an editor it lights up the drop zone instead of the overlay.</summary>
    public void SetDropHover(bool hovering)
    {
        if (_dropHover != hovering)
        {
            _dropHover = hovering;
            OnPropertyChanged(nameof(ShowDropOverlay));
        }
        if (_currentPage is EditorViewModel editor) editor.Program.IsDropHover = hovering;
    }

    /// <summary>
    /// Files were dropped on the window (PLAN 7.5). With an editor open, the first file replaces the program. Otherwise every usable file
    /// becomes a job: [CP-C] the first opens straight away as a new, unsaved draft (name from the file, program set, "Every day at 9:00"),
    /// and the next opens after each editor closes. The presets stay one click away in the draft ("Use a preset…").
    /// Interpretation (which reads shortcuts) runs on the thread pool. Anything that can't be used is explained in a toast.
    /// </summary>
    public async Task HandleDropAsync(IReadOnlyList<string> paths)
    {
        SetDropHover(false);
        if (paths.Count == 0) return;
        try
        {
            if (_currentPage is EditorViewModel editor)
            {
                if (IsSheetOpen) CloseSheet();
                bool ok = await editor.Program.UseFileAsync(paths[0]);
                if (paths.Count > 1) ShowToast("Used the first file only (" + EditorFormat.FileName(paths[0]) + "). Drop the others on the schedule list to set up their own schedules.");
                else if (!ok && editor.Program.ProgramMessage is { } why) ShowToast(why);
                return;
            }

            DropIntakeResult result = await Task.Run(() => DropIntake.Process(paths, _interpreter));
            if (result.Message is { } message) ShowToast(message);
            if (result.Programs.Count == 0) return;
            _queue.Clear();
            foreach (DroppedProgram program in result.Programs) _queue.Enqueue(program);
            ShowNextPending();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowToast("Something went wrong with that drop: " + ex.Message);
        }
    }

    /// <summary>The number of dropped files still waiting for their own draft (they open one at a time, after each editor closes).</summary>
    public int QueuedDrops => _queue.Count;

    void ShowNextPending(bool announce = false)
    {
        if (_queue.Count == 0 || !IsListShowing) return;
        DroppedProgram next = _queue.Dequeue();
        IsSheetOpen = false;
        EditorViewModel editor = EditorViewModel.ForTemplate(_services, _dialogs, MakeHost(), Templates.All.First(t => t.Id == BlankTemplateId));
        editor.Program.ApplyChoice(next.Choice, nameFromProgram: true);
        CurrentPage = editor;
        OnPropertyChanged(nameof(QueuedDrops));
        if (announce)
            ShowToast("Next dropped file: " + EditorFormat.FileName(next.Path) + (_queue.Count == 0 ? "." : " (" + _queue.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " more after this one)."));
    }

    /// <summary>The "Start from scratch" template: what a dropped file starts with (every day at 9:00).</summary>
    const string BlankTemplateId = "blank";

    /// <summary>"Use a preset…" in an editor: the template cards, choosing only the schedule of that editor.</summary>
    void OpenPresetSheet(EditorViewModel editor)
    {
        if (!ReferenceEquals(_currentPage, editor)) return;
        _presetTarget = editor;
        RaiseSheetText();
        IsSheetOpen = true;
    }

    void CloseSheet()
    {
        IsSheetOpen = false;
        if (_presetTarget is not null)
        {
            _presetTarget = null;
            RaiseSheetText();
        }
    }

    void RaiseSheetText()
    {
        OnPropertyChanged(nameof(PresetTarget));
        OnPropertyChanged(nameof(IsPresetSheet));
        OnPropertyChanged(nameof(ShowSheetDropArea));
        OnPropertyChanged(nameof(SheetTitle));
        OnPropertyChanged(nameof(SheetSubtitle));
    }

    public string? Toast { get => _toast; private set { if (Set(ref _toast, value)) OnPropertyChanged(nameof(HasToast)); } }

    public bool HasToast => _toast is not null;

    /// <summary>The build, plain text at the right of the status bar: "dev build" for a local build, "v0.2.0" when stamped by CADENCE_VERSION.</summary>
    public string VersionText { get; } = Cadence.Core.AppVersion.Display();

    /// <summary>The raw informational version (tooltip of <see cref="VersionText"/>); "dev build" when the assembly carries none.</summary>
    public string VersionTooltip { get; } = "Cadence " + (Cadence.Core.AppVersion.Raw() ?? "dev build");

    /// <summary>The ⋯ menu's checkable item; persisted in HKCU by the services' settings.</summary>
    public bool ShowForeign { get => List.ShowForeign; set { List.ShowForeign = value; OnPropertyChanged(); } }

    public ICommand NewCommand { get; }

    /// <summary>Ctrl+F: moves the keyboard focus into the table's filter box (the list only).</summary>
    public ICommand FocusFilterCommand { get; }

    public ICommand CloseSheetCommand { get; }

    public ICommand RefreshCommand { get; }

    public ICommand BackCommand { get; }

    public ICommand OpenTaskSchedulerCommand { get; }

    public ICommand OpenRunsFolderCommand { get; }

    public ICommand RemoveAllCommand { get; }

    public ICommand AboutCommand { get; }

    public ICommand DismissToastCommand { get; }

    /// <summary>Ctrl+S: saves the open editor (does nothing on the list or when saving is off).</summary>
    public ICommand SaveCurrentCommand { get; }

    /// <summary>Ctrl+R: Run now in the open editor.</summary>
    public ICommand RunNowCurrentCommand { get; }

    void SaveCurrent()
    {
        if (_currentPage is EditorViewModel editor && editor.SaveCommand.CanExecute(null)) editor.SaveCommand.Execute(null);
    }

    void RunNowCurrent()
    {
        if (_currentPage is EditorViewModel editor && editor.Runs.RunNowCommand.CanExecute(null)) editor.Runs.RunNowCommand.Execute(null);
    }

    /// <summary>Closing the window with an editor that has unsaved changes asks first. True when the window may close.</summary>
    public async Task<bool> ConfirmCloseAsync()
    {
        if (_currentPage is not EditorViewModel { IsDirty: true }) return true;
        return await _dialogs.ConfirmAsync("Discard changes?", "You have changes that are not saved. Closing Cadence will discard them.", "Discard and close", danger: true, "Keep editing");
    }

    /// <summary>The window is about to close and an editor has unsaved changes (the window then asks <see cref="ConfirmCloseAsync"/>).</summary>
    public bool HasUnsavedChanges => _currentPage is EditorViewModel { IsDirty: true };

    // ---- lifetime and refresh policy ----

    /// <summary>Called once after the first frame: starts the first background load.</summary>
    public void Start()
    {
        List.Start();
        _policyTask = RefreshPolicyAsync();
    }

    public void SetWindowActive(bool active)
    {
        _windowActive = active;
        if (active && Environment.TickCount64 - _lastRefreshTicks >= ActivationThrottle.TotalMilliseconds) _ = RefreshNowAsync(refreshOthers: false);
    }

    /// <summary>The 30 s timer: refreshes only while the window is active and the list is showing.</summary>
    public void OnPollTick()
    {
        if (_windowActive && IsListShowing) _ = RefreshNowAsync(refreshOthers: false);
    }

    Task RefreshNowAsync(bool refreshOthers)
    {
        _lastRefreshTicks = Environment.TickCount64;
        // The agent switch is re-read with every list refresh (activation, 30 s, F5, after changes), on the thread pool like the list.
        return Task.WhenAll(List.RefreshAsync(refreshOthers), RefreshPolicyAsync());
    }

    // ---- navigation ----

    void OpenSheet()
    {
        if (IsListShowing) IsSheetOpen = true;
    }

    /// <summary>Escape: close the sheet, else go back from the editor.</summary>
    public bool HandleEscape()
    {
        if (HasPanel) { ClosePanel(); return true; }
        if (IsSheetOpen) { CloseSheet(); return true; }
        if (_currentPage is EditorViewModel { IsMenuOpen: true } open) { open.IsMenuOpen = false; return true; }
        if (!IsListShowing) { Back(); return true; }
        return false;
    }

    /// <summary>Back (the arrow, Escape): the editor asks "Discard changes?" first when there are unsaved edits.</summary>
    void Back()
    {
        if (CurrentPage is EditorViewModel editor) _ = editor.RequestCloseAsync();
        else LeaveEditor();
    }

    /// <summary>Returns to the list (the editor calls this once it is allowed to close) and refreshes it.</summary>
    void LeaveEditor()
    {
        CurrentPage = List;
        _ = RefreshNowAsync(refreshOthers: false);
        ShowNextPending(announce: true);
    }

    EditorHost MakeHost() => new(LeaveEditor, () => RefreshNowAsync(refreshOthers: false), ShowToast, _shell.OpenFolder, _shell.CopyText,
        OpenDraft, OpenInTaskScheduler, _shell.OpenLink, OpenPresetSheet, ShowEditor, _shell.RevealFile);

    /// <summary>Duplicate: a new, unsaved editor for a copy of a schedule.</summary>
    void OpenDraft(JobDefinition draft)
    {
        CurrentPage = new EditorViewModel(_services, _dialogs, MakeHost(), draft, null);
        ShowToast("Duplicated. Review it, then Save to create the copy.");
    }

    /// <summary>
    /// "Open in Task Scheduler": Task Scheduler has no deep link, so the app opens it and puts the task's path on the clipboard, and the toast says where to look.
    /// Nothing is changed in Task Scheduler.
    /// </summary>
    void OpenInTaskScheduler(string taskPath)
    {
        _shell.CopyText(taskPath);
        _shell.OpenTaskScheduler();
        ShowToast(ForeignDetail.OpenedToast(taskPath));
    }

    /// <summary>A row of "Other Task Scheduler tasks": a read-only detail with one action, Open in Task Scheduler.</summary>
    async void OpenForeign(ClassifiedTask task)
    {
        try
        {
            bool open = await _dialogs.ShowForeignDetailAsync(ForeignDetail.From(task, _services.Now, _services.Zone));
            if (open) OpenInTaskScheduler(task.Observed.Path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowToast("Something went wrong: " + ex.Message);
        }
    }

    void PickTemplate(Template template)
    {
        if (_presetTarget is { } target)
        {
            // [CP-C] "Use a preset…": only the schedule changes (templates are a friendly face for cron, not a kind of job).
            CloseSheet();
            target.Schedule.ApplyPreset(template);
            return;
        }
        IsSheetOpen = false;
        CurrentPage = EditorViewModel.ForTemplate(_services, _dialogs, MakeHost(), template);
    }

    /// <summary>For render mode and tests: shows an editor that was built elsewhere (for example with a fixed clock).</summary>
    public void ShowEditor(EditorViewModel editor) => CurrentPage = editor;

    public EditorHost EditorHostForRender() => MakeHost();

    async void OpenJob(ScheduleRowViewModel row)
    {
        try
        {
            if (row.Classified.Ownership == Ownership.CorruptMetadata)
            {
                bool delete = await _dialogs.ConfirmAsync(DriftText.CorruptTitle, DriftText.CorruptMessage, "Delete", danger: true);
                if (delete)
                {
                    var result = await _services.DeleteAsync(row.Classified);
                    if (!result.Success) ShowToast(result.Message);
                    await List.RefreshAsync();
                }
                return;
            }
            if (row.Classified.Ownership == Ownership.NewerVersion)
            {
                if (await _dialogs.ConfirmAsync(DriftText.NewerTitle, DriftText.NewerMessage, "Open in Task Scheduler", danger: false, "Close"))
                    OpenInTaskScheduler(row.Classified.Observed.Path);
                return;
            }
            CurrentPage = EditorViewModel.ForEntry(_services, _dialogs, MakeHost(), row.Entry);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowToast("Something went wrong: " + ex.Message);
        }
    }

    void OpenRunsFolder()
    {
        if (!_shell.OpenFolder(_services.RunsFolderPath)) ShowToast("No run history yet.");
    }

    public async void ShowToast(string text)
    {
        int version = ++_toastVersion;
        Toast = text;
        await Task.Delay(TimeSpan.FromSeconds(5));
        if (version == _toastVersion) Toast = null;
    }

    /// <summary>For render mode and tests: opens the editor for a template without the sheet.</summary>
    public void PickTemplateById(string id) => PickTemplate(Templates.All.First(t => t.Id == id));
}
