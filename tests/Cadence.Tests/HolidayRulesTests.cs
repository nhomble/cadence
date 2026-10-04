using Cadence.Core.Holidays;
using Cadence.Core.Model;
using Cadence.TestHarness;

namespace Cadence.Tests;

public static class HolidayRulesTests
{
    // PLAN 3.7 shared table: date, US federal name (null = open), NYSE name (null = open).
    static readonly (string Date, string? Federal, string? Nyse)[] Table =
    [
        ("2021-12-31", "New Year's Day (observed)", null),
        ("2022-06-20", "Juneteenth (observed)", "Juneteenth (observed)"),
        ("2021-06-18", "Juneteenth (observed)", null),
        ("2024-03-29", null, "Good Friday"),
        ("2025-04-18", null, "Good Friday"),
        ("2026-01-01", "New Year's Day", "New Year's Day"),
        ("2026-01-19", "Martin Luther King Jr. Day", "Martin Luther King Jr. Day"),
        ("2026-02-16", "Washington's Birthday", "Washington's Birthday"),
        ("2026-04-03", null, "Good Friday"),
        ("2026-05-25", "Memorial Day", "Memorial Day"),
        ("2026-06-19", "Juneteenth", "Juneteenth"),
        ("2026-07-03", "Independence Day (observed)", "Independence Day (observed)"),
        ("2026-09-07", "Labor Day", "Labor Day"),
        ("2026-10-12", "Columbus Day", null),
        ("2026-11-11", "Veterans Day", null),
        ("2026-11-26", "Thanksgiving Day", "Thanksgiving Day"),
        ("2026-12-25", "Christmas Day", "Christmas Day"),
        ("2027-03-26", null, "Good Friday"),
        ("2027-06-18", "Juneteenth (observed)", "Juneteenth (observed)"),
        ("2027-07-05", "Independence Day (observed)", "Independence Day (observed)"),
        ("2027-12-24", "Christmas Day (observed)", "Christmas Day (observed)"),
        ("2027-12-31", "New Year's Day (observed)", null),
    ];

    static DateOnly D(string s) => DateOnly.ParseExact(s, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public static void SharedTableBothCalendars()
    {
        foreach (var (date, fed, nyse) in Table)
        {
            T.Eq($"federal {date}", fed, HolidayRules.Find(HolidayCalendar.UsFederal, D(date))?.Name);
            T.Eq($"nyse {date}", nyse, HolidayRules.Find(HolidayCalendar.Nyse, D(date))?.Name);
        }
    }

    public static void NoneCalendarHasNoHolidays()
    {
        T.Eq("For None is empty", 0, HolidayRules.For(HolidayCalendar.None, 2026).Count);
        T.True("Find None is null", HolidayRules.Find(HolidayCalendar.None, D("2026-12-25")) is null);
        T.Eq("Range None is empty", 0, HolidayRules.Range(HolidayCalendar.None, 2026, 2030).Count);
    }

    public static void EasterDates()
    {
        (int Year, string Date)[] rows =
        [
            (2024, "2024-03-31"), (2025, "2025-04-20"), (2026, "2026-04-05"),
            (2027, "2027-03-28"), (2028, "2028-04-16"), (2038, "2038-04-25"),
        ];
        foreach (var (year, date) in rows) T.Eq($"Easter {year}", D(date), HolidayRules.EasterSunday(year));
    }

    public static void CountsPerYear()
    {
        T.Eq("2026 federal", 11, HolidayRules.For(HolidayCalendar.UsFederal, 2026).Count);
        T.Eq("2026 nyse", 10, HolidayRules.For(HolidayCalendar.Nyse, 2026).Count);
        T.Eq("2021 federal (Juneteenth begins, Jan 1 2022 observed in 2021)", 12, HolidayRules.For(HolidayCalendar.UsFederal, 2021).Count);
        T.Eq("2021 nyse (no Juneteenth yet)", 9, HolidayRules.For(HolidayCalendar.Nyse, 2021).Count);
        T.Eq("2020 federal (no Juneteenth)", 10, HolidayRules.For(HolidayCalendar.UsFederal, 2020).Count);
        T.Eq("2022 federal (New Year observed in 2021)", 10, HolidayRules.For(HolidayCalendar.UsFederal, 2022).Count);
        T.Eq("2027 federal (includes Dec 31 for 2028)", 12, HolidayRules.For(HolidayCalendar.UsFederal, 2027).Count);
        T.Eq("2027 nyse", 10, HolidayRules.For(HolidayCalendar.Nyse, 2027).Count);
        T.Eq("2028 nyse (Saturday New Year not observed)", 9, HolidayRules.For(HolidayCalendar.Nyse, 2028).Count);
    }

    public static void ForIsSortedAndInYear()
    {
        foreach (var cal in new[] { HolidayCalendar.UsFederal, HolidayCalendar.Nyse })
        {
            for (int y = 2020; y <= 2040; y++)
            {
                var list = HolidayRules.For(cal, y);
                bool sorted = true, inYear = true;
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0 && list[i - 1].Date >= list[i].Date) sorted = false;
                    if (list[i].Date.Year != y) inYear = false;
                }
                T.True($"{cal} {y} sorted and distinct", sorted);
                T.True($"{cal} {y} dates all in year", inYear);
            }
        }
    }

    public static void FederalYearContainsNextYearsObservedNewYear()
    {
        var f2021 = HolidayRules.For(HolidayCalendar.UsFederal, 2021);
        T.True("For(UsFederal, 2021) contains 2021-12-31", f2021.Any(h => h.Date == D("2021-12-31")));
        T.Eq("For(UsFederal, 2021) last is New Year (observed)", "New Year's Day (observed)", f2021[^1].Name);
        T.True("For(Nyse, 2021) has no Dec 31", HolidayRules.For(HolidayCalendar.Nyse, 2021).All(h => h.Date != D("2021-12-31")));
    }

    public static void RangeSizes()
    {
        T.Eq("federal 2026..2026", 11, HolidayRules.Range(HolidayCalendar.UsFederal, 2026, 2026).Count);
        T.Eq("federal 2026..2028", 11 + 12 + 10, HolidayRules.Range(HolidayCalendar.UsFederal, 2026, 2028).Count);
        T.Eq("nyse 2026..2028", 10 + 10 + 9, HolidayRules.Range(HolidayCalendar.Nyse, 2026, 2028).Count);
        T.Eq("federal 2026..2036 (11 years)", HolidayRules.Range(HolidayCalendar.UsFederal, 2026, 2036).Count,
            Enumerable.Range(2026, 11).Sum(y => HolidayRules.For(HolidayCalendar.UsFederal, y).Count));
        T.Eq("empty range", 0, HolidayRules.Range(HolidayCalendar.Nyse, 2030, 2029).Count);
        var r = HolidayRules.Range(HolidayCalendar.Nyse, 2026, 2027);
        T.Eq("range first", D("2026-01-01"), r[0].Date);
        T.Eq("range last", D("2027-12-24"), r[^1].Date);
    }

    public static void NthAndLastWeekdayRules()
    {
        (string Date, string Name)[] rows =
        [
            ("2027-01-18", "Martin Luther King Jr. Day"), ("2027-02-15", "Washington's Birthday"),
            ("2027-05-31", "Memorial Day"), ("2027-09-06", "Labor Day"), ("2027-11-25", "Thanksgiving Day"),
            ("2025-05-26", "Memorial Day"), ("2025-11-27", "Thanksgiving Day"), ("2025-01-20", "Martin Luther King Jr. Day"),
        ];
        foreach (var (date, name) in rows)
        {
            T.Eq($"federal {date}", name, HolidayRules.Find(HolidayCalendar.UsFederal, D(date))?.Name);
            T.Eq($"nyse {date}", name, HolidayRules.Find(HolidayCalendar.Nyse, D(date))?.Name);
        }
        T.Eq("Columbus Day 2027 (2nd Monday)", "Columbus Day", HolidayRules.Find(HolidayCalendar.UsFederal, D("2027-10-11"))?.Name);
    }

    public static void ObservedDayRules()
    {
        // Independence Day on a Sunday (2021-07-04) is observed Monday; on a Saturday (2026) Friday.
        T.Eq("Jul 4 2021 Sunday -> Mon federal", "Independence Day (observed)", HolidayRules.Find(HolidayCalendar.UsFederal, D("2021-07-05"))?.Name);
        T.Eq("Jul 4 2021 Sunday -> Mon nyse", "Independence Day (observed)", HolidayRules.Find(HolidayCalendar.Nyse, D("2021-07-05"))?.Name);
        T.True("Jul 4 2021 itself is not a day off", HolidayRules.Find(HolidayCalendar.UsFederal, D("2021-07-04")) is null);
        T.True("Jul 3 2026 observed, Jul 4 2026 not", HolidayRules.Find(HolidayCalendar.Nyse, D("2026-07-04")) is null);
        // New Year 2023 is a Sunday: observed Monday Jan 2 in both calendars.
        T.Eq("Jan 1 2023 Sunday -> Jan 2 federal", "New Year's Day (observed)", HolidayRules.Find(HolidayCalendar.UsFederal, D("2023-01-02"))?.Name);
        T.Eq("Jan 1 2023 Sunday -> Jan 2 nyse", "New Year's Day (observed)", HolidayRules.Find(HolidayCalendar.Nyse, D("2023-01-02"))?.Name);
        // Christmas 2022 is a Sunday -> Monday Dec 26.
        T.Eq("Dec 25 2022 Sunday -> Dec 26", "Christmas Day (observed)", HolidayRules.Find(HolidayCalendar.Nyse, D("2022-12-26"))?.Name);
        // Veterans Day 2023 is a Saturday -> Fri Nov 10 (federal).
        T.Eq("Nov 11 2023 Saturday -> Nov 10", "Veterans Day (observed)", HolidayRules.Find(HolidayCalendar.UsFederal, D("2023-11-10"))?.Name);
    }

    public static void JuneteenthStartYears()
    {
        T.True("federal 2020-06-19 open", HolidayRules.Find(HolidayCalendar.UsFederal, D("2020-06-19")) is null);
        T.Eq("federal 2021-06-18 holiday", "Juneteenth (observed)", HolidayRules.Find(HolidayCalendar.UsFederal, D("2021-06-18"))?.Name);
        T.True("nyse 2021-06-18 open", HolidayRules.Find(HolidayCalendar.Nyse, D("2021-06-18")) is null);
        T.Eq("nyse 2022-06-20 holiday", "Juneteenth (observed)", HolidayRules.Find(HolidayCalendar.Nyse, D("2022-06-20"))?.Name);
        T.Eq("nyse 2023-06-19 holiday", "Juneteenth", HolidayRules.Find(HolidayCalendar.Nyse, D("2023-06-19"))?.Name);
    }

    public static void GoodFridayIsEasterMinusTwo()
    {
        for (int y = 2020; y <= 2040; y++)
        {
            DateOnly gf = HolidayRules.EasterSunday(y).AddDays(-2);
            T.Eq($"Good Friday {y} is NYSE", "Good Friday", HolidayRules.Find(HolidayCalendar.Nyse, gf)?.Name);
            T.True($"Good Friday {y} is not federal", HolidayRules.Find(HolidayCalendar.UsFederal, gf) is null);
            T.Eq($"Easter {y} is a Sunday", DayOfWeek.Sunday, HolidayRules.EasterSunday(y).DayOfWeek);
        }
    }

    // [Review R] Every observed date 2027-2035, derived independently of HolidayRules (a day-by-day walk in a separate script, Easter from
    // published tables), so a rule regression in ANY year of the embedded 10-year window is caught, not only in the sampled rows above.
    static readonly (int Year, string Federal, string Nyse)[] Independent =
    [
        (2027, "01-01,01-18,02-15,05-31,06-18,07-05,09-06,10-11,11-11,11-25,12-24,12-31", "01-01,01-18,02-15,03-26,05-31,06-18,07-05,09-06,11-25,12-24"),
        (2028, "01-17,02-21,05-29,06-19,07-04,09-04,10-09,11-10,11-23,12-25", "01-17,02-21,04-14,05-29,06-19,07-04,09-04,11-23,12-25"),
        (2029, "01-01,01-15,02-19,05-28,06-19,07-04,09-03,10-08,11-12,11-22,12-25", "01-01,01-15,02-19,03-30,05-28,06-19,07-04,09-03,11-22,12-25"),
        (2030, "01-01,01-21,02-18,05-27,06-19,07-04,09-02,10-14,11-11,11-28,12-25", "01-01,01-21,02-18,04-19,05-27,06-19,07-04,09-02,11-28,12-25"),
        (2031, "01-01,01-20,02-17,05-26,06-19,07-04,09-01,10-13,11-11,11-27,12-25", "01-01,01-20,02-17,04-11,05-26,06-19,07-04,09-01,11-27,12-25"),
        (2032, "01-01,01-19,02-16,05-31,06-18,07-05,09-06,10-11,11-11,11-25,12-24,12-31", "01-01,01-19,02-16,03-26,05-31,06-18,07-05,09-06,11-25,12-24"),
        (2033, "01-17,02-21,05-30,06-20,07-04,09-05,10-10,11-11,11-24,12-26", "01-17,02-21,04-15,05-30,06-20,07-04,09-05,11-24,12-26"),
        (2034, "01-02,01-16,02-20,05-29,06-19,07-04,09-04,10-09,11-10,11-23,12-25", "01-02,01-16,02-20,04-07,05-29,06-19,07-04,09-04,11-23,12-25"),
        (2035, "01-01,01-15,02-19,05-28,06-19,07-04,09-03,10-08,11-12,11-22,12-25", "01-01,01-15,02-19,03-23,05-28,06-19,07-04,09-03,11-22,12-25"),
    ];

    public static void IndependentTable2027To2035()
    {
        foreach (var (year, fed, nyse) in Independent)
        {
            string Actual(HolidayCalendar cal) => string.Join(",", HolidayRules.For(cal, year).Select(h => h.Date.ToString("MM-dd", System.Globalization.CultureInfo.InvariantCulture)));
            T.Eq($"federal {year}", fed, Actual(HolidayCalendar.UsFederal));
            T.Eq($"nyse {year}", nyse, Actual(HolidayCalendar.Nyse));
        }
    }
}
