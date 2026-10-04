using Cadence.Core.Model;

namespace Cadence.Core.Scheduling;

/// <summary>How often a schedule fires (PLAN 12.4.5). Pure: used by the agent tools to refuse schedules that run too often.</summary>
public static class ScheduleDensity
{
    /// <summary>The most occurrences looked at in one window; a schedule firing every minute has 20,160 in 14 days, so this is never the limiting factor for the gap.</summary>
    public const int MaxOccurrences = 20_000;

    /// <summary>
    /// [CP-D] How far <see cref="GapForLimit"/> looks: four years and a day, so every month, every day of the month, every weekday combination and a leap day come
    /// round at least once. A 14-day look alone let "every minute on 20 December" (<c>* * 20 12 *</c>) through on 1 October.
    /// </summary>
    public const int HorizonDays = 1462;

    /// <summary>
    /// The smallest positive gap between two consecutive distinct occurrences in the <paramref name="days"/> days from <paramref name="from"/>
    /// (holidays are not considered: a skipped run is still a run Task Scheduler starts). Null when fewer than two occurrences fall in the
    /// window: a schedule that runs at sign-in only, a yearly one, or no triggers at all.
    /// </summary>
    public static TimeSpan? MinimumGap(ValueList<TaskTrigger> triggers, DateTimeOffset from, TimeZoneInfo tz, int days = 14)
    {
        ArgumentNullException.ThrowIfNull(tz);
        if (days < 1) days = 1;
        IReadOnlyList<Occurrence> runs = NextRuns.Between(triggers, from, from.AddDays(days), tz, MaxOccurrences);
        TimeSpan? min = null;
        for (int i = 1; i < runs.Count; i++)
        {
            TimeSpan gap = runs[i].At - runs[i - 1].At;
            if (gap <= TimeSpan.Zero) continue;
            if (min is null || gap < min) min = gap;
        }
        return min;
    }

    /// <summary>
    /// [CP-D] The gap the agents' minimum interval must be judged on: the 14-day <see cref="MinimumGap"/>, unless some later stretch within
    /// <see cref="HorizonDays"/> fires closer together than <paramref name="limit"/>, in which case that (smaller) gap. A schedule that is dense only on some
    /// days of the year (<c>* * 20 12 *</c>, <c>0,1 9 1 1 *</c>, <c>* * 29 2 *</c>) is therefore caught whatever today's date is.
    /// </summary>
    public static TimeSpan? GapForLimit(ValueList<TaskTrigger> triggers, DateTimeOffset from, TimeZoneInfo tz, TimeSpan limit, int horizonDays = HorizonDays)
    {
        TimeSpan? near = MinimumGap(triggers, from, tz);
        if (near is { } n && n < limit) return n;
        // Cron has minute granularity: nothing can be closer than a minute, so a limit of one minute or less needs no longer look.
        if (limit <= TimeSpan.FromMinutes(1)) return near;
        return FirstGapBelow(triggers, from, tz, limit, horizonDays) ?? near;
    }

    /// <summary>
    /// The first positive gap smaller than <paramref name="limit"/> between consecutive occurrences in the <paramref name="horizonDays"/> days from
    /// <paramref name="from"/>, or null when there is none. Looks in 14-day windows and jumps over stretches with no runs, so a sparse schedule is cheap.
    /// </summary>
    public static TimeSpan? FirstGapBelow(ValueList<TaskTrigger> triggers, DateTimeOffset from, TimeZoneInfo tz, TimeSpan limit, int horizonDays = HorizonDays)
    {
        ArgumentNullException.ThrowIfNull(tz);
        if (triggers.Count == 0 || horizonDays < 1) return null;
        DateTimeOffset end = from.AddDays(horizonDays);
        DateTimeOffset cursor = from;
        DateTimeOffset? prev = null;
        while (cursor < end)
        {
            DateTimeOffset windowEnd = cursor.AddDays(14);
            if (windowEnd > end) windowEnd = end;
            IReadOnlyList<Occurrence> runs = NextRuns.Between(triggers, cursor, windowEnd, tz, MaxOccurrences);
            if (runs.Count == 0)
            {
                IReadOnlyList<Occurrence> next = NextRuns.Next(triggers, windowEnd, 1, tz, horizonDays);
                if (next.Count == 0 || next[0].At >= end) return null;
                cursor = next[0].At;
                continue;
            }
            foreach (Occurrence o in runs)
            {
                if (prev is { } p)
                {
                    TimeSpan gap = o.At - p;
                    if (gap > TimeSpan.Zero && gap < limit) return gap;
                }
                prev = o.At;
            }
            // A window cut at MaxOccurrences goes on from its last run; otherwise from the window's end (an occurrence exactly there repeats with gap 0, which is skipped).
            DateTimeOffset last = runs[^1].At;
            cursor = runs.Count >= MaxOccurrences ? last.AddTicks(1) : (windowEnd > last ? windowEnd : last.AddTicks(1));
        }
        return null;
    }
}
