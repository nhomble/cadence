using Cadence.Core.Cron;
using Cadence.Core.Model;
using Cadence.Core.Text;
using Cadence.TestHarness;

namespace Cadence.Tests;

/// <summary>The Simple | Cron state machine and the preview of the schedule editor (PLAN Phase 13). Fixed clocks and zones only.</summary>
public static class ScheduleEditorStateTests
{
    static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
    static readonly TimeZoneInfo Fixed = TimeZoneInfo.CreateCustomTimeZone("T", TimeSpan.FromHours(-5), "T", "T");
    static readonly DateTimeOffset Nov25 = new(2026, 11, 25, 9, 0, 0, TimeSpan.FromHours(-5));
    static readonly DateOnly Anchor = new(2026, 9, 1);

    static ScheduleEditorState State(string cron, HolidayCalendar skip = HolidayCalendar.None) =>
        new(new ScheduleSpec(new ValueList<string>(cron.Split('\n')), skip, Anchor));

    public static void EveryTemplateRoundTripsSimpleCronSimple()
    {
        foreach (Template t in Templates.All)
        {
            var s = State(t.Cron, t.SuggestedSkip);
            T.True(t.Id + " opens in Simple", s.Mode == ScheduleEditorMode.Simple && s.Human is not null);
            HumanSchedule before = HumanScheduleMapper.Normalize(s.Human!);
            string text = s.CronText;
            s.SetMode(ScheduleEditorMode.Cron);
            T.Eq(t.Id + " cron text unchanged", text, s.CronText);
            s.SetMode(ScheduleEditorMode.Simple);
            T.True(t.Id + " back in Simple with the same schedule", s.Human is not null && HumanScheduleMapper.Normalize(s.Human).Equals(before));
            T.Eq(t.Id + " skip kept", t.SuggestedSkip, s.SkipHolidays);
            T.True(t.Id + " spec equal", s.ToSpec().CronLines.Equals(new ValueList<string>([t.Cron])));
        }
    }

    public static void CronOnlyScheduleOpensInCronAndLosesNothing()
    {
        var s = State("0,30 9 * * *");
        T.True("opens in Cron", s.Mode == ScheduleEditorMode.Cron && s.Human is null);
        s.SetMode(ScheduleEditorMode.Simple);
        T.True("Simple is read-only", s.Mode == ScheduleEditorMode.Simple && !s.SimpleIsEditable);
        T.Eq("reason", ScheduleEditorState.CronOnlyMessage, s.SimpleReadOnlyReason);
        T.Eq("text kept", "0,30 9 * * *", s.CronText);
        s.SetHuman(HumanSchedule.Default(HumanFrequency.Daily));
        T.Eq("a Simple edit is ignored", "0,30 9 * * *", s.CronText);
        s.SetMode(ScheduleEditorMode.Cron);
        T.Eq("still kept", "0,30 9 * * *", s.CronText);
        T.True("preview still has a sentence", s.Preview(Nov25, Fixed).IsValid);
    }

    public static void CronWithErrorsCannotBeShownInSimple()
    {
        var s = State("0 9 * * 1-5");
        s.SetMode(ScheduleEditorMode.Cron);
        s.SetCronText("0 9 15W * *");
        s.SetMode(ScheduleEditorMode.Simple);
        T.Eq("reason", ScheduleEditorState.CronErrorsMessage, s.SimpleReadOnlyReason);
        T.Eq("text kept", "0 9 15W * *", s.CronText);
        T.True("has an error", s.Preview(Nov25, Fixed).HasErrors);
    }

    public static void CronEditsBecomeSimpleWhenExpressible()
    {
        var s = State("0 9 * * 1-5");
        s.SetMode(ScheduleEditorMode.Cron);
        s.SetCronText("30 6 * * 1,3,5");
        s.SetMode(ScheduleEditorMode.Simple);
        T.Eq("frequency", HumanFrequency.SpecificDays, s.Human!.Frequency);
        T.Eq("time", new TimeOnly(6, 30), s.Human.Times[0]);
        T.Eq("days", WeekDays.Monday | WeekDays.Wednesday | WeekDays.Friday, s.Human.Days);
        s.SetCronText("  \n 30 6 * * 1,3,5 \n\n");
        T.Eq("blank lines are dropped", 1, s.CronLines.Count);
    }

    public static void SimpleEditsWriteNormalizedCron()
    {
        var s = State("0 9 * * *");
        s.SetHuman(HumanSchedule.Default(HumanFrequency.SpecificDays) with { Times = ValueList.Of(new TimeOnly(9, 0)), Days = WeekDays.All });
        T.Eq("all days is every day", "0 9 * * *", s.CronText);
        s.SetHuman(HumanSchedule.Default(HumanFrequency.SpecificDays) with { Times = ValueList.Of(new TimeOnly(9, 0)), Days = WeekDays.Weekdays });
        T.Eq("Mon-Fri is weekdays", "0 9 * * 1-5", s.CronText);
        s.SetHuman(HumanSchedule.Default(HumanFrequency.Daily) with { Times = ValueList.Of(new TimeOnly(19, 30), new TimeOnly(7, 0)) });
        T.True("two times, two lines", s.CronLines.Count == 2 && s.CronLines.Contains("0 7 * * *") && s.CronLines.Contains("30 19 * * *"));
        string sentence = s.Preview(Nov25, Fixed).Sentence;
        T.True("English names both", sentence.Contains("7:00", StringComparison.Ordinal) && sentence.Contains("19:30", StringComparison.Ordinal));
    }

    public static void IncompleteSimpleDraftKeepsLastTextAndReportsAnError()
    {
        var s = State("0 9 * * *");
        s.SetHuman(HumanSchedule.Default(HumanFrequency.Daily));
        T.Eq("text kept", "0 9 * * *", s.CronText);
        SchedulePreview p = s.Preview(Nov25, Fixed);
        T.True("invalid", !p.IsValid && p.HasErrors);
        T.Eq("message", "Add at least one time.", p.Diagnostics[0].Message);
        T.Eq("no runs", 0, p.Runs.Count);
        s.SetHuman(HumanSchedule.Default(HumanFrequency.SpecificDays) with { Times = ValueList.Of(new TimeOnly(9, 0)), Days = WeekDays.None });
        T.Eq("no days", "Pick at least one day of the week.", s.Preview(Nov25, Fixed).Diagnostics[0].Message);
        s.SetHuman(HumanSchedule.Default(HumanFrequency.MonthlyOnDates) with { Times = ValueList.Of(new TimeOnly(9, 0)) });
        T.Eq("no dates", "Pick at least one day of the month.", s.Preview(Nov25, Fixed).Diagnostics[0].Message);
        s.SetHuman(HumanSchedule.Default(HumanFrequency.EveryNHours) with { Interval = 12, FromHour = 9, ToHour = 10 });
        T.True("hours need two runs", s.Preview(Nov25, Fixed).Diagnostics[0].Message.Contains("two runs", StringComparison.Ordinal));
    }

    public static void PreviewShowsEightRunsAndTheSentence()
    {
        SchedulePreview p = State("0 8 * * 1-5").Preview(Nov25, Fixed);
        T.Eq("sentence", "Every weekday at 8:00", p.Sentence);
        T.Eq("eight runs", 8, p.Runs.Count);
        T.Eq("first", "Thu 26 Nov 8:00", p.Runs[0].Text);
        T.Eq("second", "Fri 27 Nov 8:00", p.Runs[1].Text);
    }

    public static void HolidaySkipIsStruckInThePreview()
    {
        SchedulePreview p = State("0 18 * * 1-5", HolidayCalendar.Nyse).Preview(Nov25, Fixed);
        T.Eq("sentence", "Every weekday at 18:00, except NYSE market holidays", p.Sentence);
        T.Eq("text", "Wed 25 Nov 18:00", p.Runs[0].Text);
        T.Eq("skipped text", "Thu 26 Nov 18:00 - skipped (Thanksgiving Day)", p.Runs[1].Text);
        T.True("flag", p.Runs[1].IsSkipped && !p.Runs[0].IsSkipped);
        T.Eq("time part", "Thu 26 Nov 18:00", p.Runs[1].TimeText);
        T.Eq("note", "skipped - Thanksgiving Day", p.Runs[1].Note);
        var s = State("0 18 * * 1-5");
        s.SetSkipHolidays(HolidayCalendar.Nyse);
        T.Eq("skip is part of the spec", HolidayCalendar.Nyse, s.ToSpec().SkipHolidays);
    }

    public static void February29GetsTheWindowsQuirkNote()
    {
        const string note = "Task Scheduler may also run this on 1 February of leap years (a Windows quirk).";
        T.True("0 0 29 2 *", State("0 0 29 2 *").Preview(Nov25, Fixed).Notes.Contains(note));
        T.True("0 0 29 * *", !State("0 0 29 * *").Preview(Nov25, Fixed).Notes.Contains(note));
        T.True("0 0 28 2 *", !State("0 0 28 2 *").Preview(Nov25, Fixed).Notes.Contains(note));
        T.True("0 0 1 2,3 *", !State("0 0 1 2,3 *").Preview(Nov25, Fixed).Notes.Contains(note));
    }

    public static void RepeatingRunsOnClockChangeDaysAreMarked()
    {
        var now = new DateTimeOffset(2027, 3, 14, 0, 0, 0, TimeSpan.FromHours(-5));
        SchedulePreview p = State("*/30 * * * *").Preview(now, Eastern);
        T.True("every run that day is marked", p.Runs.Count == 8 && p.Runs.All(r => r.IsClockChange && r.Text.Contains("(clock change)", StringComparison.Ordinal)));
        T.True("explained", p.Notes.Any(n => n.Contains("clock", StringComparison.Ordinal)));
        SchedulePreview daily = State("0 8 * * *").Preview(new DateTimeOffset(2027, 3, 13, 12, 0, 0, TimeSpan.FromHours(-5)), Eastern);
        T.True("a plain daily run is not marked", daily.Runs.All(r => !r.IsClockChange));
        SchedulePreview ordinary = State("*/30 * * * *").Preview(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.FromHours(-4)), Eastern);
        T.True("an ordinary day is not marked", ordinary.Runs.All(r => !r.IsClockChange) && ordinary.Notes.Count == 0);
        T.True("fall back day", ScheduleEditorState.IsClockChangeDate(new DateOnly(2026, 11, 1), Eastern));
        T.True("spring forward day", ScheduleEditorState.IsClockChangeDate(new DateOnly(2027, 3, 14), Eastern));
        T.True("ordinary day", !ScheduleEditorState.IsClockChangeDate(new DateOnly(2027, 3, 15), Eastern));
        T.True("a zone without clock changes", !ScheduleEditorState.IsClockChangeDate(new DateOnly(2027, 3, 14), Fixed));
    }

    public static void NeverRunsAndLogonAndAlternative()
    {
        SchedulePreview never = State("0 0 31 2 *").Preview(Nov25, Fixed);
        T.True("never-runs is an error", never.HasErrors && !never.IsValid);
        SchedulePreview logon = State("@logon").Preview(Nov25, Fixed);
        T.True("logon has no list", logon.IsValid && logon.RunsAtSignIn && logon.Runs.Count == 0);
        T.Eq("logon sentence", "When you sign in", logon.Sentence);
        SchedulePreview reboot = State("@reboot").Preview(Nov25, Fixed);
        T.True("reboot is an error with an alternative", reboot.HasErrors && reboot.AlternativeLines is { Count: 1 } l && l[0] == "@logon");
        T.True("explanation", !string.IsNullOrEmpty(reboot.AlternativeExplanation));
        T.True("errors name the field", State("0 9 15W * *").Preview(Nov25, Fixed).Diagnostics.Any(d => d.Severity == CronSeverity.Error && d.Message.Contains("15W", StringComparison.Ordinal)));
    }

    public static void TimeOfDayWordsDoNotDependOnToday()
    {
        // Two calls with the same inputs agree, and no result depends on the machine's clock.
        T.Eq("same", State("0 8 * * 1-5").Preview(Nov25, Fixed).Runs[0].Text, State("0 8 * * 1-5").Preview(Nov25, Fixed).Runs[0].Text);
    }
}
