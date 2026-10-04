using Cadence.Core.Model;

namespace Cadence.Core.Cron;

/// <summary>
/// One parsed cron line. Sets are sorted and distinct. A field written as `*` or `?` has Star = true and its
/// value set holds the full range (dom 1-31, dow all seven days), so consumers can treat every field as a set.
/// </summary>
public sealed record CronExpr(ValueList<int> Minutes, ValueList<int> Hours, DomSpec Dom, MonthSet Months, DowSpec Dow, bool AtLogon);

/// <summary>Day of month: Days are 1-31; Last is true when an `L` item is present.</summary>
public sealed record DomSpec(bool Star, ValueList<int> Days, bool Last);

/// <summary>The weekday named by an `nL` (Week = Last) or `n#k` item.</summary>
public sealed record NthWeekday(WeekOfMonth Week, DayOfWeek Day);

/// <summary>Day of week: Days are plain weekday items (7 folded into Sunday); Nth holds `nL` and `n#k` items.</summary>
public sealed record DowSpec(bool Star, WeekDays Days, ValueList<NthWeekday> Nth);
