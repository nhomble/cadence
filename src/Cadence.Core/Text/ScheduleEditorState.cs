using System.Globalization;
using Cadence.Core.Cron;
using Cadence.Core.Model;
using Cadence.Core.Scheduling;

namespace Cadence.Core.Text;

/// <summary>Which view of the schedule the editor shows.</summary>
public enum ScheduleEditorMode { Simple, Cron }

/// <summary>One line of the preview's "next runs" list. <see cref="Text"/> is the full <see cref="PreviewFormatter.FormatOne"/> text; <see cref="TimeText"/> and <see cref="Note"/> are its parts.</summary>
public sealed record PreviewRun(Occurrence Run, string Text, string TimeText, string? Note, bool IsSkipped, bool IsClockChange);

/// <summary>
/// What the editor shows under the schedule: the English sentence, the diagnostics (Errors block Save), the next runs and the yellow notes.
/// <see cref="RunsAtSignIn"/> is true when the schedule has an at-sign-in line (it has no clock time to list).
/// </summary>
public sealed record SchedulePreview(
    string Sentence,
    bool IsValid,
    ValueList<CronDiagnostic> Diagnostics,
    ValueList<PreviewRun> Runs,
    bool RunsAtSignIn,
    ValueList<string> Notes,
    ValueList<string>? AlternativeLines,
    string? AlternativeExplanation)
{
    public bool HasErrors => Diagnostics.Any(d => d.Severity == CronSeverity.Error);
}

/// <summary>
/// The Simple | Cron toggle of the editor as a pure state machine (PLAN Phase 13). The cron lines are the one schedule; the Simple view
/// (<see cref="Human"/>) is a lens on them that exists only when <see cref="HumanScheduleMapper.TryFromCron"/> accepts the lines.
/// <list type="bullet">
/// <item>A schedule the Simple view cannot express opens in Cron mode, and a switch to Simple then shows it read-only (<see cref="Human"/> is null);
/// nothing is ever rewritten or lost by looking at it.</item>
/// <item>Editing in Simple mode normalizes through <see cref="HumanScheduleMapper.Normalize"/> (inside <c>ToCron</c>/<c>IsValid</c>) and writes the
/// canonical lines to <see cref="CronText"/>; an incomplete draft (no times, no days) keeps the last good lines and reports an Error in the preview.</item>
/// <item>Cron → Simple uses <c>TryFromCron</c>; Simple → Cron shows the generated lines.</item>
/// </list>
/// </summary>
public sealed class ScheduleEditorState
{
    public const string CronOnlyMessage = "This schedule can only be edited in the Cron view.";
    public const string CronErrorsMessage = "The cron text has errors, so it can't be shown in the Simple view yet. Fix it in the Cron view.";

    string _cronText;
    HumanSchedule? _human;

    public ScheduleEditorState(ScheduleSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        SkipHolidays = spec.SkipHolidays;
        Anchor = spec.Anchor;
        _cronText = string.Join("\n", spec.CronLines);
        if (HumanScheduleMapper.TryFromCron(spec.CronLines, out HumanSchedule h))
        {
            Mode = ScheduleEditorMode.Simple;
            _human = h;
        }
        else
        {
            Mode = ScheduleEditorMode.Cron;
            _human = null;
        }
    }

    public ScheduleEditorMode Mode { get; private set; }

    /// <summary>The Simple view's draft, as the user left it (not normalized, so the controls don't jump while editing). Null when the lines are cron-only.</summary>
    public HumanSchedule? Human => _human;

    /// <summary>The cron lines as text, one line per cron (what the Cron view's text box shows).</summary>
    public string CronText => _cronText;

    public HolidayCalendar SkipHolidays { get; private set; }

    public DateOnly Anchor { get; }

    /// <summary>True when the Simple view can edit the schedule (it is in Simple mode with a draft, or a switch to Simple would work).</summary>
    public bool SimpleIsEditable => Mode == ScheduleEditorMode.Simple && _human is not null;

    /// <summary>In Simple mode without a draft: why the Simple view is read-only. Null otherwise.</summary>
    public string? SimpleReadOnlyReason
    {
        get
        {
            if (Mode != ScheduleEditorMode.Simple || _human is not null) return null;
            return HasCronErrors() ? CronErrorsMessage : CronOnlyMessage;
        }
    }

    /// <summary>The cron lines of the current text: trimmed, blank lines dropped.</summary>
    public ValueList<string> CronLines => ParseLines(_cronText);

    // ----- changes ------------------------------------------------------------------------------------

    public void SetMode(ScheduleEditorMode mode)
    {
        if (mode == Mode) return;
        if (mode == ScheduleEditorMode.Cron)
        {
            // The Simple draft already wrote its lines to CronText as it was edited; nothing to generate here.
            Mode = ScheduleEditorMode.Cron;
            return;
        }
        Mode = ScheduleEditorMode.Simple;
        _human = HumanScheduleMapper.TryFromCron(CronLines, out HumanSchedule h) ? h : null;
    }

    /// <summary>Replaces the Simple draft. Ignored when the schedule is cron-only (there is no draft to edit).</summary>
    public void SetHuman(HumanSchedule human)
    {
        ArgumentNullException.ThrowIfNull(human);
        if (_human is null || Mode != ScheduleEditorMode.Simple) return;
        _human = human;
        if (HumanScheduleMapper.IsValid(human)) _cronText = string.Join("\n", HumanScheduleMapper.ToCron(human));
    }

    /// <summary>Replaces the Cron view's text. The Simple draft is re-derived when the view is next switched to.</summary>
    public void SetCronText(string text)
    {
        _cronText = text ?? "";
        if (Mode == ScheduleEditorMode.Cron) return;
        _human = HumanScheduleMapper.TryFromCron(CronLines, out HumanSchedule h) ? h : null;
    }

    public void SetSkipHolidays(HolidayCalendar calendar) => SkipHolidays = calendar;

    /// <summary>Starts a new Simple draft of the given frequency, replacing a cron-only schedule (an explicit user action). Times start at 9:00 where they apply.</summary>
    public void StartSimple(HumanSchedule human)
    {
        ArgumentNullException.ThrowIfNull(human);
        _human = human;
        Mode = ScheduleEditorMode.Simple;
        if (HumanScheduleMapper.IsValid(human)) _cronText = string.Join("\n", HumanScheduleMapper.ToCron(human));
    }

    // ----- results ------------------------------------------------------------------------------------

    /// <summary>The schedule as it will be saved (the cron lines of the current text). Check <see cref="Evaluate"/> for errors first.</summary>
    public ScheduleSpec ToSpec() => new(CronLines, SkipHolidays, Anchor);

    /// <summary>Compiles the schedule and the Simple draft's own checks; no clock needed. Errors block Save.</summary>
    public ValueList<CronDiagnostic> Evaluate(string userId = "user") => Compile(userId).Diagnostics;

    /// <summary>The preview for the given moment and zone (all times are passed in; this class never reads the clock).</summary>
    public SchedulePreview Preview(DateTimeOffset now, TimeZoneInfo tz, string userId = "user", int count = 8)
    {
        ArgumentNullException.ThrowIfNull(tz);
        (CompiledSchedule? compiled, ValueList<CronDiagnostic> diags) = Compile(userId);
        if (compiled is null || !compiled.Success)
        {
            return new SchedulePreview("Invalid schedule", false, diags, ValueList<PreviewRun>.Empty, false, ValueList<string>.Empty,
                compiled?.ClosestAlternative, compiled?.AlternativeExplanation);
        }

        string sentence = EnglishRenderer.Describe(compiled, SkipHolidays);
        IReadOnlyList<Occurrence> runs = NextRuns.Next(compiled.Triggers, SkipHolidays, now, count, tz);
        var rows = new List<PreviewRun>(runs.Count);
        foreach (Occurrence o in runs)
        {
            bool repeating = o.TriggerIndex >= 0 && o.TriggerIndex < compiled.Triggers.Count && compiled.Triggers[o.TriggerIndex].Repetition is not null;
            bool changeDay = repeating && IsClockChangeDate(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(o.At, tz).DateTime), tz);
            Occurrence marked = o with { DstAdjusted = o.DstAdjusted || changeDay };
            string text = PreviewFormatter.FormatOne(marked, now, tz);
            string timeText = PreviewFormatter.FormatOne(marked with { SkippedHoliday = null }, now, tz);
            string? note = null;
            if (marked.SkippedHoliday is not null) note = "skipped - " + marked.SkippedHoliday;
            rows.Add(new PreviewRun(marked, text, timeText, note, marked.SkippedHoliday is not null, marked.DstAdjusted));
        }

        var notes = new List<string>();
        if (compiled.Triggers.Any(IsFebruary29))
            notes.Add("Task Scheduler may also run this on 1 February of leap years (a Windows quirk).");
        if (rows.Any(r => r.IsClockChange))
            notes.Add("Runs marked (clock change) fall on a day the clocks change; Windows may time them slightly differently.");

        return new SchedulePreview(sentence, true, diags, new ValueList<PreviewRun>(rows), compiled.AtLogon, new ValueList<string>(notes), null, null);
    }

    // ----- helpers ------------------------------------------------------------------------------------

    bool HasCronErrors() => Compile("user").Diagnostics.Any(d => d.Severity == CronSeverity.Error);

    (CompiledSchedule? Compiled, ValueList<CronDiagnostic> Diagnostics) Compile(string userId)
    {
        if (Mode == ScheduleEditorMode.Simple && _human is { } h && !HumanScheduleMapper.IsValid(h))
        {
            var problem = new CronDiagnostic(CronSeverity.Error, 0, "schedule", HumanProblem(h));
            return (null, ValueList.Of(problem));
        }
        if (Mode == ScheduleEditorMode.Simple && _human is null && CronLines.Count == 0)
        {
            return (null, ValueList.Of(new CronDiagnostic(CronSeverity.Error, 0, "schedule", "There is no schedule yet.")));
        }
        CompiledSchedule compiled = CronCompiler.Compile(ToSpec(), new MapContext(userId));
        return (compiled, compiled.Diagnostics);
    }

    /// <summary>A friendly sentence for the first thing wrong with an incomplete Simple draft.</summary>
    public static string HumanProblem(HumanSchedule h)
    {
        ArgumentNullException.ThrowIfNull(h);
        HumanSchedule n = HumanScheduleMapper.Normalize(h);
        bool timesFreq = n.Frequency is HumanFrequency.Daily or HumanFrequency.Weekdays or HumanFrequency.SpecificDays
            or HumanFrequency.MonthlyOnDates or HumanFrequency.MonthlyOnWeekday;
        if (timesFreq && n.Times.Count == 0) return "Add at least one time.";
        if (timesFreq && n.Times.Count > HumanScheduleMapper.MaxTimes)
            return "At most " + HumanScheduleMapper.MaxTimes.ToString(CultureInfo.InvariantCulture) + " times are supported. Use the Cron view for more.";
        if (n.Frequency is HumanFrequency.SpecificDays or HumanFrequency.EveryNMinutes or HumanFrequency.EveryNHours && n.Days == WeekDays.None)
            return "Pick at least one day of the week.";
        if (n.Frequency == HumanFrequency.MonthlyOnDates)
        {
            if (n.MonthDates.Count == 0 && !n.LastDayOfMonth) return "Pick at least one day of the month.";
            if (n.MonthDates.Any(d => d is < 1 or > 31)) return "Days of the month go from 1 to 31.";
        }
        if (n.Frequency is HumanFrequency.EveryNMinutes or HumanFrequency.EveryNHours)
        {
            if (n.FromHour is { } f && n.ToHour is { } t && (f < 0 || t > 23 || f > t)) return "The hours must go from earlier to later, within 0 to 23.";
            if (n.Frequency == HumanFrequency.EveryNMinutes && !HumanScheduleMapper.MinuteIntervals.Contains(n.Interval)) return "Pick an interval from the list.";
            if (n.Frequency == HumanFrequency.EveryNHours)
            {
                if (!HumanScheduleMapper.HourIntervals.Contains(n.Interval)) return "Pick an interval from the list.";
                if (n.MinuteOffset is < 0 or > 59) return "The minute must be between 0 and 59.";
                return "Every N hours needs at least two runs a day: widen the hours or pick a shorter interval.";
            }
        }
        return "This schedule isn't complete yet.";
    }

    static ValueList<string> ParseLines(string text) =>
        new(text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));

    static bool IsFebruary29(TaskTrigger t) => t is MonthlyTrigger { Months: MonthSet.February } m && m.Days.Contains(29);

    /// <summary>True when the UTC offset differs between the start of the date and the start of the next date (a spring-forward or fall-back day).</summary>
    public static bool IsClockChangeDate(DateOnly date, TimeZoneInfo tz)
    {
        ArgumentNullException.ThrowIfNull(tz);
        DateTime start = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        DateTime next = DateTime.SpecifyKind(date.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        return tz.GetUtcOffset(start) != tz.GetUtcOffset(next);
    }
}
