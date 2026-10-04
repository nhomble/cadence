using System.Collections.Concurrent;
using System.Globalization;
using Cadence.Core.Cron;
using Cadence.Core.Holidays;
using Cadence.Core.Model;
using Cadence.Core.Planning;
using Cadence.Core.Scheduling;
using Cadence.Core.Text;

namespace Cadence.App.UI.Logic;

/// <summary>What a schedule's triggers amount to: runs at fixed times, only a sign-in or an event, or nothing at all.</summary>
public enum RateKind { Timed, Rare, AtSignIn, EventOnly, Never }

/// <summary>How often a schedule really runs ([P25] the Rate column and its sort). <see cref="RunsPerWeek"/> is counted, never guessed from the cron text.</summary>
public sealed record RateInfo(RateKind Kind, double RunsPerWeek, string Label, string Tooltip, bool AlsoAtSignIn)
{
    /// <summary>The cell of a schedule whose triggers can't be read.</summary>
    public static RateInfo Unknown { get; } = new(RateKind.Never, 0, "-", "Cadence can't tell how often this runs.", false);
}

/// <summary>
/// The Rate of a schedule: runs per week counted from the triggers that will really fire, and a short label for it ("every 15 min", "weekdays", "3× weekly").
/// Pure (no WPF, no clock of its own). Counting is memoized by <see cref="RateCache"/> so a 30 s refresh never recounts an every-minute job.
/// </summary>
public static class RateFormat
{
    const int WindowDays = 28;
    const int YearDays = 364;
    const int MaxCounted = 50_000;

    /// <summary>
    /// A managed row. A drifted task is measured as Windows will run it (its live triggers); any other task from its compiled cron. The label comes from the
    /// Simple view when the row is in sync and its cron has one, else from the counted rate.
    /// </summary>
    public static RateInfo ForTask(ClassifiedTask task, DateTimeOffset now, TimeZoneInfo tz, RateCache? cache = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        JobDefinition? job = task.Job;
        HolidayCalendar skip = job?.Schedule.SkipHolidays ?? HolidayCalendar.None;
        bool drifted = task.Sync == SyncStatus.Drifted;
        ValueList<TaskTrigger>? triggers = null;
        HumanSchedule? human = null;

        if (drifted && task.Actual is { Triggers.Count: > 0 } live) triggers = live.Triggers;
        else if (job is not null)
        {
            CompiledSchedule compiled = CronCompiler.Compile(job.Schedule, new MapContext("user"));
            if (compiled.Success) triggers = compiled.Triggers;
            // The words come from the cron Cadence wrote; they only describe the counted triggers when those are the same cron (not a drifted task's live triggers).
            if (HumanScheduleMapper.TryFromCron(job.Schedule.CronLines, out HumanSchedule h)) human = h;
        }
        if (triggers is null && task.Actual is { } actual) triggers = actual.Triggers;
        if (triggers is null) return RateInfo.Unknown;
        return Memo(cache, triggers, skip, human, now, tz);
    }

    /// <summary>A template of the editor's Templates list (same column, same rules).</summary>
    public static RateInfo ForTemplate(Template template, DateTimeOffset now, TimeZoneInfo tz, RateCache? cache = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        var spec = new ScheduleSpec(ValueList.Of(template.Cron), template.SuggestedSkip, DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, tz).DateTime));
        CompiledSchedule compiled = CronCompiler.Compile(spec, new MapContext("user"));
        if (!compiled.Success) return RateInfo.Unknown;
        HumanSchedule? human = HumanScheduleMapper.TryFromCron(spec.CronLines, out HumanSchedule h) ? h : null;
        return Memo(cache, compiled.Triggers, template.SuggestedSkip, human, now, tz);
    }

    /// <summary>Somebody else's task: its live triggers, or "-" when they can't be read.</summary>
    public static RateInfo ForForeign(ClassifiedTask task, DateTimeOffset now, TimeZoneInfo tz, RateCache? cache = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.Actual is not { } actual) return RateInfo.Unknown;
        return Memo(cache, actual.Triggers, HolidayCalendar.None, null, now, tz);
    }

    static RateInfo Memo(RateCache? cache, ValueList<TaskTrigger> triggers, HolidayCalendar skip, HumanSchedule? human, DateTimeOffset now, TimeZoneInfo tz)
    {
        if (cache is null) return ForTriggers(triggers, skip, now, tz, human);
        DateOnly today = LocalDate(now, tz);
        return cache.GetOrCompute((triggers, skip, human, today, tz.Id), () => ForTriggers(triggers, skip, now, tz, human));
    }

    /// <summary>Counts the runs of <paramref name="triggers"/> (see 2.1 of the design): 28 days from local midnight, or 364 days when that gives fewer than 8.</summary>
    public static RateInfo ForTriggers(ValueList<TaskTrigger> triggers, HolidayCalendar skip, DateTimeOffset now, TimeZoneInfo tz, HumanSchedule? human = null)
    {
        ArgumentNullException.ThrowIfNull(triggers);
        ArgumentNullException.ThrowIfNull(tz);
        DateOnly today = LocalDate(now, tz);
        DateTimeOffset a = Midnight(today, tz);
        bool hasLogon = triggers.Any(t => t is LogonTrigger && t.Enabled);
        UnsupportedTrigger? unsupported = triggers.OfType<UnsupportedTrigger>().FirstOrDefault(t => t.Enabled);

        int n = Count(triggers, skip, a, Midnight(today.AddDays(WindowDays), tz), tz);
        bool year = false;
        if (n < 8)
        {
            n = Count(triggers, skip, a, Midnight(today.AddDays(YearDays), tz), tz);
            year = true;
        }

        if (n == 0)
        {
            if (hasLogon) return new RateInfo(RateKind.AtSignIn, 0, "at sign-in", "Runs when you sign in.", false);
            if (unsupported is not null)
                return new RateInfo(RateKind.EventOnly, 0, "on an event", "Starts on an event (" + unsupported.ElementName + "). Cadence can't predict it.", false);
            IReadOnlyList<Occurrence> far = NextRuns.Next(triggers, a, 1, tz, horizonDays: 3660);
            if (far.Count > 0)
            {
                double days = Math.Max(1, (LocalDate(far[0].At, tz).DayNumber - today.DayNumber));
                return new RateInfo(RateKind.Rare, 7 / days, "less than yearly", "Runs less than once a year. Next: " + far[0].At.ToString("d MMM yyyy", CultureInfo.InvariantCulture) + ".", false);
            }
            return new RateInfo(RateKind.Never, 0, "never", "No run in the next 10 years.", false);
        }

        double perWeek = year ? n / 52.0 : n / 4.0;
        string label = human is not null ? HumanLabel(human) : CountedLabel(perWeek, triggers, a, tz);
        if (hasLogon) label += " + sign-in";
        return new RateInfo(RateKind.Timed, perWeek, label, Tooltip(perWeek, skip, year, hasLogon), hasLogon);
    }

    // ---- counting ----

    static int Count(ValueList<TaskTrigger> triggers, HolidayCalendar skip, DateTimeOffset from, DateTimeOffset toExclusive, TimeZoneInfo tz)
    {
        IReadOnlyList<Occurrence> runs = NextRuns.Between(triggers, from, toExclusive.AddTicks(-1), tz, MaxCounted);
        if (skip == HolidayCalendar.None) return runs.Count;
        int count = 0;
        foreach (Occurrence o in runs)
            if (HolidayRules.Find(skip, LocalDate(o.At, tz)) is null) count++;   // the wrapper skips a holiday run, so it is not a real run
        return count;
    }

    static DateOnly LocalDate(DateTimeOffset at, TimeZoneInfo tz) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, tz).DateTime);

    static DateTimeOffset Midnight(DateOnly date, TimeZoneInfo tz) => WallClock.Resolve(date.ToDateTime(TimeOnly.MinValue), tz).At;

    // ---- labels ----

    static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);

    static string Times(int k, string word) => k <= 1 ? word : Num(k) + "× " + word;

    static string DaySuffix(WeekDays days)
    {
        if (days == WeekDays.All) return "";
        if (days == WeekDays.Weekdays) return ", weekdays";
        int k = 0;
        for (int bits = (int)days; bits != 0; bits &= bits - 1) k++;
        return ", " + Num(k) + " days/wk";
    }

    static string HumanLabel(HumanSchedule h)
    {
        int times = Math.Max(1, h.Times.Count);
        switch (h.Frequency)
        {
            case HumanFrequency.EveryNMinutes:
                return (h.Interval <= 1 ? "every minute" : "every " + Num(h.Interval) + " min") + DaySuffix(h.Days);
            case HumanFrequency.EveryNHours:
                return (h.Interval <= 1 ? "hourly" : "every " + Num(h.Interval) + " h") + DaySuffix(h.Days);
            case HumanFrequency.Daily:
                return Times(times, "daily");
            case HumanFrequency.Weekdays:
                return Times(times, "weekdays");
            case HumanFrequency.SpecificDays:
            {
                int days = 0;
                for (int bits = (int)h.Days; bits != 0; bits &= bits - 1) days++;
                int n = Math.Max(1, days) * times;
                return n == 1 ? "weekly" : Num(n) + "× weekly";
            }
            case HumanFrequency.MonthlyOnDates:
            {
                int n = Math.Max(1, h.MonthDates.Count + (h.LastDayOfMonth ? 1 : 0)) * times;
                return n == 1 ? "monthly" : Num(n) + "× monthly";
            }
            case HumanFrequency.MonthlyOnWeekday:
                return Times(times, "monthly");
            default:
                return "at sign-in";
        }
    }

    static string Short(double value, string format = "0.#") => value.ToString(format, CultureInfo.InvariantCulture);

    static string CountedLabel(double r, ValueList<TaskTrigger> triggers, DateTimeOffset a, TimeZoneInfo tz)
    {
        if (r >= 168)
        {
            TimeSpan? gap = ScheduleDensity.MinimumGap(triggers, a, tz);
            if (gap is { } g && g > TimeSpan.Zero && Math.Abs(TimeSpan.FromDays(7) / g - r) <= r * 0.02)
            {
                double minutes = Math.Round(g.TotalMinutes);
                return minutes >= 60 && minutes % 60 == 0 ? "every " + Short(minutes / 60) + " h" : "every " + Short(minutes) + " min";
            }
            return "~" + Num((int)Math.Round(r / 7)) + "× daily";
        }
        if (r >= 7)
        {
            double perDay = r / 7;
            int n = (int)Math.Round(perDay);
            if (Math.Abs(perDay - n) < 0.05) return n == 1 ? "daily" : Num(n) + "× daily";
            return "~" + Short(perDay) + "× daily";
        }

        // Fewer than a run a day: "every 3 days", "weekly", "every 2 weeks". A 28 day count of a every-3-days job is 9 or 10, so up to 10% is allowed for short intervals.
        double interval = 7 / r;
        int whole = (int)Math.Round(interval);
        bool near = whole >= 2 && (whole < 7 ? Math.Abs(interval - whole) <= whole * 0.1 : Math.Abs(interval - whole) < 0.05)
            || whole == 1 && Math.Abs(interval - 1) < 0.05;
        if (near)
        {
            if (whole == 1) return "daily";
            if (whole == 7) return "weekly";
            if (whole % 7 == 0) return "every " + Num(whole / 7) + " weeks";
            return "every " + Num(whole) + " days";
        }
        if (r >= 1) return "~" + Short(r) + "× weekly";

        double perMonth = r * 52 / 12;
        int months = (int)Math.Round(perMonth);
        if (months >= 1 && Math.Abs(perMonth - months) <= months * 0.1) return months == 1 ? "monthly" : Num(months) + "× monthly";
        if (perMonth < 1)
        {
            double perYear = r * 52;
            int years = (int)Math.Round(perYear);
            if (years >= 1 && Math.Abs(perYear - years) < 0.05) return years == 1 ? "yearly" : Num(years) + "× yearly";
            return "~" + Short(perYear, "0.##") + "× yearly";
        }
        return "~" + Short(perMonth) + "× monthly";
    }

    static string Tooltip(double perWeek, HolidayCalendar skip, bool year, bool hasLogon)
    {
        string amount = perWeek >= 10 ? Num((int)Math.Round(perWeek)) : perWeek >= 1 ? Short(perWeek) : Short(perWeek, "0.##");
        string text = "About " + amount + " runs a week";
        if (skip != HolidayCalendar.None) text += ", not counting " + (skip == HolidayCalendar.Nyse ? "NYSE" : "US federal") + " holidays";
        text += ". Counted over the next " + (year ? "year" : "4 weeks") + ".";
        if (hasLogon) text += " Also runs when you sign in.";
        return text;
    }
}

/// <summary>Remembers counted rates, so the 30 s refresh does not recount. Thread-safe (rows are described on the thread pool).</summary>
public sealed class RateCache
{
    readonly ConcurrentDictionary<(ValueList<TaskTrigger>, HolidayCalendar, HumanSchedule?, DateOnly, string), RateInfo> _items = new();
    int _computations;

    /// <summary>How many times a rate was really counted (the test that a second describe does not recount reads this).</summary>
    public int Computations => Volatile.Read(ref _computations);

    /// <summary>Returns the cached rate for a key, counting it with <paramref name="compute"/> the first time (test hook: the cache is public so the tests can read <see cref="Computations"/>).</summary>
    public RateInfo GetOrCompute((ValueList<TaskTrigger>, HolidayCalendar, HumanSchedule?, DateOnly, string) key, Func<RateInfo> compute)
    {
        if (_items.TryGetValue(key, out RateInfo? known)) return known;
        RateInfo made = compute();
        Interlocked.Increment(ref _computations);
        if (_items.Count > 2000) _items.Clear();   // a long-running window sees few distinct schedules; this only guards against growth
        _items[key] = made;
        return made;
    }
}
