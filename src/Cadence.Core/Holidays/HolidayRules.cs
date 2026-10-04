using System.Collections.Concurrent;
using Cadence.Core.Model;

namespace Cadence.Core.Holidays;

/// <summary>US federal (5 U.S.C. 6103) and NYSE holiday rules, computed (no data files). PLAN 3.7.</summary>
public static class HolidayRules
{
    static readonly ConcurrentDictionary<(HolidayCalendar, int), IReadOnlyList<Holiday>> _cache = new();

    /// <summary>Holidays whose observed date falls in <paramref name="year"/>, sorted. Federal may include Dec 31 for next year's New Year's Day.</summary>
    public static IReadOnlyList<Holiday> For(HolidayCalendar cal, int year)
    {
        if (cal == HolidayCalendar.None) return [];
        return _cache.GetOrAdd((cal, year), static k => Compute(k.Item1, k.Item2));
    }

    public static Holiday? Find(HolidayCalendar cal, DateOnly date)
    {
        foreach (Holiday h in For(cal, date.Year))
            if (h.Date == date) return h;
        return null;
    }

    /// <summary>Easter Sunday by the Anonymous Gregorian algorithm (Meeus/Jones/Butcher).</summary>
    public static DateOnly EasterSunday(int year)
    {
        int a = year % 19, b = year / 100, c = year % 100;
        int d = b / 4, e = b % 4, f = (b + 8) / 25, g = (b - f + 1) / 3;
        int h = (19 * a + b - d - g + 15) % 30;
        int i = c / 4, k = c % 4;
        int l = (32 + 2 * e + 2 * i - h - k) % 7;
        int m = (a + 11 * h + 22 * l) / 451;
        int month = (h + l - 7 * m + 114) / 31;
        int day = ((h + l - 7 * m + 114) % 31) + 1;
        return new DateOnly(year, month, day);
    }

    public static ValueList<Holiday> Range(HolidayCalendar cal, int fromYear, int toYearInclusive)
    {
        var all = new List<Holiday>();
        for (int y = fromYear; y <= toYearInclusive; y++) all.AddRange(For(cal, y));
        return new ValueList<Holiday>(all);
    }

    static IReadOnlyList<Holiday> Compute(HolidayCalendar cal, int year)
    {
        bool nyse = cal == HolidayCalendar.Nyse;
        var list = new List<Holiday>();

        void Fixed(int y, int month, int day, string name, bool saturdayObserved = true)
        {
            var actual = new DateOnly(y, month, day);
            DateOnly obs = actual;
            if (actual.DayOfWeek == DayOfWeek.Saturday)
            {
                if (!saturdayObserved) return;
                obs = actual.AddDays(-1);
            }
            else if (actual.DayOfWeek == DayOfWeek.Sunday) obs = actual.AddDays(1);
            if (obs.Year != year) return;
            list.Add(new Holiday(obs, obs == actual ? name : name + " (observed)"));
        }

        void Add(DateOnly d, string name) => list.Add(new Holiday(d, name));

        // New Year's Day: this year's, and next year's when a Saturday puts the observance on Dec 31 (federal only).
        Fixed(year, 1, 1, "New Year's Day", saturdayObserved: !nyse);
        Fixed(year + 1, 1, 1, "New Year's Day", saturdayObserved: !nyse);
        Add(NthWeekday(year, 1, DayOfWeek.Monday, 3), "Martin Luther King Jr. Day");
        Add(NthWeekday(year, 2, DayOfWeek.Monday, 3), "Washington's Birthday");
        if (nyse) Add(EasterSunday(year).AddDays(-2), "Good Friday");
        Add(LastWeekday(year, 5, DayOfWeek.Monday), "Memorial Day");
        if (year >= (nyse ? 2022 : 2021)) Fixed(year, 6, 19, "Juneteenth");
        Fixed(year, 7, 4, "Independence Day");
        Add(NthWeekday(year, 9, DayOfWeek.Monday, 1), "Labor Day");
        if (!nyse)
        {
            Add(NthWeekday(year, 10, DayOfWeek.Monday, 2), "Columbus Day");
            Fixed(year, 11, 11, "Veterans Day");
        }
        Add(NthWeekday(year, 11, DayOfWeek.Thursday, 4), "Thanksgiving Day");
        Fixed(year, 12, 25, "Christmas Day");

        list.Sort(static (x, y) => x.Date.CompareTo(y.Date));
        return list;
    }

    static DateOnly NthWeekday(int year, int month, DayOfWeek dow, int n)
    {
        var first = new DateOnly(year, month, 1);
        int delta = ((int)dow - (int)first.DayOfWeek + 7) % 7;
        return first.AddDays(delta + 7 * (n - 1));
    }

    static DateOnly LastWeekday(int year, int month, DayOfWeek dow)
    {
        var last = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        int delta = ((int)last.DayOfWeek - (int)dow + 7) % 7;
        return last.AddDays(-delta);
    }
}
