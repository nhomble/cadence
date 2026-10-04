using System.Windows.Input;
using Cadence.App.UI.Logic;
using Cadence.App.UI.Services;
using Cadence.Mvvm;
using Cadence.Core.Planning;
using Cadence.Core.Text;

namespace Cadence.App.UI.ViewModels;

/// <summary>A table row that opens on Enter or a double-click; also what its UI Automation Invoke pattern calls (see <c>ScheduleGrid</c>).</summary>
public interface IOpenRow
{
    ICommand OpenCommand { get; }
}

/// <summary>One schedule row of the table: display strings from <see cref="ListFormat"/> plus the row commands (the toolbar and menus act on the selection, see <see cref="ListPageViewModel"/>).</summary>
public sealed class ScheduleRowViewModel : ObservableObject, IOpenRow
{
    public ScheduleRowViewModel(ManagedEntry entry, RowText text, Action<ScheduleRowViewModel> open, Func<ScheduleRowViewModel, Task> runNow,
        Func<ScheduleRowViewModel, Task> toggleEnabled, Func<ScheduleRowViewModel, Task> delete)
    {
        Entry = entry;
        Text = text;
        OpenCommand = new RelayCommand(() => open(this));
        RunNowCommand = new AsyncRelayCommand(() => runNow(this), () => CanRun);
        ToggleEnabledCommand = new AsyncRelayCommand(() => toggleEnabled(this), () => CanToggle);
        DeleteCommand = new AsyncRelayCommand(() => delete(this));
    }

    public ManagedEntry Entry { get; }

    public ClassifiedTask Classified => Entry.Task;

    public RowText Text { get; }

    /// <summary>Run now is possible: an enabled, in-sync schedule of Cadence's own.</summary>
    public bool CanRun => !Text.IsProblem && Text.IsEnabled && Entry.Task.Sync != SyncStatus.Drifted;

    /// <summary>Enable or Disable is possible (not for a damaged, newer-version or changed-outside row).</summary>
    public bool CanToggle => !Text.IsProblem && Entry.Task.Sync != SyncStatus.Drifted;

    /// <summary>Duplicate is possible for a schedule Cadence can read (not a damaged or newer-version one).</summary>
    public bool CanDuplicate => Entry.Task.Job is not null && !Text.IsProblem;

    public string TaskPath => Entry.Task.Observed.Path;

    public string Name => Text.Name;

    public string Schedule => Text.Schedule;

    public string Next => Text.Next;

    public string ResultTitle => Text.ResultTitle;

    public string ResultDetail => Text.ResultDetail;

    public BadgeKind Badge => Text.Badge;

    public BadgeKind Tone => Text.Tone;

    public string? BadgeTooltip => Text.BadgeTooltip;

    public string? ResultTooltip => Text.ResultTooltip;

    public string? Note => Text.Note;

    public bool HasNote => Text.Note is not null;

    public bool IsProblem => Text.IsProblem;

    public bool IsEnabled => Text.IsEnabled;

    /// <summary>A disabled schedule is shown in DimText (a damaged one is not: it is a problem, not an idle row).</summary>
    public bool IsDimmed => !Text.IsEnabled && !Text.IsProblem;

    // ---- the cells of the table ----

    public string RateText => Text.RateText;

    public string? RateTooltip => Text.RateTooltip;

    public string NextCell => Text.NextCell;

    public string ResultCell => Text.ResultCell;

    public TextTone ResultTone => Text.ResultTone;

    public string StateText => Text.StateText;

    public TextTone StateTone => Text.StateTone;

    public string AvgText => Text.AvgText;

    public string SuccessText => Text.SuccessText;

    public string UpdatedCell => Text.UpdatedCell;

    /// <summary>What a screen reader hears after the name: "every 15 min · Ok · 14 min ago · Changed in Task Scheduler".</summary>
    public string HelpText
    {
        get
        {
            var parts = new List<string>();
            if (Text.RateText.Length > 0) parts.Add(Text.RateText);
            if (Text.ResultCell.Length > 0) parts.Add(Text.ResultCell);
            if (Text.StateText.Length > 0) parts.Add(Text.StateText);
            return string.Join(" · ", parts);
        }
    }

    /// <summary>"Updated 2 h ago by Claude Code" (null when the task carries no Cadence data); the full words behind the Updated cell, also its UI Automation name.</summary>
    public string? Updated => Text.Updated;

    public bool HasUpdated => Text.Updated is not null;

    /// <summary>The UI Automation name of the Updated cell: the full words, or the cell text itself (what a screen reader hears anyway) when nobody has changed the task; never null, so the binding has nothing to warn about.</summary>
    public string UpdatedAutomationName => Text.Updated ?? Text.UpdatedCell;

    /// <summary>Created and updated, in full, plus the lock (the tooltip of the Updated line).</summary>
    public string? UpdatedTooltip => Text.UpdatedTooltip;

    /// <summary>The owner locked this job against AI agents (a small padlock beside the name).</summary>
    public bool IsLocked => Text.Locked;

    public string LockedTooltip => AgentText.LockedTooltip;

    public string ToggleText => Text.IsEnabled ? "Disable" : "Enable";

    public ICommand OpenCommand { get; }

    public ICommand RunNowCommand { get; }

    public ICommand ToggleEnabledCommand { get; }

    public ICommand DeleteCommand { get; }
}

/// <summary>One read-only row of "Other Task Scheduler tasks". The rate is counted on a background thread and arrives later ("-" until then).</summary>
public sealed class ForeignRowViewModel(ForeignText text, string sortKey, Action? open = null) : ObservableObject, IOpenRow
{
    RateInfo? _rate;

    /// <summary>Opens the read-only detail (a click on the row). Nothing about the task can be changed from there.</summary>
    public ICommand OpenCommand { get; } = new RelayCommand(() => open?.Invoke());

    public string Name => text.Name;

    public string Folder => text.Folder;

    public string Next => text.Next;

    public string NextCell => text.NextCell;

    public string Result => text.Result;

    public TextTone ResultTone => text.ResultTone;

    public bool IsEnabled => text.IsEnabled;

    /// <summary>Foreign rows are never dimmed (the shared cell style binds IsDimmed for every row type); a constant keeps that binding resolvable.</summary>
    public bool IsDimmed => false;

    public string StateText => text.IsEnabled ? "" : "Disabled";

    public string SortKey => sortKey;

    public ForeignText Text => text;

    public RateInfo? Rate
    {
        get => _rate;
        set
        {
            if (!Set(ref _rate, value)) return;
            OnPropertyChanged(nameof(RateText));
            OnPropertyChanged(nameof(RateTooltip));
        }
    }

    public string RateText => _rate?.Label ?? "-";

    public string? RateTooltip => _rate?.Tooltip;

    public ForeignSortItem SortItem => new(text, _rate);
}

/// <summary>One card of the template picker (the empty state and the PLUS sheet share the same nine).</summary>
public sealed class TemplateCardViewModel
{
    static readonly string[] Accents = ["BlueBrush", "CyanBrush", "PurpleBrush", "GreenBrush", "YellowBrush", "OrangeBrush", "BlueBrush", "CyanBrush", "DimBrush"];

    public TemplateCardViewModel(Template template, int index, string next, Action<Template> pick)
    {
        Template = template;
        Title = template.Title;
        Subtitle = template.Subtitle;
        Next = next;
        IsBlank = template.Id == "blank";
        AccentKey = IsBlank ? "DimBrush" : Accents[index % Accents.Length];
        PickCommand = new RelayCommand(() => pick(template));
    }

    public Template Template { get; }

    public string Title { get; }

    public string Subtitle { get; }

    public string Next { get; }

    public bool IsBlank { get; }

    public string AccentKey { get; }

    public ICommand PickCommand { get; }
}
