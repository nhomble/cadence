using Cadence.Core.Model;

namespace Cadence.Core.Cron;

/// <summary>
/// Compiles a schedule's cron lines into exact Task Scheduler triggers (PLAN.md 3.3): per line, the day fields
/// become <see cref="DayRule"/>s, the minute/hour fields become the cheapest <see cref="TimeRule"/> strategy, and
/// each (day rule, time rule) pair becomes triggers. Nothing is approximated: anything that can't be represented
/// exactly is an Error diagnostic with a plain-English reason.
/// </summary>
public static class CronCompiler
{
    public const int MaxTriggers = 48;
    public const int MaxLines = 10;

    static readonly ValueList<WeekOfMonth> AllWeeks = new(
        [WeekOfMonth.First, WeekOfMonth.Second, WeekOfMonth.Third, WeekOfMonth.Fourth, WeekOfMonth.Last]);

    public static CompiledSchedule Compile(ScheduleSpec spec, MapContext ctx)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(ctx);

        var diags = new List<CronDiagnostic>();
        var groups = new List<ScheduleGroup>();
        var triggers = new List<TaskTrigger>();
        bool atLogon = false;
        int lineCount = spec.CronLines.Count;

        if (lineCount < 1 || lineCount > MaxLines)
        {
            diags.Add(new CronDiagnostic(CronSeverity.Error, 0, "lines",
                $"a schedule needs between 1 and {MaxLines} cron lines, but this one has {lineCount}"));
        }

        for (int i = 0; i < lineCount; i++)
        {
            int lineNo = i + 1;
            var expr = CronSyntax.Parse(spec.CronLines[i], lineNo, diags);
            if (expr is null) continue;

            if (expr.AtLogon)
            {
                if (!atLogon) triggers.Add(new LogonTrigger(ctx.UserId, null));
                atLogon = true;
                continue;
            }

            var lineGroups = new List<ScheduleGroup>();
            var dayRules = DayRules(expr, lineNo, diags);
            var (strategy, timeRules) = PlanTimes(expr);
            foreach (var day in dayRules)
                foreach (var time in timeRules)
                    lineGroups.Add(new ScheduleGroup(day, time));

            int before = triggers.Count;
            foreach (var g in lineGroups) triggers.AddRange(ToTriggers(g, spec.Anchor));
            int lineTriggers = triggers.Count - before;
            groups.AddRange(lineGroups);

            if (strategy is 'C' or 'D')
            {
                diags.Add(new CronDiagnostic(CronSeverity.Info, lineNo, "minute/hour",
                    $"line {lineNo}, minute/hour: represented exactly with {lineTriggers} triggers"));
            }
        }

        if (triggers.Count > MaxTriggers)
        {
            diags.Add(new CronDiagnostic(CronSeverity.Error, 0, "triggers",
                $"this schedule needs {triggers.Count} triggers; Task Scheduler allows {MaxTriggers} per task"));
        }

        bool success = !diags.Any(d => d.Severity == CronSeverity.Error);
        if (success)
        {
            return new CompiledSchedule(true, new ValueList<ScheduleGroup>(groups), atLogon,
                new ValueList<TaskTrigger>(triggers), new ValueList<CronDiagnostic>(diags), null, null);
        }

        var (alt, why) = ClosestAlternative(spec, ctx);
        return new CompiledSchedule(false, ValueList<ScheduleGroup>.Empty, false,
            ValueList<TaskTrigger>.Empty, new ValueList<CronDiagnostic>(diags), alt, why);
    }

    // ----- @reboot -> @logon ------------------------------------------------------------------------

    static (ValueList<string>?, string?) ClosestAlternative(ScheduleSpec spec, MapContext ctx)
    {
        bool any = false;
        var lines = new List<string>();
        bool logonSeen = false;
        foreach (string raw in spec.CronLines)
        {
            string line = raw.Trim();
            bool reboot = line.Equals("@reboot", StringComparison.OrdinalIgnoreCase);
            if (reboot) { any = true; line = "@logon"; }
            if (line.Equals("@logon", StringComparison.OrdinalIgnoreCase))
            {
                if (logonSeen) continue;
                logonSeen = true;
            }
            lines.Add(line);
        }
        if (!any) return (null, null);

        // Only offer the alternative when it is itself valid.
        var attempt = Compile(spec with { CronLines = new ValueList<string>(lines) }, ctx);
        if (!attempt.Success) return (null, null);
        return (new ValueList<string>(lines),
            "Start-up triggers need administrator rights. \"@logon\" runs the job when you sign in instead, which doesn't.");
    }

    // ----- step 1: days ------------------------------------------------------------------------------

    static List<DayRule> DayRules(CronExpr e, int lineNo, List<CronDiagnostic> diags)
    {
        var months = e.Months;
        bool domRestricted = !e.Dom.Star;
        bool dowRestricted = !e.Dow.Star;
        bool domAll = e.Dom.Days.Count == 31;
        bool dowAll = e.Dow.Days == WeekDays.All;
        var rules = new List<DayRule>();

        // An explicit but all-covering field (1-31, 0-6) means every day, whatever the other field says.
        bool everyDay = (!domRestricted && !dowRestricted) || (domRestricted && domAll) || (dowRestricted && dowAll);
        if (everyDay)
        {
            if (months == MonthSet.All) rules.Add(new EveryDay());
            else rules.Add(new MonthlyOnDays(new ValueList<int>(Enumerable.Range(1, 31)), false, months));
            return rules;
        }

        if (domRestricted && dowRestricted)
        {
            diags.Add(new CronDiagnostic(CronSeverity.Info, lineNo, "day-of-month",
                $"line {lineNo}, day-of-month: both day-of-month and day-of-week are set, so it runs when either the day of month or the weekday matches"));
        }

        // [CP-A] A day of month that none of the selected months has (30 February, 31 April) would compile to a trigger that
        // never fires. Unless the weekday field adds runs (OR semantics), that is an Error, not a silent no-op schedule.
        if (domRestricted && !dowRestricted && !e.Dom.Last && !e.Dom.Days.Any(d => AnyMonthHasDay(months, d)))
        {
            string days = e.Dom.Days.Count == 1 ? "day " + e.Dom.Days[0] : "any of days " + string.Join(", ", e.Dom.Days);
            diags.Add(new CronDiagnostic(CronSeverity.Error, lineNo, "day-of-month",
                $"line {lineNo}, day-of-month: this never runs, because none of the selected months has {days}"));
            return rules;
        }

        if (domRestricted)
        {
            rules.Add(new MonthlyOnDays(e.Dom.Days, e.Dom.Last, months));
            var late = e.Dom.Days.Where(d => d >= 29).ToList();
            if (late.Count == 1)
            {
                diags.Add(new CronDiagnostic(CronSeverity.Info, lineNo, "day-of-month",
                    $"line {lineNo}, day-of-month: day {late[0]} is skipped in months without that day"));
            }
            else if (late.Count > 1)
            {
                string list = string.Join(", ", late.Take(late.Count - 1)) + " and " + late[^1];
                diags.Add(new CronDiagnostic(CronSeverity.Info, lineNo, "day-of-month",
                    $"line {lineNo}, day-of-month: days {list} are skipped in months without those days"));
            }
        }

        if (dowRestricted) rules.AddRange(DowRules(e.Dow, months));
        return rules;
    }

    // February counts as 29 days: a leap-year-only day still runs.
    static bool AnyMonthHasDay(MonthSet months, int day)
    {
        ReadOnlySpan<int> maxDays = [31, 29, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];
        for (int m = 0; m < 12; m++)
            if (((int)months & (1 << m)) != 0 && day <= maxDays[m]) return true;
        return false;
    }

    static List<DayRule> DowRules(DowSpec dow, MonthSet months)
    {
        var rules = new List<DayRule>();
        var plain = dow.Days;

        // Nth items on a weekday that is already listed plainly add nothing.
        var weeksByDay = new SortedDictionary<int, SortedSet<int>>();
        foreach (var n in dow.Nth)
        {
            int d = (int)n.Day;
            if (((int)plain & (1 << d)) != 0) continue;
            if (!weeksByDay.TryGetValue(d, out var set)) weeksByDay[d] = set = [];
            set.Add((int)n.Week);
        }
        // A weekday listed for every week of the month is the same as a plain weekday.
        foreach (var kv in weeksByDay.Where(kv => kv.Value.Count == 5).ToList())
        {
            plain |= (WeekDays)(1 << kv.Key);
            weeksByDay.Remove(kv.Key);
        }

        if (plain != WeekDays.None)
        {
            if (months == MonthSet.All) rules.Add(new WeeklyOn(plain));
            else rules.Add(new MonthlyOnWeekdays(AllWeeks, plain, months));
        }

        // One rule per distinct week set.
        var byWeeks = new SortedDictionary<string, (ValueList<WeekOfMonth> Weeks, WeekDays Days)>(StringComparer.Ordinal);
        foreach (var (day, set) in weeksByDay)
        {
            string key = string.Join(",", set);
            byWeeks.TryGetValue(key, out var cur);
            byWeeks[key] = (new ValueList<WeekOfMonth>(set.Select(w => (WeekOfMonth)w)), cur.Days | (WeekDays)(1 << day));
        }
        foreach (var (weeks, days) in byWeeks.Values)
            rules.Add(new MonthlyOnWeekdays(weeks, days, months));
        return rules;
    }

    // ----- step 2: times -----------------------------------------------------------------------------

    static (char Strategy, List<TimeRule> Rules) PlanTimes(CronExpr e)
    {
        var minutes = e.Minutes;
        var hours = e.Hours;
        var all = new int[hours.Count * minutes.Count];
        int k = 0;
        for (int h = 0; h < hours.Count; h++)
            for (int m = 0; m < minutes.Count; m++)
                all[k++] = hours[h] * 60 + minutes[m];   // already sorted: hours and minutes are sorted

        if (all.Length == 1) return ('A', [new AtTimes(new ValueList<TimeOnly>([ToTime(all[0])]))]);

        // A two-time set is listed, not repeated: a "repetition" that fires twice is just two times.
        if (all.Length > 2)
        {
            // [CP-A] The same rule inside C and D: a two-element hour or minute progression would give repetitions whose Duration
            // equals their Interval (two runs each; awkward English such as "every 8 hours from 9:00 to 17:00", and
            // Duration == Interval is unverified against Task Scheduler). Listed times are used instead, unless they don't fit.
            int minProgression = all.Length > MaxTriggers ? 2 : 3;
            int bestCount = int.MaxValue;
            char best = 'E';
            if (IsProgression(all)) { bestCount = 1; best = 'B'; }
            if (hours.Count >= minProgression && minutes.Count < bestCount && IsProgression(Slice(hours))) { bestCount = minutes.Count; best = 'C'; }
            if (minutes.Count >= minProgression && hours.Count < bestCount && IsProgression(Slice(minutes))) { bestCount = hours.Count; best = 'D'; }
            if (all.Length < bestCount) { best = 'E'; }

            switch (best)
            {
                case 'B':
                    return ('B', [Repeat(all[0], all[^1], all[1] - all[0])]);
                case 'C':
                {
                    int step = (hours[1] - hours[0]) * 60;
                    var rules = new List<TimeRule>();
                    foreach (int m in minutes) rules.Add(Repeat(hours[0] * 60 + m, hours[^1] * 60 + m, step));
                    return ('C', rules);
                }
                case 'D':
                {
                    int step = minutes[1] - minutes[0];
                    var rules = new List<TimeRule>();
                    foreach (int h in hours) rules.Add(Repeat(h * 60 + minutes[0], h * 60 + minutes[^1], step));
                    return ('D', rules);
                }
            }
        }
        return ('E', [new AtTimes(new ValueList<TimeOnly>(all.Select(ToTime)))]);
    }

    static int[] Slice(ValueList<int> v)
    {
        var a = new int[v.Count];
        for (int i = 0; i < a.Length; i++) a[i] = v[i];
        return a;
    }

    static bool IsProgression(int[] sorted)
    {
        if (sorted.Length < 2) return false;
        int step = sorted[1] - sorted[0];
        for (int i = 2; i < sorted.Length; i++)
            if (sorted[i] - sorted[i - 1] != step) return false;
        return step > 0;
    }

    static TimeOnly ToTime(int minuteOfDay) => new(minuteOfDay / 60, minuteOfDay % 60);

    static RepeatEvery Repeat(int firstMinute, int lastMinute, int stepMinutes) =>
        new(TimeSpan.FromMinutes(stepMinutes), ToTime(firstMinute), ToTime(lastMinute));

    // ----- step 3: IR -> triggers --------------------------------------------------------------------

    static IEnumerable<TaskTrigger> ToTriggers(ScheduleGroup g, DateOnly anchor)
    {
        switch (g.Times)
        {
            case AtTimes at:
                foreach (var t in at.Times)
                    yield return Make(g.Days, anchor.ToDateTime(t), null);
                break;
            case RepeatEvery r:
                // Calendar-trigger repetition end is inclusive (verified, 2.3), so the duration reaches Last.
                // Cron hours never wrap past midnight, so Last >= First and the duration never wraps.
                yield return Make(g.Days, anchor.ToDateTime(r.First),
                    new Repetition(r.Every, r.Last.ToTimeSpan() - r.First.ToTimeSpan(), false));
                break;
        }
    }

    static TaskTrigger Make(DayRule day, DateTime start, Repetition? rep) => day switch
    {
        EveryDay => new DailyTrigger(1) { StartBoundary = start, Repetition = rep },
        WeeklyOn w => new WeeklyTrigger(1, w.Days) { StartBoundary = start, Repetition = rep },
        MonthlyOnDays m => new MonthlyTrigger(m.Days, m.LastDay, m.Months) { StartBoundary = start, Repetition = rep },
        MonthlyOnWeekdays mw => new MonthlyDowTrigger(mw.Weeks, mw.Days, mw.Months) { StartBoundary = start, Repetition = rep },
        _ => throw new InvalidOperationException("unknown day rule " + day.GetType().Name),
    };
}
