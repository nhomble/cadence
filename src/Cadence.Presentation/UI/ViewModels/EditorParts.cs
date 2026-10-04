using System.Windows.Input;
using Cadence.App.UI.Logic;
using Cadence.App.UI.Services;
using Cadence.Mvvm;
using Cadence.Core.Cron;
using Cadence.Core.History;
using Cadence.Core.Text;

namespace Cadence.App.UI.ViewModels;

/// <summary>A message under a field: red when <see cref="IsError"/>, yellow otherwise.</summary>
public sealed record FieldNote(string Text, bool IsError);

/// <summary>An item of a dropdown: the text shown and the value it stands for.</summary>
public sealed record Option<T>(string Label, T Value);

/// <summary>One time of the Simple view's "at" list. The text is committed when the box loses focus; an invalid text is flagged, never silently dropped.</summary>
public sealed class TimeItem : ObservableObject
{
    readonly Action<TimeItem> _changed;
    string _text;

    public TimeItem(string text, Action<TimeItem> changed, Action<TimeItem> remove)
    {
        _text = text;
        _changed = changed;
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public string Text
    {
        get => _text;
        set
        {
            if (!Set(ref _text, value)) return;
            if (EditorFormat.TryParseTime(value, out TimeOnly t))
            {
                // Show the canonical form ("8" -> "8:00") once the text is understood.
                string canonical = EditorFormat.TimeText(t);
                if (canonical != value) { _text = canonical; OnPropertyChanged(); }
            }
            OnPropertyChanged(nameof(IsValid));
            _changed(this);
        }
    }

    public bool IsValid => EditorFormat.TryParseTime(_text, out _);

    public bool TryGet(out TimeOnly time) => EditorFormat.TryParseTime(_text, out time);

    public ICommand RemoveCommand { get; }
}

/// <summary>A small on/off chip (a weekday, a day of the month).</summary>
public sealed class ChipItem : ObservableObject
{
    readonly Action _changed;
    bool _isOn;

    public ChipItem(string label, int value, Action changed)
    {
        Label = label;
        Value = value;
        _changed = changed;
    }

    public string Label { get; }

    public int Value { get; }

    public bool IsOn
    {
        get => _isOn;
        set { if (Set(ref _isOn, value)) _changed(); }
    }

    /// <summary>Sets the state without telling the editor (used while loading from the schedule).</summary>
    public void SetSilently(bool on)
    {
        _isOn = on;
        OnPropertyChanged(nameof(IsOn));
    }
}

/// <summary>One environment variable row of the Advanced section.</summary>
public sealed class EnvRowViewModel : ObservableObject
{
    readonly Action _changed;
    string _name;
    string _value;

    public EnvRowViewModel(string name, string value, Action changed, Action<EnvRowViewModel> remove)
    {
        _name = name;
        _value = value;
        _changed = changed;
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public string Name { get => _name; set { if (Set(ref _name, value)) _changed(); } }

    public string Value { get => _value; set { if (Set(ref _value, value)) _changed(); } }

    public bool IsBlank => _name.Length == 0 && _value.Length == 0;

    public ICommand RemoveCommand { get; }
}

/// <summary>One compiler message under the cron box.</summary>
public sealed record DiagnosticItem(string Text, CronSeverity Severity)
{
    public bool IsError => Severity == CronSeverity.Error;
    public bool IsWarning => Severity == CronSeverity.Warning;
    public bool IsInfo => Severity == CronSeverity.Info;
}

/// <summary>One line of the preview's next-runs list.</summary>
public sealed record PreviewRunItem(string TimeText, string Relative, string? Note, bool IsSkipped, bool IsClockChange)
{
    public bool HasNote => Note is not null;
}

/// <summary>One row of the recent-runs list. Clicking expands it and loads the captured output once.</summary>
public sealed class RunRowViewModel : ObservableObject
{
    readonly IAppServices _services;
    readonly string _jobId;
    bool _isExpanded;
    bool _loaded;
    bool _showErrors;
    string? _output;
    string? _errors;

    public RunRowViewModel(RunView run, RunRowText text, RunDetailText detail, string jobId, IAppServices services, Action? openSecurity = null)
    {
        Run = run;
        IsBlocked = EditorFormat.IsBlockedRun(run);
        OpenSecurityCommand = new RelayCommand(() => openSecurity?.Invoke());
        Text = text;
        Detail = detail;
        _jobId = jobId;
        _services = services;
        ToggleCommand = new AsyncRelayCommand(ToggleAsync);
    }

    public RunView Run { get; }

    public RunRowText Text { get; }

    public RunDetailText Detail { get; }

    public BadgeKind Tone => Text.Tone;

    /// <summary>Task Scheduler reported access denied (0x80070005) for this run: the expanded row offers the neutral "Open Windows Security" shortcut.</summary>
    public bool IsBlocked { get; }

    public ICommand OpenSecurityCommand { get; }

    public ICommand ToggleCommand { get; }

    public bool IsExpanded { get => _isExpanded; private set => Set(ref _isExpanded, value); }

    /// <summary>The Errors tab is showing (else Output).</summary>
    public bool ShowErrors { get => _showErrors; set { if (Set(ref _showErrors, value)) { OnPropertyChanged(nameof(ShowOutput)); OnPropertyChanged(nameof(ShownText)); } } }

    public bool ShowOutput { get => !_showErrors; set => ShowErrors = !value; }

    public bool HasOutputFiles => Run.HasOutput;

    /// <summary>Text of the tab that is showing. Empty captures say so.</summary>
    public string ShownText
    {
        get
        {
            if (!_loaded) return "Loading…";
            string? text = _showErrors ? _errors : _output;
            return string.IsNullOrEmpty(text) ? (_showErrors ? "(nothing on the error stream)" : "(no output captured)") : text;
        }
    }

    /// <summary>The plain text copied by "Copy": the details plus both streams.</summary>
    public string CopyText => string.Join("\n", new[]
    {
        Text.Title + " · " + Text.When, Detail.Exit, Detail.Started, Detail.Message ?? "",
        "--- output ---", _output ?? "", "--- errors ---", _errors ?? "",
    });

    async Task ToggleAsync()
    {
        IsExpanded = !IsExpanded;
        if (!IsExpanded || _loaded) return;
        if (!Run.HasOutput || Run.IsSynthetic)
        {
            _loaded = true;
            OnPropertyChanged(nameof(ShownText));
            return;
        }
        OnPropertyChanged(nameof(ShownText));
        string? output = await _services.ReadOutputAsync(_jobId, Run.RunId, stderr: false);
        string? errors = await _services.ReadOutputAsync(_jobId, Run.RunId, stderr: true);
        _output = output;
        _errors = errors;
        _loaded = true;
        // Show the errors first when the run failed and only wrote there.
        if (Run.Status is RunStatus.Failed or RunStatus.TimedOut && string.IsNullOrEmpty(output) && !string.IsNullOrEmpty(errors)) _showErrors = true;
        OnPropertyChanged(nameof(ShowErrors));
        OnPropertyChanged(nameof(ShowOutput));
        OnPropertyChanged(nameof(ShownText));
    }
}
