using Cadence.Core.Model;
using Cadence.TestHarness;

namespace Cadence.Tests;

public static class ValueListTests
{
    public static void EqualityIsStructural()
    {
        var a = ValueList.Of(1, 2, 3);
        var b = new ValueList<int>([1, 2, 3]);
        T.True("equal lists", a.Equals(b));
        T.True("Equals(object)", a.Equals((object)b));
        T.True("not equal: different element", !a.Equals(ValueList.Of(1, 2, 4)));
        T.True("not equal: different length", !a.Equals(ValueList.Of(1, 2)));
        T.True("not equal: order matters", !a.Equals(ValueList.Of(3, 2, 1)));
        ValueList<int>? none = null;
        T.True("not equal: null", !a.Equals(none));
        var same = a;
        T.True("equal: same reference", same.Equals(a));
    }

    public static void HashCodeMatchesEquality()
    {
        T.Eq("equal lists hash equal", ValueList.Of("a", "b").GetHashCode(), new ValueList<string>(["a", "b"]).GetHashCode());
        T.True("different lists usually hash differently", ValueList.Of(1, 2, 3).GetHashCode() != ValueList.Of(3, 2, 1).GetHashCode());
        var set = new HashSet<ValueList<int>> { ValueList.Of(1, 2) };
        T.True("usable as a hash key", set.Contains(new ValueList<int>([1, 2])));
    }

    public static void EmptyList()
    {
        T.Eq("Empty.Count", 0, ValueList<int>.Empty.Count);
        T.True("Empty equals a new empty list", ValueList<int>.Empty.Equals(new ValueList<int>([])));
        T.Eq("Empty hash equals empty hash", ValueList<string>.Empty.GetHashCode(), new ValueList<string>([]).GetHashCode());
        T.True("Of() with no items is empty", ValueList.Of<int>().Equals(ValueList<int>.Empty));
        T.Eq("Empty enumerates nothing", 0, ValueList<int>.Empty.Count());
    }

    public static void IndexerCountAndEnumeration()
    {
        var l = ValueList.Of("x", "y", "z");
        T.Eq("Count", 3, l.Count);
        T.Eq("indexer 0", "x", l[0]);
        T.Eq("indexer 2", "z", l[2]);
        T.Eq("enumeration order", "x,y,z", string.Join(",", l));
        T.Throws<IndexOutOfRangeException>("indexer out of range throws", () => _ = l[3]);
    }

    public static void IsImmutableCopy()
    {
        var source = new List<int> { 1, 2, 3 };
        var l = new ValueList<int>(source);
        source[0] = 99;
        source.Add(4);
        T.Eq("source edits do not leak in (value)", 1, l[0]);
        T.Eq("source edits do not leak in (count)", 3, l.Count);
    }

    public static void NullItemsThrow() =>
        T.Throws<ArgumentNullException>("null source throws", () => _ = new ValueList<int>(null!));

    public static void WorksInsideRecords()
    {
        var d = new DateOnly(2026, 10, 1);
        var s1 = new ScheduleSpec(ValueList.Of("0 8 * * 1-5"), HolidayCalendar.Nyse, d);
        var s2 = new ScheduleSpec(new ValueList<string>(["0 8 * * 1-5"]), HolidayCalendar.Nyse, d);
        var s3 = new ScheduleSpec(ValueList.Of("0 9 * * 1-5"), HolidayCalendar.Nyse, d);
        T.True("records with equal lists are equal", s1 == s2);
        T.Eq("record hash codes match", s1.GetHashCode(), s2.GetHashCode());
        T.True("records with different lists differ", s1 != s3);
        var t1 = new MonthlyTrigger(ValueList.Of(1, 15), true, MonthSet.All);
        var t2 = new MonthlyTrigger(ValueList.Of(1, 15), true, MonthSet.All);
        T.True("trigger records are structurally equal", t1 == t2);
    }
}
