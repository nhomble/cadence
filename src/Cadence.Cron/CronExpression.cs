namespace Cadence.Cron;

/// <summary>
/// One parsed cron line. Sets are sorted and distinct. A field written as `*` or `?` has Star = true and its
/// value set holds the full range (dom 1-31, dow all seven days), so consumers can treat every field as a set.
/// </summary>
public sealed record CronExpression(CronList<int> Minutes, CronList<int> Hours, CronDom Dom, CronMonths Months, CronDow Dow, bool AtLogon);

/// <summary>Day of month: Days are 1-31; Last is true when an `L` item is present.</summary>
public sealed record CronDom(bool Star, CronList<int> Days, bool Last);

/// <summary>The weekday named by an `nL` (Week = <see cref="Last"/>) or `n#k` item (Week = k, 1-4).</summary>
public sealed record CronNth(int Week, DayOfWeek Day)
{
    /// <summary>The Week value of an `nL` item.</summary>
    public const int Last = 5;
}

/// <summary>Day of week: Days are plain weekday items (7 folded into Sunday); Nth holds `nL` and `n#k` items.</summary>
public sealed record CronDow(bool Star, CronDays Days, CronList<CronNth> Nth);