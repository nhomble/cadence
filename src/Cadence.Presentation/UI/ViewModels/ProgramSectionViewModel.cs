using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Cadence.App.UI.Logic;
using Cadence.Mvvm;
using Cadence.Core.Metadata;
using Cadence.Core.Model;
using Cadence.Core.Planning;

namespace Cadence.App.UI.ViewModels;

/// <summary>
/// The "Do" part of the editor: the program card and the Advanced section (program, arguments, working folder, time limit, hidden and encoding,
/// environment variables), Browse, drops, Open folder and Copy path. Owned by <see cref="EditorViewModel"/> (<c>Program</c>), which is told through
/// <c>Changed</c> after every edit and builds the saved job from <see cref="BuildRun"/>.
/// </summary>
public sealed class ProgramSectionViewModel : ObservableObject
{
    readonly EditorViewModel _root;
    string _exe;
    string _args;
    string _workingDirectory;
    bool _hidden;
    bool _utf8;
    string? _sourcePath;
    bool _isAdvancedOpen;
    TimeoutChoice _timeout;
    string _customTimeout = "";
    string? _programMessage;
    IReadOnlyList<string> _warnings = [];
    bool _dropHover;

    internal ProgramSectionViewModel(EditorViewModel root, RunSpec run)
    {
        _root = root;
        _exe = run.Exe ?? "";
        _args = run.Args ?? "";
        _hidden = run.Hidden;
        _utf8 = run.Encoding == OutputEncoding.Utf8;
        _sourcePath = run.SourcePath;
        _timeout = TimeoutChoices[0];

        // A stored working folder equal to the automatic default shows as "automatic" (empty), so changing the program later moves it along.
        string? auto = EditorFormat.DefaultWorkingFolder(_exe, _args);
        _workingDirectory = run.WorkingDirectory is { } cwd && !string.Equals(cwd, auto, StringComparison.OrdinalIgnoreCase) ? cwd : "";
        _isAdvancedOpen = false;
        LoadTimeout(run.Timeout);
        foreach (KeyValuePair<string, string> kv in run.Env) Env.Add(new EnvRowViewModel(kv.Key, kv.Value, root.Changed, RemoveEnv));

        BrowseCommand = new RelayCommand(Browse);
        AddEnvCommand = new RelayCommand(AddEnv);
        OpenProgramFolderCommand = new RelayCommand(OpenProgramFolder, () => HasProgram);
        CopyProgramPathCommand = new RelayCommand(CopyProgramPath, () => HasProgram);
        ProgramInterpreter = new DropProgramInterpreter(root.Services.FileProbe);
    }

    public static IReadOnlyList<TimeoutChoice> TimeoutChoices => EditorFormat.TimeoutChoices;

    /// <summary>What turns a picked or dropped file into the program card (<see cref="DropProgramInterpreter"/> by default): Browse, drops and <see cref="UseFile"/> all go through it.</summary>
    public IProgramInterpreter ProgramInterpreter { get; set; }

    public string Exe
    {
        get => _exe;
        set
        {
            if (!Set(ref _exe, value)) return;
            _sourcePath = null;
            _programMessage = null;
            ClearWarnings();
            OnPropertyChanged(nameof(HasProgram));
            RaiseCard();
            OnPropertyChanged(nameof(ProgramMessage)); OnPropertyChanged(nameof(HasProgramMessage));
            OnPropertyChanged(nameof(WorkingDirectoryPlaceholder));
            _root.Changed();
        }
    }

    public bool HasProgram => !string.IsNullOrWhiteSpace(_exe);

    ProgramCardText Card => EditorFormat.ProgramCard(_exe, _args, _sourcePath);

    /// <summary>The card's title: the script when a script runs (ingest.py), else the program (backup.exe).</summary>
    public string ProgramName => Card.Title;

    /// <summary>The folder of what <see cref="ProgramName"/> names.</summary>
    public string ProgramFolder => Card.Folder;

    /// <summary>The quiet third line: "Runs with py.exe · from the shortcut Stock ingestion". Empty for a plain program.</summary>
    public string ProgramVia => Card.Via;

    public bool HasProgramVia => Card.Via.Length > 0;

    void RaiseCard()
    {
        OnPropertyChanged(nameof(ProgramName));
        OnPropertyChanged(nameof(ProgramFolder));
        OnPropertyChanged(nameof(ProgramVia));
        OnPropertyChanged(nameof(HasProgramVia));
        OnPropertyChanged(nameof(CommandLineText));
        OnPropertyChanged(nameof(WorkingFolderText));
        OnPropertyChanged(nameof(ShortcutText));
        OnPropertyChanged(nameof(HasShortcut));
        (OpenProgramFolderCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CopyProgramPathCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    /// <summary>The whole command line as stored (program, then arguments), shown selectable on the card. Nothing is expanded: %VAR% stays as typed.</summary>
    public string CommandLineText => ProgramReveal.CommandText(_exe, _args);

    /// <summary>"Working folder: C:\x" when one is set, else what an empty folder means (see <see cref="WorkingDirectoryPlaceholder"/>).</summary>
    public string WorkingFolderText => "Working folder: " + (string.IsNullOrWhiteSpace(_workingDirectory) ? WorkingDirectoryPlaceholder : _workingDirectory.Trim());

    /// <summary>For a job made from a dropped shortcut: "Shortcut: C:\Users\me\Desktop\Ingest.lnk" (the file Open folder shows). Empty otherwise.</summary>
    public string ShortcutText => ProgramReveal.Resolve(_exe, _args, _sourcePath) is { Kind: RevealKind.Shortcut } t ? "Shortcut: " + t.Path : "";

    public bool HasShortcut => ShortcutText.Length > 0;

    /// <summary>Open folder: Explorer with the script (or program, or shortcut) highlighted.</summary>
    public ICommand OpenProgramFolderCommand { get; }

    /// <summary>Copy path: the full path of the same file.</summary>
    public ICommand CopyProgramPathCommand { get; }

    void OpenProgramFolder() =>
        ProgramReveal.Reveal(_exe, _args, _sourcePath, _root.Services.FileProbe, _root.Host.RevealFile ?? (_ => false), _root.Host.OpenFolder, _root.Host.Toast);

    void CopyProgramPath()
    {
        if (ProgramReveal.Resolve(_exe, _args, _sourcePath) is not { } target) return;
        _root.Host.CopyText(ProgramReveal.ExpandedPath(target) ?? target.Path);
        _root.Host.Toast("Path copied.");
    }

    /// <summary>A sentence about the last file that was refused (a script before Phase 14, an unknown type). Cleared when a program is chosen.</summary>
    public string? ProgramMessage => _programMessage;

    public bool HasProgramMessage => _programMessage is not null;

    public string Args
    {
        get => _args;
        set { if (Set(ref _args, value)) { OnPropertyChanged(nameof(WorkingDirectoryPlaceholder)); RaiseCard(); _root.Changed(); } }
    }

    public string WorkingDirectory
    {
        get => _workingDirectory;
        set { if (Set(ref _workingDirectory, value)) { OnPropertyChanged(nameof(WorkingFolderText)); _root.Changed(); } }
    }

    /// <summary>What an empty working folder means: the script's or program's folder, else a hint.</summary>
    public string WorkingDirectoryPlaceholder =>
        EditorFormat.DefaultWorkingFolder(_exe, _args) is { } d ? d + "  (automatic)" : "Starts in C:\\Windows\\System32 unless you set a folder";

    public bool Hidden
    {
        get => _hidden;
        set { if (Set(ref _hidden, value)) { OnPropertyChanged(nameof(ShowEncoding)); _root.Changed(); } }
    }

    public bool ShowEncoding => _hidden;

    public bool Utf8Output
    {
        get => _utf8;
        set { if (Set(ref _utf8, value)) _root.Changed(); }
    }

    public bool IsAdvancedOpen
    {
        get => _isAdvancedOpen;
        set => Set(ref _isAdvancedOpen, value);
    }

    public TimeoutChoice SelectedTimeout
    {
        get => _timeout;
        set
        {
            if (value is null || !Set(ref _timeout, value)) return;
            OnPropertyChanged(nameof(ShowCustomTimeout));
            _root.Changed();
        }
    }

    public bool ShowCustomTimeout => _timeout.IsCustom;

    /// <summary>Minutes, when "Custom…" is chosen (decimals allowed, for example 1.5).</summary>
    public string CustomTimeoutMinutes
    {
        get => _customTimeout;
        set { if (Set(ref _customTimeout, value)) _root.Changed(); }
    }

    public ObservableCollection<EnvRowViewModel> Env { get; } = [];

    public ICommand BrowseCommand { get; }

    public ICommand AddEnvCommand { get; }

    /// <summary>Browse: asks the dialogs for a file, then <see cref="UseFile"/>.</summary>
    void Browse()
    {
        string? path = _root.Dialogs.PickProgram();
        if (path is not null) UseFile(path);
    }

    /// <summary>
    /// Sets the program from a file (Browse, or a drop) through <see cref="ProgramInterpreter"/>. Returns false, with
    /// <see cref="ProgramMessage"/> set, when the file can't be used; the current program is then left alone.
    /// </summary>
    public bool UseFile(string path)
    {
        ProgramChoice? choice = ProgramInterpreter.Interpret(path, out string? reject);
        return Finish(choice, reject);
    }

    /// <summary>
    /// <see cref="UseFile"/> with the interpretation (which may read a shortcut from disk) on the thread pool, so a drop never blocks the UI thread.
    /// Continues on the caller's context.
    /// </summary>
    public async Task<bool> UseFileAsync(string path)
    {
        IProgramInterpreter interpreter = ProgramInterpreter;
        (ProgramChoice? choice, string? reject) = await Task.Run(() =>
        {
            ProgramChoice? c = interpreter.Interpret(path, out string? r);
            return (c, r);
        });
        return Finish(choice, reject);
    }

    bool Finish(ProgramChoice? choice, string? reject)
    {
        if (_root.Notice.IsLocked)
        {
            // A drifted job is shown read-only until the banner's re-apply / stop-managing decision: a drop or Browse must not change it.
            _programMessage = _root.Notice.State is { Kind: BannerKind.Edited or BannerKind.Renamed }
                ? "This schedule was changed outside Cadence, so its program can't be changed until you re-apply it or stop managing it."
                : "This schedule is read-only (see the notice at the top), so its program can't be changed.";
            OnPropertyChanged(nameof(ProgramMessage)); OnPropertyChanged(nameof(HasProgramMessage));
            return false;
        }
        if (choice is null)
        {
            _programMessage = reject ?? "Cadence can't run that file.";
            OnPropertyChanged(nameof(ProgramMessage)); OnPropertyChanged(nameof(HasProgramMessage));
            return false;
        }
        ApplyChoice(choice);
        return true;
    }

    /// <summary>True while a file is being dragged over the window with this editor showing (the drop zone lights up).</summary>
    public bool IsDropHover { get => _dropHover; set => Set(ref _dropHover, value); }

    /// <summary>Notes about the program that was just chosen ("Python wasn't found ..."), shown under the program card. Cleared when the program is changed by hand.</summary>
    public IReadOnlyList<string> ProgramWarnings => _warnings;

    public bool HasProgramWarnings => _warnings.Count > 0;

    void ClearWarnings()
    {
        if (_warnings.Count == 0) return;
        _warnings = [];
        OnPropertyChanged(nameof(ProgramWarnings));
        OnPropertyChanged(nameof(HasProgramWarnings));
    }

    /// <summary>Applies a <see cref="ProgramChoice"/> to the form. <paramref name="nameFromProgram"/> also sets the job's name from the file's suggested name (a drop that started this job).</summary>
    public void ApplyChoice(ProgramChoice c, bool nameFromProgram = false)
    {
        _root.Loading = true;
        try
        {
            Exe = c.Exe;
            Args = c.Args;
            // A folder equal to the automatic default stays "automatic", so changing the program later moves it along.
            string? auto = EditorFormat.DefaultWorkingFolder(c.Exe, c.Args);
            WorkingDirectory = c.WorkingDirectory is { } cwd && !string.Equals(cwd, auto, StringComparison.OrdinalIgnoreCase) ? cwd : "";
            Utf8Output = c.Encoding == OutputEncoding.Utf8;
            Env.Clear();
            foreach (KeyValuePair<string, string> kv in c.Env) Env.Add(new EnvRowViewModel(kv.Key, kv.Value, _root.Changed, RemoveEnv));
            _sourcePath = c.SourcePath;
            _root.SuggestName(c.SuggestedName, nameFromProgram);
            _programMessage = null;
            _warnings = c.Warnings.ToList();
        }
        finally { _root.Loading = false; }
        OnPropertyChanged(nameof(ProgramMessage)); OnPropertyChanged(nameof(HasProgramMessage));
        OnPropertyChanged(nameof(ProgramWarnings));
        OnPropertyChanged(nameof(HasProgramWarnings));
        OnPropertyChanged(nameof(HasProgram));
        OnPropertyChanged(nameof(WorkingDirectoryPlaceholder));
        RaiseCard();
        _root.Changed();
    }

    void AddEnv()
    {
        Env.Add(new EnvRowViewModel("", "", _root.Changed, RemoveEnv));
        IsAdvancedOpen = true;
        _root.Changed();
    }

    void RemoveEnv(EnvRowViewModel row)
    {
        Env.Remove(row);
        _root.Changed();
    }

    void LoadTimeout(TimeSpan? timeout)
    {
        if (timeout is null) { _timeout = TimeoutChoices[0]; return; }
        TimeoutChoice? match = TimeoutChoices.FirstOrDefault(c => c.Minutes is { } m && TimeSpan.FromMinutes(m) == timeout);
        if (match is not null) { _timeout = match; return; }
        _timeout = TimeoutChoices.First(c => c.IsCustom);
        _customTimeout = timeout.Value.TotalMinutes.ToString("0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>What Save would write for the program part of the job. An empty working folder becomes the automatic default.</summary>
    internal RunSpec BuildRun()
    {
        string? cwd = string.IsNullOrWhiteSpace(_workingDirectory) ? EditorFormat.DefaultWorkingFolder(_exe, _args) : _workingDirectory.Trim();
        var env = new ValueList<KeyValuePair<string, string>>(Env.Where(r => !r.IsBlank).Select(r => new KeyValuePair<string, string>(r.Name.Trim(), r.Value)));
        return new RunSpec
        {
            Exe = _exe.Trim(),
            Args = _args,
            WorkingDirectory = cwd,
            Env = env,
            Timeout = ReadTimeout(out _),
            Hidden = _hidden,
            Encoding = _utf8 ? OutputEncoding.Utf8 : OutputEncoding.Oem,
            SourcePath = _sourcePath,
        };
    }

    /// <summary>The Custom time limit text's problem as of the last <see cref="CheckTimeout"/> (null when there is none).</summary>
    internal FieldNote? TimeoutProblem { get; private set; }

    internal void CheckTimeout()
    {
        ReadTimeout(out FieldNote? tp);
        TimeoutProblem = tp;
    }

    /// <summary>The time limit as a span, or null (no limit, or a custom text that is not usable: <paramref name="problem"/> then says why).</summary>
    internal TimeSpan? ReadTimeout(out FieldNote? problem)
    {
        problem = null;
        if (!_timeout.IsCustom) return _timeout.Minutes is { } m ? TimeSpan.FromMinutes(m) : null;
        string text = _customTimeout.Trim();
        if (text.Length == 0)
        {
            problem = new FieldNote("Enter the timeout in minutes, for example 90.", true);
            return null;
        }
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double minutes) || double.IsNaN(minutes) || double.IsInfinity(minutes) || minutes <= 0)
        {
            problem = new FieldNote("Enter the timeout as a number of minutes, for example 90.", true);
            return null;
        }
        if (minutes * 60 > MetadataCodec.MaxTimeoutSeconds)
        {
            problem = new FieldNote("The timeout can be at most 24 days.", true);
            return null;
        }
        return TimeSpan.FromMinutes(minutes);
    }
}
