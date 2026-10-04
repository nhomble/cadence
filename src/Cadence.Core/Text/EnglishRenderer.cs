using System.Globalization;
using Cadence.Core.Cron;
using Cadence.Core.Model;

namespace Cadence.Core.Text;

/// <summary>
/// Plain English for a compiled schedule (PLAN.md 3.5). Works on the IR groups the compiler produced, so the text
/// always describes exactly what Task Scheduler will do. Invariant culture, 24-hour H:mm, never any cron syntax.
/// </summary>
public static class EnglishRenderer
{
    const string InvalidText = "Invalid schedule";

    static readonly string[] MonthNames =
    [
        "January", "February", "March", "April", "May", "June",
        "July", "August", "September", "October", "November", "December",
    ];

    static readonly (WeekDays Flag, string Name)[] DayNames =
    [
        (WeekDays.Monday, "Monday"), (WeekDays.Tuesday, "Tuesday"), (WeekDays.Wednesday, "Wednesday"),
        (WeekDays.Thursday, "Thursday"), (WeekDays.Friday, "Friday"), (WeekDays.Saturday, "Saturday"),
        (WeekDays.Sunday, "Sunday"),
    ];

    /// <summary>Compiles the spec, then renders it. An invalid schedule gives "Invalid schedule".</summary>
    public static string Describe(ScheduleSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var compiled = CronCompiler.Compile(spec, new MapContext(""));
        return Describe(compiled, spec.SkipHolidays);
    }

    public static string Describe(CompiledSchedule compiled, HolidayCalendar skip)
    {
        ArgumentNullException.ThrowIfNull(compiled);
        if (!compiled.Success || (compiled.Groups.Count == 0 && !compiled.AtLogon)) return InvalidText;

        var pieces = new List<string>();
        if (compiled.AtLogon) pieces.Add("When you sign in");

        var groups = compiled.Groups;
        int i = 0;
        while (i < groups.Count)
        {
            int cluster = HourlyClusterLength(groups, i);
            if (cluster >= 2)
            {
                pieces.Add(HourlyCluster(groups, i, cluster));
                i += cluster;
            }
            else
            {
                pieces.Add(Single(groups[i]));
                i++;
            }
        }

        var sb = new System.Text.StringBuilder(pieces[0]);
        for (int k = 1; k < pieces.Count; k++)
        {
            sb.Append("; and ").Append(char.ToLowerInvariant(pieces[k][0])).Append(pieces[k].AsSpan(1));
        }
        sb.Append(HolidayClause(skip));
        return sb.ToString();
    }

    /// <summary>The clause <see cref="Describe(CompiledSchedule, HolidayCalendar)"/> appends for a holiday calendar ("" for none).</summary>
    public static string HolidayClause(HolidayCalendar skip) => skip switch
    {
        HolidayCalendar.Nyse => ", except NYSE market holidays",
        HolidayCalendar.UsFederal => ", except US federal holidays",
        _ => "",
    };

    // ----- groups ---------------------------------------------------------------------------------

    static string Single(ScheduleGroup g)
    {
        var (prefix, suffix) = Days(g.Days);
        string note = Note(g.Days);
        string text;
        switch (g.Times)
        {
            case AtTimes at:
                text = prefix + " at " + Ordinals.JoinAnd([.. at.Times.Select(Clock)]);
                break;
            case RepeatEvery r:
                text = RepeatCore(r) + (suffix is null ? "" : ", " + suffix);
                break;
            default:
                return InvalidText;
        }
        return note.Length == 0 ? text : text + " " + note;
    }

    static string RepeatCore(RepeatEvery r)
    {
        bool fullDay = r.First == new TimeOnly(0, 0) && r.Last.ToTimeSpan() + r.Every == TimeSpan.FromHours(24);
        bool hourly = r.Every == TimeSpan.FromHours(1);
        if (hourly && fullDay) return "Every hour, on the hour";
        string text = "Every " + Interval(r.Every);
        if (hourly && r.First.Hour == 0 && r.Last.Hour == 23 && r.First.Minute == r.Last.Minute)
        {
            return text + " at :" + r.First.Minute.ToString("00", CultureInfo.InvariantCulture);
        }
        return fullDay ? text : text + " from " + Clock(r.First) + " to " + Clock(r.Last);
    }

    // Consecutive hourly repeats that only differ in the minute (strategy C of the compiler): "Every hour at :00, :07 ...".
    static int HourlyClusterLength(ValueList<ScheduleGroup> groups, int start)
    {
        if (!IsHourly(groups[start], out var first)) return 0;
        int n = 1;
        while (start + n < groups.Count
            && IsHourly(groups[start + n], out var other)
            && groups[start + n].Days.Equals(groups[start].Days)
            && other.First.Hour == first.First.Hour
            && other.Last.Hour == first.Last.Hour)
        {
            n++;
        }
        return n;
    }

    static bool IsHourly(ScheduleGroup g, out RepeatEvery r)
    {
        if (g.Times is RepeatEvery rep && rep.Every == TimeSpan.FromHours(1) && rep.First.Minute == rep.Last.Minute)
        {
            r = rep;
            return true;
        }
        r = null!;
        return false;
    }

    static string HourlyCluster(ValueList<ScheduleGroup> groups, int start, int count)
    {
        var first = (RepeatEvery)groups[start].Times;
        var (_, suffix) = Days(groups[start].Days);
        string note = Note(groups[start].Days);
        var minutes = new List<string>();
        for (int k = 0; k < count; k++)
        {
            minutes.Add(":" + ((RepeatEvery)groups[start + k].Times).First.Minute.ToString("00", CultureInfo.InvariantCulture));
        }
        bool fullDay = first.First.Hour == 0 && first.Last.Hour == 23;
        string text = "Every hour"
            + (fullDay ? "" : " from " + first.First.Hour.ToString(CultureInfo.InvariantCulture) + ":00 to "
                + first.Last.Hour.ToString(CultureInfo.InvariantCulture) + ":59")
            + " at " + Ordinals.JoinAnd(minutes)
            + (suffix is null ? "" : ", " + suffix);
        return note.Length == 0 ? text : text + " " + note;
    }

    // ----- time pieces ----------------------------------------------------------------------------

    static string Clock(TimeOnly t) =>
        t.Hour.ToString(CultureInfo.InvariantCulture) + ":" + t.Minute.ToString("00", CultureInfo.InvariantCulture);

    static string Interval(TimeSpan every)
    {
        int minutes = (int)every.TotalMinutes;
        if (minutes % 60 == 0)
        {
            int h = minutes / 60;
            return h == 1 ? "hour" : h.ToString(CultureInfo.InvariantCulture) + " hours";
        }
        return minutes == 1 ? "minute" : minutes.ToString(CultureInfo.InvariantCulture) + " minutes";
    }

    // ----- day pieces -----------------------------------------------------------------------------

    // Prefix starts a sentence ("Every weekday"); Suffix follows an interval phrase ("on weekdays"), null = every day.
    static (string Prefix, string? Suffix) Days(DayRule rule)
    {
        switch (rule)
        {
            case EveryDay:
                return ("Every day", null);
            case WeeklyOn w:
                return Weekly(w.Days, MonthSet.All);
            case MonthlyOnDays m:
                return MonthlyDays(m);
            case MonthlyOnWeekdays mw:
                return MonthlyWeekdays(mw);
            default:
                return ("Every day", null);
        }
    }

    static (string, string?) Weekly(WeekDays days, MonthSet months)
    {
        string inMonths = months == MonthSet.All ? "" : " in " + MonthList(months);
        if (days == WeekDays.All) return ("Every day" + inMonths, months == MonthSet.All ? null : "in " + MonthList(months));
        string prefix = "Every " + DayList(days) + inMonths;
        string suffix = days == WeekDays.Weekdays ? "on weekdays" : "on " + DayList(days);
        return (prefix, suffix + inMonths);
    }

    static (string, string?) MonthlyDays(MonthlyOnDays m)
    {
        bool allDays = m.Days.Count >= 31;
        if (allDays)
        {
            return m.Months == MonthSet.All
                ? ("Every day", null)
                : ("Every day in " + MonthList(m.Months), "in " + MonthList(m.Months));
        }
        if (m.Days.Count == 1 && m.Days[0] == 29 && !m.LastDay && m.Months == MonthSet.February)
        {
            return ("On 29 February", "on 29 February");
        }
        var items = m.Days.Select(Ordinals.Of).ToList();
        if (m.LastDay) items.Add("last day");
        string phrase = "the " + Ordinals.JoinAnd(items) + " of " + (m.Months == MonthSet.All ? "every month" : MonthList(m.Months));
        return ("On " + phrase, "on " + phrase);
    }

    static (string, string?) MonthlyWeekdays(MonthlyOnWeekdays mw)
    {
        bool everyWeek = mw.Weeks.Contains(WeekOfMonth.First) && mw.Weeks.Contains(WeekOfMonth.Second)
            && mw.Weeks.Contains(WeekOfMonth.Third) && mw.Weeks.Contains(WeekOfMonth.Fourth) && mw.Weeks.Contains(WeekOfMonth.Last);
        if (everyWeek) return Weekly(mw.Days, mw.Months);
        string weeks = Ordinals.JoinAnd([.. mw.Weeks.OrderBy(w => (int)w).Select(Ordinals.WeekWord)]);
        string days = Ordinals.JoinAnd([.. DayNames.Where(d => (mw.Days & d.Flag) != 0).Select(d => d.Name)]);
        string phrase = "the " + weeks + " " + days + " of " + (mw.Months == MonthSet.All ? "every month" : MonthList(mw.Months));
        return ("On " + phrase, "on " + phrase);
    }

    static string Note(DayRule rule)
    {
        if (rule is not MonthlyOnDays m || m.Days.Count == 0 || m.Days.Count >= 31) return "";
        if (m.Days.Count == 1 && m.Days[0] == 29 && !m.LastDay && m.Months == MonthSet.February) return "(only in leap years)";
        bool Has(MonthSet s) => (m.Months & s) != 0;
        const MonthSet Short31 = MonthSet.February | MonthSet.April | MonthSet.June | MonthSet.September | MonthSet.November;
        bool fewer = (m.Days.Contains(31) && Has(Short31)) || (m.Days.Contains(30) && Has(MonthSet.February));
        if (fewer) return "(skipped in months with fewer days)";
        if (m.Days.Contains(29) && Has(MonthSet.February)) return "(skipped in February, except in leap years)";
        return "";
    }

    static string DayList(WeekDays days)
    {
        if (days == WeekDays.Weekdays) return "weekday";
        if (days == WeekDays.All) return "day";
        return Ordinals.JoinAnd([.. DayNames.Where(d => (days & d.Flag) != 0).Select(d => d.Name)]);
    }

    static string MonthList(MonthSet months) =>
        Ordinals.JoinAnd([.. MonthNames.Where((_, idx) => ((int)months & (1 << idx)) != 0)]);
}
