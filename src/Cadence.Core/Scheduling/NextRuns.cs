using Cadence.Core.Holidays;
using Cadence.Core.Model;

namespace Cadence.Core.Scheduling;

/// <summary>The next-runs engine: Task Scheduler trigger semantics measured in PLAN 2.3, DST policy per PLAN 3.4.</summary>
public static class NextRuns
{
    /// <summary>The next <paramref name="count"/> runs strictly after <paramref name="after"/>.</summary>
    public static IReadOnlyList<Occurrence> Next(ValueList<TaskTrigger> triggers, DateTimeOffset after, int count, TimeZoneInfo tz, int horizonDays = 3660)
    {
        if (count <= 0) return [];
        DateOnly afterDate = LocalDate(after, tz);
        DateTimeOffset horizonEnd = WallClock.Resolve(afterDate.AddDays(horizonDays + 1).ToDateTime(TimeOnly.MinValue), tz).At;
        return Stream(triggers, after, tz, horizonEnd).Take(count).ToList();
    }

    /// <summary>All runs in [<paramref name="fromInclusive"/>, <paramref name="toInclusive"/>], at most <paramref name="max"/>.</summary>
    public static IReadOnlyList<Occurrence> Between(ValueList<TaskTrigger> triggers, DateTimeOffset fromInclusive, DateTimeOffset toInclusive, TimeZoneInfo tz, int max = 100_000)
    {
        if (max <= 0 || toInclusive < fromInclusive) return [];
        DateTimeOffset horizonEnd = toInclusive.AddTicks(1);
        return Stream(triggers, fromInclusive.AddTicks(-1), tz, horizonEnd).TakeWhile(o => o.At <= toInclusive).Take(max).ToList();
    }

    /// <summary>Preview overload: runs on a holiday of <paramref name="skip"/> get SkippedHoliday set (they still count toward <paramref name="count"/>).</summary>
    public static IReadOnlyList<Occurrence> Next(ValueList<TaskTrigger> triggers, HolidayCalendar skip, DateTimeOffset after, int count, TimeZoneInfo tz)
    {
        IReadOnlyList<Occurrence> runs = Next(triggers, after, count, tz);
        if (skip == HolidayCalendar.None) return runs;
        var result = new List<Occurrence>(runs.Count);
        foreach (Occurrence o in runs)
        {
            Holiday? h = HolidayRules.Find(skip, LocalDate(o.At, tz));
            result.Add(h is null ? o : o with { SkippedHoliday = h.Name });
        }
        return result;
    }

    static DateOnly LocalDate(DateTimeOffset at, TimeZoneInfo tz) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, tz).DateTime);

    // Merge of the per-trigger streams, time ordered, equal instants deduplicated (lowest trigger index wins).
    static IEnumerable<Occurrence> Stream(ValueList<TaskTrigger> triggers, DateTimeOffset after, TimeZoneInfo tz, DateTimeOffset horizonEnd)
    {
        var pq = new PriorityQueue<(IEnumerator<Occurrence> E, int Idx), (DateTimeOffset At, int Idx)>();
        var all = new List<IEnumerator<Occurrence>>();
        try
        {
            for (int i = 0; i < triggers.Count; i++)
            {
                TaskTrigger t = triggers[i];
                if (!t.Enabled || t is LogonTrigger or UnsupportedTrigger) continue;
                IEnumerator<Occurrence> e = TriggerStream(t, i, after, tz, horizonEnd).GetEnumerator();
                all.Add(e);
                if (e.MoveNext()) pq.Enqueue((e, i), (e.Current.At, i));
            }
            bool has = false;
            DateTimeOffset last = default;
            while (pq.TryDequeue(out var item, out _))
            {
                Occurrence cur = item.E.Current;
                if (!has || cur.At != last)
                {
                    yield return cur;
                    last = cur.At;
                    has = true;
                }
                if (item.E.MoveNext()) pq.Enqueue(item, (item.E.Current.At, item.Idx));
            }
        }
        finally
        {
            foreach (IEnumerator<Occurrence> e in all) e.Dispose();
        }
    }

    static IEnumerable<Occurrence> TriggerStream(TaskTrigger t, int idx, DateTimeOffset after, TimeZoneInfo tz, DateTimeOffset horizonEnd)
    {
        if (t is TimeTrigger)
        {
            var b = WallClock.Resolve(t.StartBoundary, tz);
            return Expand(t, idx, b, after, tz, horizonEnd, null, timeTrigger: true);
        }
        return CalendarStream(t, idx, after, tz, horizonEnd);
    }

    static IEnumerable<Occurrence> CalendarStream(TaskTrigger t, int idx, DateTimeOffset after, TimeZoneInfo tz, DateTimeOffset horizonEnd)
    {
        var s = DateOnly.FromDateTime(t.StartBoundary);
        DateOnly afterDate = LocalDate(after, tz);
        Repetition? rep = Repeats(t);
        int back = rep is null ? 1 : rep.Duration is { } d ? (int)Math.Ceiling(d.TotalDays) + 1 : 400;
        DateOnly from = afterDate.AddDays(-back);
        if (from < s) from = s;
        DateOnly lastDate = LocalDate(horizonEnd, tz);

        using IEnumerator<(DateTimeOffset At, bool Adj)> bases = BaseOccurrences(t, s, from, lastDate, tz).GetEnumerator();
        var pq = new PriorityQueue<IEnumerator<Occurrence>, DateTimeOffset>();
        var open = new List<IEnumerator<Occurrence>>();
        try
        {
            bool hasCur = bases.MoveNext();
            (DateTimeOffset At, bool Adj) cur = hasCur ? bases.Current : default;
            bool has = false;
            DateTimeOffset last = default;
            while (true)
            {
                if (hasCur && (!pq.TryPeek(out _, out DateTimeOffset minAt) || cur.At <= minAt))
                {
                    bool hasNext = bases.MoveNext();
                    (DateTimeOffset At, bool Adj) nxt = hasNext ? bases.Current : default;
                    IEnumerator<Occurrence> e = Expand(t, idx, cur, after, tz, horizonEnd, hasNext ? nxt.At : null, timeTrigger: false).GetEnumerator();
                    open.Add(e);
                    if (e.MoveNext()) pq.Enqueue(e, e.Current.At);
                    cur = nxt;
                    hasCur = hasNext;
                    continue;
                }
                if (!pq.TryDequeue(out IEnumerator<Occurrence>? en, out _)) yield break;
                Occurrence o = en.Current;
                if (!has || o.At != last)
                {
                    yield return o;
                    last = o.At;
                    has = true;
                }
                if (en.MoveNext()) pq.Enqueue(en, en.Current.At);
            }
        }
        finally
        {
            foreach (IEnumerator<Occurrence> e in open) e.Dispose();
        }
    }

    static Repetition? Repeats(TaskTrigger t) => t.Repetition is { } r && r.Interval > TimeSpan.Zero ? r : null;

    // Base occurrences (items 1 and 2): one per matching date, ascending, wall time >= StartBoundary and <= EndBoundary.
    static IEnumerable<(DateTimeOffset At, bool Adj)> BaseOccurrences(TaskTrigger t, DateOnly s, DateOnly from, DateOnly last, TimeZoneInfo tz)
    {
        TimeSpan tod = t.StartBoundary.TimeOfDay;
        for (DateOnly d = from; d <= last; d = d.AddDays(1))
        {
            if (!IsBaseDate(t, d, s)) continue;
            DateTime wall = d.ToDateTime(TimeOnly.MinValue) + tod;
            if (wall < t.StartBoundary) continue;
            if (t.EndBoundary is { } eb && wall > eb) yield break;
            yield return WallClock.Resolve(wall, tz);
        }
    }

    static bool IsBaseDate(TaskTrigger t, DateOnly d, DateOnly s)
    {
        if (d < s) return false;
        switch (t)
        {
            case DailyTrigger dt:
                return (d.DayNumber - s.DayNumber) % Math.Max(1, dt.DaysInterval) == 0;
            case WeeklyTrigger wt:
            {
                if (((int)wt.Days & (1 << (int)d.DayOfWeek)) == 0) return false;
                int weekD = d.DayNumber - (int)d.DayOfWeek;
                int weekS = s.DayNumber - (int)s.DayOfWeek;
                return ((weekD - weekS) / 7) % Math.Max(1, wt.WeeksInterval) == 0;
            }
            case MonthlyTrigger mt:
            {
                if (((int)mt.Months & (1 << (d.Month - 1))) == 0) return false;
                if (mt.LastDay && d.Day == DateTime.DaysInMonth(d.Year, d.Month)) return true;
                foreach (int day in mt.Days) if (day == d.Day) return true;
                return false;
            }
            case MonthlyDowTrigger dw:
            {
                if (((int)dw.Months & (1 << (d.Month - 1))) == 0) return false;
                if (((int)dw.Days & (1 << (int)d.DayOfWeek)) == 0) return false;
                int dim = DateTime.DaysInMonth(d.Year, d.Month);
                foreach (WeekOfMonth w in dw.Weeks)
                {
                    if (w == WeekOfMonth.Last) { if (d.Day > dim - 7) return true; }
                    else
                    {
                        int n = (int)w;
                        if (d.Day >= 7 * n - 6 && d.Day <= 7 * n) return true;
                    }
                }
                return false;
            }
            default:
                return false;
        }
    }

    // Repetition expansion of one base occurrence (item 3): elapsed-time offsets, then EndBoundary and the horizon.
    static IEnumerable<Occurrence> Expand(TaskTrigger t, int idx, (DateTimeOffset At, bool Adj) b, DateTimeOffset after, TimeZoneInfo tz,
        DateTimeOffset horizonEnd, DateTimeOffset? nextBase, bool timeTrigger)
    {
        Repetition? rep = Repeats(t);
        DateTimeOffset baseUtc = b.At.ToUniversalTime();
        TimeSpan delay = t.RandomDelay ?? TimeSpan.Zero;
        long k = 0;
        if (rep is not null && baseUtc <= after) k = (after - baseUtc).Ticks / rep.Interval.Ticks;
        for (; ; k++)
        {
            if (rep is null && k > 0) yield break;
            TimeSpan offset = rep is null ? TimeSpan.Zero : TimeSpan.FromTicks(k * rep.Interval.Ticks);
            if (rep is not null && k > 0)
            {
                if (rep.Duration is { } dur)
                {
                    if (timeTrigger ? offset >= dur : offset > dur) yield break;
                }
                else if (!timeTrigger && nextBase is { } nb && baseUtc + offset >= nb) yield break;
            }
            DateTimeOffset atUtc = baseUtc + offset;
            if (atUtc >= horizonEnd) yield break;
            DateTimeOffset at = TimeZoneInfo.ConvertTime(atUtc, tz);
            if (t.EndBoundary is { } eb && at.DateTime > eb) yield break;
            if (atUtc <= after) continue;
            yield return new Occurrence(at, idx, k == 0 && b.Adj, delay);
        }
    }
}
