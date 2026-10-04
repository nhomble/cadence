using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Cadence.App.UI.Logic;
using Cadence.Mvvm;
using Cadence.Core.Cron;
using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.Core.Text;

namespace Cadence.App.UI.ViewModels;

/// <summary>
/// The "When" part of the editor: the Simple | Cron schedule (one <see cref="ScheduleEditorState"/>), Skip holidays, the presets, and the live preview
/// (sentence, next runs, notes, the closest alternative). Owned by <see cref="EditorViewModel"/> (<c>Schedule</c>), which is told through <c>Changed</c> after
/// every edit and reads <see cref="State"/> and <see cref="Preview"/> to build and check the saved job. Only Core types, the clock and the zone are used here.
/// </summary>
public sealed class ScheduleSectionViewModel : ObservableObject
{
    static readonly string[] WeekdayLabels = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
    static readonly WeekDays[] WeekdayFlags = [WeekDays.Monday, WeekDays.Tuesday, WeekDays.Wednesday, WeekDays.Thursday, WeekDays.Friday, WeekDays.Saturday, WeekDays.Sunday];

    readonly EditorViewModel _root;
    readonly ScheduleEditorState _state;
    bool _syncing;
    HumanFrequency _frequency;
    WeekOfMonth _week;
    DayOfWeek _weekday;
    int _interval;
    int _minuteOffset;
    bool _limitHours;
    int _fromHour = 9;
    int _toHour = 17;
    bool _lastDay;
    string _cronText = "";
    HolidayCalendar _skip;
    SchedulePreview _preview;
    FieldNote? _timesProblem;

    internal ScheduleSectionViewModel(EditorViewModel root, ScheduleSpec schedule)
    {
        _root = root;
        _state = new ScheduleEditorState(schedule);
        _skip = schedule.SkipHolidays;
        _cronText = _state.CronText;

        Days = WeekdayLabels.Select((l, i) => new ChipItem(l, i, PushHuman)).ToList();
        MonthDates = Enumerable.Range(1, 31).Select(d => new ChipItem(d.ToString(CultureInfo.InvariantCulture), d, PushHuman)).ToList();
        _frequency = HumanFrequency.Daily;
        _interval = 15;
        PullHuman();

        _preview = _state.Preview(root.Now, root.Zone);

        AddTimeCommand = new RelayCommand(AddTime);
        UseAlternativeCommand = new RelayCommand(UseAlternative);
        EditInCronCommand = new RelayCommand(() => IsCronMode = true);
        PickPresetCommand = new RelayCommand(() => { if (!_root.Notice.IsLocked) _root.Host.PickPreset?.Invoke(_root); });
    }

    public static IReadOnlyList<Option<HumanFrequency>> FrequencyOptions { get; } =
    [
        new("Every day", HumanFrequency.Daily),
        new("Weekdays", HumanFrequency.Weekdays),
        new("Specific days", HumanFrequency.SpecificDays),
        new("Monthly on dates", HumanFrequency.MonthlyOnDates),
        new("Monthly on a weekday", HumanFrequency.MonthlyOnWeekday),
        new("Every N minutes", HumanFrequency.EveryNMinutes),
        new("Every N hours", HumanFrequency.EveryNHours),
        new("When I sign in", HumanFrequency.AtSignIn),
    ];

    public static IReadOnlyList<Option<HolidayCalendar>> SkipOptions { get; } =
    [
        new("None", HolidayCalendar.None),
        new("US federal holidays", HolidayCalendar.UsFederal),
        new("US stock market (NYSE) holidays", HolidayCalendar.Nyse),
    ];

    public static IReadOnlyList<Option<WeekOfMonth>> WeekOptions { get; } =
    [
        new("First", WeekOfMonth.First), new("Second", WeekOfMonth.Second), new("Third", WeekOfMonth.Third),
        new("Fourth", WeekOfMonth.Fourth), new("Last", WeekOfMonth.Last),
    ];

    public static IReadOnlyList<Option<DayOfWeek>> WeekdayOptions { get; } =
    [
        new("Monday", DayOfWeek.Monday), new("Tuesday", DayOfWeek.Tuesday), new("Wednesday", DayOfWeek.Wednesday), new("Thursday", DayOfWeek.Thursday),
        new("Friday", DayOfWeek.Friday), new("Saturday", DayOfWeek.Saturday), new("Sunday", DayOfWeek.Sunday),
    ];

    public static IReadOnlyList<int> HourOptions { get; } = Enumerable.Range(0, 24).ToList();

    public static IReadOnlyList<int> MinuteOptions { get; } = Enumerable.Range(0, 60).ToList();

    public const string CronHelp =
        "Five fields: minute hour day-of-month month day-of-week\n" +
        "*  any      */15  every 15      1-5  a range      1,15  a list\n" +
        "Weekdays: 0 or 7 = Sunday, 1 = Monday ... 6 = Saturday (MON-FRI works)\n" +
        "Also: L (last day: 0 23 L * *), 5L (last Friday), 1#1 (first Monday)\n" +
        "Macros: @hourly @daily @weekly @monthly @logon\n" +
        "One schedule per line; all the lines together are the schedule.";

    public ScheduleEditorState State => _state;

    public bool IsSimpleMode
    {
        get => _state.Mode == ScheduleEditorMode.Simple;
        set { if (value) SetMode(ScheduleEditorMode.Simple); }
    }

    public bool IsCronMode
    {
        get => _state.Mode == ScheduleEditorMode.Cron;
        set { if (value) SetMode(ScheduleEditorMode.Cron); }
    }

    /// <summary>The Simple view has controls to show (it is in Simple mode and the schedule is expressible).</summary>
    public bool ShowSimpleControls => _state.SimpleIsEditable;

    /// <summary>Simple mode but the schedule can only be edited as cron: the sentence is shown read-only with this reason.</summary>
    public bool ShowSimpleReadOnly => IsSimpleMode && !_state.SimpleIsEditable;

    public string? SimpleReadOnlyReason => _state.SimpleReadOnlyReason;

    void SetMode(ScheduleEditorMode mode)
    {
        if (_state.Mode == mode) return;
        _state.SetMode(mode);
        _cronText = _state.CronText;
        PullHuman();
        OnPropertyChanged(nameof(CronText));
        RaiseModeProperties();
        Changed();
    }

    void RaiseModeProperties()
    {
        OnPropertyChanged(nameof(IsSimpleMode));
        OnPropertyChanged(nameof(IsCronMode));
        OnPropertyChanged(nameof(ShowSimpleControls));
        OnPropertyChanged(nameof(ShowSimpleReadOnly));
        OnPropertyChanged(nameof(SimpleReadOnlyReason));
    }

    public HumanFrequency Frequency
    {
        get => _frequency;
        set
        {
            HumanFrequency old = _frequency;
            if (!Set(ref _frequency, value)) return;
            ApplyFrequencyDefaults(old);
            RaiseVisibility();
            PushHuman();
        }
    }

    public ObservableCollection<TimeItem> Times { get; } = [];

    public IReadOnlyList<ChipItem> Days { get; }

    public IReadOnlyList<ChipItem> MonthDates { get; }

    public bool LastDayOfMonth
    {
        get => _lastDay;
        set { if (Set(ref _lastDay, value)) PushHuman(); }
    }

    public WeekOfMonth Week
    {
        get => _week;
        set { if (Set(ref _week, value)) PushHuman(); }
    }

    public DayOfWeek Weekday
    {
        get => _weekday;
        set { if (Set(ref _weekday, value)) PushHuman(); }
    }

    public IReadOnlyList<int> IntervalOptions =>
        _frequency == HumanFrequency.EveryNHours ? HumanScheduleMapper.HourIntervals : HumanScheduleMapper.MinuteIntervals;

    public int Interval
    {
        get => _interval;
        set { if (Set(ref _interval, value)) PushHuman(); }
    }

    public string IntervalUnit => _frequency == HumanFrequency.EveryNHours ? (_interval == 1 ? "hour" : "hours") : (_interval == 1 ? "minute" : "minutes");

    public int MinuteOffset
    {
        get => _minuteOffset;
        set { if (Set(ref _minuteOffset, value)) PushHuman(); }
    }

    public bool LimitHours
    {
        get => _limitHours;
        set { if (Set(ref _limitHours, value)) { OnPropertyChanged(nameof(ShowWindowPickers)); PushHuman(); } }
    }

    public int FromHour
    {
        get => _fromHour;
        set { if (Set(ref _fromHour, value)) PushHuman(); }
    }

    public int ToHour
    {
        get => _toHour;
        set { if (Set(ref _toHour, value)) PushHuman(); }
    }

    public bool ShowTimes => _frequency is HumanFrequency.Daily or HumanFrequency.Weekdays or HumanFrequency.SpecificDays or HumanFrequency.MonthlyOnDates or HumanFrequency.MonthlyOnWeekday;

    public bool ShowDays => _frequency is HumanFrequency.SpecificDays or HumanFrequency.EveryNMinutes or HumanFrequency.EveryNHours;

    public string DaysLabel => _frequency == HumanFrequency.SpecificDays ? "On" : "Only on";

    public bool ShowMonthDates => _frequency == HumanFrequency.MonthlyOnDates;

    public bool ShowWeekday => _frequency == HumanFrequency.MonthlyOnWeekday;

    public bool ShowInterval => _frequency is HumanFrequency.EveryNMinutes or HumanFrequency.EveryNHours;

    public bool ShowOffset => _frequency == HumanFrequency.EveryNHours;

    public bool ShowWindow => _frequency is HumanFrequency.EveryNMinutes or HumanFrequency.EveryNHours;

    public bool ShowWindowPickers => ShowWindow && _limitHours;

    public ICommand AddTimeCommand { get; }

    public ICommand EditInCronCommand { get; }

    void RaiseVisibility()
    {
        OnPropertyChanged(nameof(ShowTimes));
        OnPropertyChanged(nameof(ShowDays));
        OnPropertyChanged(nameof(DaysLabel));
        OnPropertyChanged(nameof(ShowMonthDates));
        OnPropertyChanged(nameof(ShowWeekday));
        OnPropertyChanged(nameof(ShowInterval));
        OnPropertyChanged(nameof(ShowOffset));
        OnPropertyChanged(nameof(ShowWindow));
        OnPropertyChanged(nameof(ShowWindowPickers));
        OnPropertyChanged(nameof(IntervalOptions));
        OnPropertyChanged(nameof(IntervalUnit));
    }

    void ApplyFrequencyDefaults(HumanFrequency previous)
    {
        _syncing = true;
        try
        {
            if (ShowTimes && Times.Count == 0) Times.Add(NewTime("9:00"));
            if (_frequency == HumanFrequency.MonthlyOnDates && !MonthDates.Any(c => c.IsOn) && !_lastDay) MonthDates[0].SetSilently(true);
            if (_frequency == HumanFrequency.SpecificDays)
            {
                WeekDays d = ReadDays();
                if (d is WeekDays.All or WeekDays.Weekdays or WeekDays.None) WriteDays(WeekDays.Monday | WeekDays.Wednesday | WeekDays.Friday);
            }
            if (ShowDays && ReadDays() == WeekDays.None) WriteDays(WeekDays.All);
            // Coming from "Weekdays" or "Every day", an interval schedule starts on every day (not on the weekdays the chips happened to hold).
            bool wasDayAware = previous is HumanFrequency.SpecificDays or HumanFrequency.EveryNMinutes or HumanFrequency.EveryNHours;
            if (_frequency is HumanFrequency.EveryNMinutes or HumanFrequency.EveryNHours && !wasDayAware) WriteDays(WeekDays.All);
            if (_frequency == HumanFrequency.EveryNMinutes && !HumanScheduleMapper.MinuteIntervals.Contains(_interval)) { _interval = 15; OnPropertyChanged(nameof(Interval)); }
            if (_frequency == HumanFrequency.EveryNHours && !HumanScheduleMapper.HourIntervals.Contains(_interval)) { _interval = 2; OnPropertyChanged(nameof(Interval)); }
            OnPropertyChanged(nameof(IntervalUnit));
        }
        finally { _syncing = false; }
    }

    TimeItem NewTime(string text) => new(text, _ => PushHuman(), RemoveTime);

    void AddTime()
    {
        // The new time follows the last one by an hour (9:00, 10:00 ...), a handy default for "several times a day".
        TimeOnly next = new(9, 0);
        if (Times.Count > 0 && Times[^1].TryGet(out TimeOnly last)) next = last.AddHours(1);
        Times.Add(NewTime(EditorFormat.TimeText(next)));
        PushHuman();
    }

    void RemoveTime(TimeItem item)
    {
        Times.Remove(item);
        PushHuman();
    }

    WeekDays ReadDays()
    {
        WeekDays d = WeekDays.None;
        foreach (ChipItem c in Days) if (c.IsOn) d |= WeekdayFlags[c.Value];
        return d;
    }

    void WriteDays(WeekDays days)
    {
        foreach (ChipItem c in Days) c.SetSilently((days & WeekdayFlags[c.Value]) != 0);
    }

    /// <summary>The Simple controls as a (possibly incomplete) HumanSchedule. Times that don't parse are left out and reported separately.</summary>
    HumanSchedule BuildHuman()
    {
        var times = new List<TimeOnly>();
        foreach (TimeItem t in Times) if (t.TryGet(out TimeOnly time)) times.Add(time);
        return new HumanSchedule(_frequency, new ValueList<TimeOnly>(times), ReadDays(),
            new ValueList<int>(MonthDates.Where(c => c.IsOn).Select(c => c.Value)), _lastDay,
            _week, _weekday, _interval, _minuteOffset, _limitHours ? _fromHour : null, _limitHours ? _toHour : null);
    }

    void PushHuman()
    {
        if (_syncing || _root.Loading || !_state.SimpleIsEditable) return;
        _state.SetHuman(BuildHuman());
        _cronText = _state.CronText;
        OnPropertyChanged(nameof(CronText));
        Changed();
    }

    /// <summary>Loads the Simple controls from the state's draft (without pushing anything back).</summary>
    void PullHuman()
    {
        bool was = _syncing;
        _syncing = true;
        try
        {
            if (_state.Human is not { } h) { RaiseVisibility(); return; }
            _frequency = h.Frequency;
            OnPropertyChanged(nameof(Frequency));
            Times.Clear();
            foreach (TimeOnly t in h.Times) Times.Add(NewTime(EditorFormat.TimeText(t)));
            WriteDays(h.Frequency is HumanFrequency.SpecificDays or HumanFrequency.EveryNMinutes or HumanFrequency.EveryNHours ? h.Days : WeekDays.Weekdays);
            foreach (ChipItem c in MonthDates) c.SetSilently(h.MonthDates.Contains(c.Value));
            _lastDay = h.LastDayOfMonth;
            _week = h.Week;
            _weekday = h.Weekday;
            _interval = h.Frequency is HumanFrequency.EveryNMinutes or HumanFrequency.EveryNHours ? h.Interval : (_frequency == HumanFrequency.EveryNHours ? 2 : 15);
            _minuteOffset = h.MinuteOffset;
            _limitHours = h.FromHour is not null && h.ToHour is not null;
            if (_limitHours) { _fromHour = h.FromHour!.Value; _toHour = h.ToHour!.Value; }
            OnPropertyChanged(nameof(LastDayOfMonth));
            OnPropertyChanged(nameof(Week));
            OnPropertyChanged(nameof(Weekday));
            OnPropertyChanged(nameof(Interval));
            OnPropertyChanged(nameof(MinuteOffset));
            OnPropertyChanged(nameof(LimitHours));
            OnPropertyChanged(nameof(FromHour));
            OnPropertyChanged(nameof(ToHour));
            RaiseVisibility();
        }
        finally { _syncing = was; }
    }

    // ============ Cron view, holidays, preview ============

    public string CronText
    {
        get => _cronText;
        set
        {
            if (!Set(ref _cronText, value ?? "")) return;
            _state.SetCronText(_cronText);
            Changed();
        }
    }

    public HolidayCalendar SkipHolidays
    {
        get => _skip;
        set
        {
            if (!Set(ref _skip, value)) return;
            _state.SetSkipHolidays(value);
            OnPropertyChanged(nameof(HolidayTooltip));
            Changed();
        }
    }

    // ---- [CP-C] presets: the template cards as a shortcut for the schedule of this editor ----

    /// <summary>"Use a preset…" next to WHEN: shown when the window can open the template sheet for this editor.</summary>
    public bool HasPresets => _root.Host.PickPreset is not null;

    public ICommand PickPresetCommand { get; }

    /// <summary>
    /// Replaces only the schedule with a template's cron and Skip holidays (the name, program and everything else stay). Templates are not a kind
    /// of job: this is the same as typing the template's cron in the Cron view. Does nothing while the form is locked by a notice.
    /// </summary>
    public void ApplyPreset(Template template)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (_root.Notice.IsLocked) return;
        _state.SetCronText(template.Cron);
        _cronText = _state.CronText;
        OnPropertyChanged(nameof(CronText));
        PullHuman();
        RaiseModeProperties();
        if (_skip != template.SuggestedSkip) SkipHolidays = template.SuggestedSkip;
        Changed();
    }

    public string HolidayTooltip =>EditorFormat.HolidayList(_skip, TimeZoneInfo.ConvertTime(_root.Now, _root.Zone).Year);

    public ICommand UseAlternativeCommand { get; }

    public SchedulePreview Preview => _preview;

    public string Sentence => _preview.Sentence;

    /// <summary>The preview card's headline: the sentence, or a nudge while the schedule is incomplete.</summary>
    public string PreviewHeadline => PreviewIsValid ? _preview.Sentence : "Fix the message above to see when this will run.";

    public bool PreviewIsValid => _preview.IsValid && _timesProblem is null;

    public IReadOnlyList<DiagnosticItem> Diagnostics { get; private set; } = [];

    public IReadOnlyList<PreviewRunItem> PreviewRuns { get; private set; } = [];

    public IReadOnlyList<string> PreviewNotes => _preview.Notes;

    public bool HasPreviewNotes => _preview.Notes.Count > 0;

    public bool ShowRunList => _preview.IsValid && _preview.Runs.Count > 0;

    public bool ShowAtSignIn => _preview.IsValid && _preview.RunsAtSignIn && _preview.Runs.Count == 0;

    public bool HasAlternative => _preview.AlternativeLines is not null;

    public string AlternativeText => _preview.AlternativeLines is { } l ? "Use " + string.Join("; ", l) + " instead" : "";

    public string? AlternativeExplanation => _preview.AlternativeExplanation;

    public string CronHelpText => CronHelp;

    void UseAlternative()
    {
        if (_preview.AlternativeLines is not { } lines) return;
        CronText = string.Join("\n", lines);
    }

    internal void RefreshPreview()
    {
        _preview = _state.Preview(_root.Now, _root.Zone);
        var diags = new List<DiagnosticItem>();
        if (_timesProblem is { } tp) diags.Add(new DiagnosticItem(tp.Text, CronSeverity.Error));
        foreach (CronDiagnostic d in _preview.Diagnostics.OrderByDescending(d => d.Severity))
            diags.Add(new DiagnosticItem(d.Message, d.Severity));
        Diagnostics = diags;
        PreviewRuns = _preview.Runs.Select(r => new PreviewRunItem(r.TimeText, EditorFormat.RelativeDay(r.Run.At, _root.Now, _root.Zone), r.Note, r.IsSkipped, r.IsClockChange)).ToList();
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(Sentence));
        OnPropertyChanged(nameof(PreviewHeadline));
        OnPropertyChanged(nameof(PreviewIsValid));
        OnPropertyChanged(nameof(Diagnostics));
        OnPropertyChanged(nameof(PreviewRuns));
        OnPropertyChanged(nameof(PreviewNotes));
        OnPropertyChanged(nameof(HasPreviewNotes));
        OnPropertyChanged(nameof(ShowRunList));
        OnPropertyChanged(nameof(ShowAtSignIn));
        OnPropertyChanged(nameof(HasAlternative));
        OnPropertyChanged(nameof(AlternativeText));
        OnPropertyChanged(nameof(AlternativeExplanation));
        OnPropertyChanged(nameof(SimpleReadOnlyReason));
    }

    /// <summary>The time box whose text isn't a time, reported as a schedule problem (the Simple draft ignores it meanwhile); null when every box is fine.</summary>
    internal FieldNote? TimesProblem => _timesProblem;

    // A time box whose text isn't a time is reported as a schedule problem (the Simple draft ignores it meanwhile).
    internal void CheckTimes()
    {
        _timesProblem = null;
        if (!ShowTimes || _state.Mode != ScheduleEditorMode.Simple) return;
        TimeItem? bad = Times.FirstOrDefault(t => !t.IsValid);
        if (bad is not null)
            _timesProblem = new FieldNote("\"" + bad.Text.Trim() + "\" isn't a time. Write it like 8:00 or 17:30.", true);
    }

    /// <summary>An edit of this section: ignored while the Simple controls and the state are being synchronised, else the editor re-validates.</summary>
    void Changed()
    {
        if (_syncing) return;
        _root.Changed();
    }
}
