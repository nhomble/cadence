using Cadence.Core.Model;
using Cadence.Cron;
using Cadence.TestHarness;

namespace Cadence.Tests;

/// <summary>The contract of the cron module itself (Cadence.Cron) and of the bit parity that Core's adapter (CronSyntax) relies on.</summary>
public static class CronModuleTests
{
    public static void CronListHasStructuralEqualityAndHash()
    {
        var a = new CronList<int>([1, 2, 3]);
        var b = new CronList<int>([1, 2, 3]);
        var c = new CronList<int>([1, 2]);
        T.True("equal lists are equal", a.Equals(b) && a.Equals((object)b) && a.Equals(a));
        T.Eq("equal lists have the same hash", a.GetHashCode(), b.GetHashCode());
        T.True("different lists differ", !a.Equals(c));
        var other = new CronList<int>([1, 2, 3]);
        T.True("not equal to null or to another type", !other.Equals((CronList<int>?)null) && !other.Equals((object)"x"));
        T.True("the empty list equals another empty list", CronList<int>.Empty.Equals(new CronList<int>([])));
        var d = new CronList<int>([1, 2, 3]);
        T.Eq("count", 3, d.Count);
        T.Eq("indexer", 2, d[1]);
        T.Eq("text", "[1, 2, 3]", d.ToString());
        T.True("records over lists compare structurally", new CronDom(true, a, false).Equals(new CronDom(true, b, false)) && !new CronDom(true, a, false).Equals(new CronDom(true, c, false)));
        T.Throws<ArgumentNullException>("null items", () => _ = new CronList<int>(null!));
    }

    public static void TheFlagEnumsHaveTheSameBitsAsCoresModel()
    {
        foreach (CronDays d in Enum.GetValues<CronDays>())
            T.Eq("CronDays." + d, (int)Enum.Parse<WeekDays>(d.ToString()), (int)d);
        foreach (CronMonths m in Enum.GetValues<CronMonths>())
            T.Eq("CronMonths." + m, (int)Enum.Parse<MonthSet>(m.ToString()), (int)m);
        foreach (DayOfWeek d in Enum.GetValues<DayOfWeek>())
            T.Eq("bit of " + d, 1 << (int)d, (int)Enum.Parse<CronDays>(d.ToString()));
        T.Eq("the first week", (int)WeekOfMonth.First, 1);
        T.Eq("the fourth week", (int)WeekOfMonth.Fourth, 4);
        T.Eq("the last week", (int)WeekOfMonth.Last, CronNth.Last);
    }

    public static void TheParserWorksOnItsOwn()
    {
        var notes = new List<CronNote>();
        CronExpression e = CronParser.Parse("*/20 8-9 L * 5L,1#2", 3, notes)!;
        T.Eq("no notes", 0, notes.Count);
        T.Eq("minutes", "[0, 20, 40]", e.Minutes.ToString());
        T.Eq("hours", "[8, 9]", e.Hours.ToString());
        T.True("day of month: not a star, the last day", !e.Dom.Star && e.Dom.Last && e.Dom.Days.Count == 0);
        T.Eq("months", CronMonths.All, e.Months);
        T.True("day of week: the last Friday and the second Monday", !e.Dow.Star && e.Dow.Days == CronDays.None
            && e.Dow.Nth.SequenceEqual([new CronNth(2, DayOfWeek.Monday), new CronNth(CronNth.Last, DayOfWeek.Friday)]));

        notes.Clear();
        T.True("a bad line gives null", CronParser.Parse("61 * * * *", 7, notes) is null);
        T.True("and an Error note that names the line", notes.Count == 1 && notes[0].Level == CronNoteLevel.Error && notes[0].Line == 7 && notes[0].Field == "minute"
            && notes[0].Message == "line 7, minute: 61 is out of range 0-59");

        notes.Clear();
        CronExpression logon = CronParser.Parse("@logon", 1, notes)!;
        T.True("@logon", logon.AtLogon && notes.Count == 0 && !CronMatcher.Matches(logon, new DateTime(2026, 10, 1, 8, 0, 0)));
    }

    public static void TheMatcherFollowsTheDayRule()
    {
        CronExpression both = CronParser.Parse("0 9 13 * 5", 1, [])!;
        T.True("the 13th (a Tuesday) matches", CronMatcher.MatchesDate(both, new DateOnly(2026, 10, 13)));
        T.True("a Friday matches", CronMatcher.MatchesDate(both, new DateOnly(2026, 10, 2)));
        T.True("another day does not", !CronMatcher.MatchesDate(both, new DateOnly(2026, 10, 14)));
        CronExpression last = CronParser.Parse("0 9 L 2 *", 1, [])!;
        T.True("the last day of a leap February", CronMatcher.MatchesDate(last, new DateOnly(2028, 2, 29)));
        T.True("a union of lines", CronMatcher.Matches([both, last], new DateTime(2028, 2, 29, 9, 0, 0)) && !CronMatcher.Matches([both, last], new DateTime(2028, 2, 29, 9, 1, 0)));
    }
}