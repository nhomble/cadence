namespace Cadence.Cron;

/// <summary>
/// A direct implementation of cron semantics on a parsed <see cref="CronExpression"/>, independent of any
/// compiler or next-runs engine, so tests can use it as an oracle for them. Day semantics follow the dialect:
/// when both day-of-month and day-of-week are restricted (not written as `*` or `?`) a date matches if EITHER
/// matches; otherwise only the restricted one counts. `@logon` lines never match a minute.
/// </summary>
public static class CronMatcher
{
    public static bool Matches(CronExpression e, DateTime local) =>
        !e.AtLogon && Has(e.Hours, local.Hour) && Has(e.Minutes, local.Minute) && MatchesDate(e, DateOnly.FromDateTime(local));

    /// <summary>True when any line matches (a schedule is the union of its lines).</summary>
    public static bool Matches(IEnumerable<CronExpression> lines, DateTime local)
    {
        foreach (var e in lines) if (Matches(e, local)) return true;
        return false;
    }

    /// <summary>Month and day rules only (the time of day is ignored).</summary>
    public static bool MatchesDate(CronExpression e, DateOnly d)
    {
        if (e.AtLogon) return false;
        if (((int)e.Months & (1 << (d.Month - 1))) == 0) return false;
        bool domRestricted = !e.Dom.Star, dowRestricted = !e.Dow.Star;
        if (domRestricted && dowRestricted) return DomMatches(e.Dom, d) || DowMatches(e.Dow, d);
        if (domRestricted) return DomMatches(e.Dom, d);
        if (dowRestricted) return DowMatches(e.Dow, d);
        return true;
    }

    static bool DomMatches(CronDom dom, DateOnly d) =>
        Has(dom.Days, d.Day) || (dom.Last && d.Day == DateTime.DaysInMonth(d.Year, d.Month));

    static bool DowMatches(CronDow dow, DateOnly d)
    {
        if (((int)dow.Days & (1 << (int)d.DayOfWeek)) != 0) return true;
        int dim = DateTime.DaysInMonth(d.Year, d.Month);
        for (int i = 0; i < dow.Nth.Count; i++)
        {
            var n = dow.Nth[i];
            if (n.Day != d.DayOfWeek) continue;
            if (n.Week == CronNth.Last)
            {
                if (d.Day > dim - 7) return true;
            }
            else
            {
                int k = n.Week;
                if (d.Day >= 7 * k - 6 && d.Day <= 7 * k) return true;
            }
        }
        return false;
    }

    static bool Has(CronList<int> set, int value)
    {
        for (int i = 0; i < set.Count; i++) if (set[i] == value) return true;
        return false;
    }
}
