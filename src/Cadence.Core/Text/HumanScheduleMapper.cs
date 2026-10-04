using System.Globalization;
using Cadence.Core.Cron;
using Cadence.Core.Model;

namespace Cadence.Core.Text;

/// <summary>
/// HumanSchedule to cron and back (PLAN.md 3.5). <see cref="ToCron"/> writes canonical numeric lines;
/// <see cref="TryFromCron"/> recognizes exactly those shapes (plus equivalent spellings such as MON-FRI or a macro).
/// For every valid h: <c>TryFromCron(ToCron(h))</c> succeeds and equals <c>Normalize(h)</c>.
/// </summary>
public static class HumanScheduleMapper
{
    public const int MaxTimes = 10;

    public static readonly IReadOnlyList<int> MinuteIntervals = [1, 2, 3, 4, 5, 6, 10, 12, 15, 20, 30];
    public static readonly IReadOnlyList<int> HourIntervals = [1, 2, 3, 4, 6, 8, 12];

    static readonly (WeekDays Flag, int Cron)[] CronDays =
    [
        (WeekDays.Monday, 1), (WeekDays.Tuesday, 2), (WeekDays.Wednesday, 3), (WeekDays.Thursday, 4),
        (WeekDays.Friday, 5), (WeekDays.Saturday, 6), (WeekDays.Sunday, 0),
    ];

    // ----- Normalize / IsValid --------------------------------------------------------------------

    /// <summary>
    /// The canonical form: times sorted, distinct, minute resolution; fields that do not belong to the frequency reset to
    /// <see cref="HumanSchedule.Default"/>; SpecificDays with every day becomes Daily and with Mon-Fri becomes Weekdays;
    /// an EveryN window that covers the whole day is dropped; the hour window's end snaps to the last hour that runs.
    /// </summary>
    public static HumanSchedule Normalize(HumanSchedule h)
    {
        ArgumentNullException.ThrowIfNull(h);
        var times = new ValueList<TimeOnly>(h.Times.Select(t => new TimeOnly(t.Hour, t.Minute)).Distinct().Order());
        var days = h.Days & WeekDays.All;
        switch (h.Frequency)
        {
            case HumanFrequency.Daily:
                return HumanSchedule.Default(HumanFrequency.Daily) with { Times = times };
            case HumanFrequency.Weekdays:
                return HumanSchedule.Default(HumanFrequency.Weekdays) with { Times = times };
            case HumanFrequency.SpecificDays:
                if (days == WeekDays.All) return HumanSchedule.Default(HumanFrequency.Daily) with { Times = times };
                if (days == WeekDays.Weekdays) return HumanSchedule.Default(HumanFrequency.Weekdays) with { Times = times };
                return HumanSchedule.Default(HumanFrequency.SpecificDays) with { Times = times, Days = days };
            case HumanFrequency.MonthlyOnDates:
                return HumanSchedule.Default(HumanFrequency.MonthlyOnDates) with
                {
                    Times = times,
                    MonthDates = new ValueList<int>(h.MonthDates.Distinct().Order()),
                    LastDayOfMonth = h.LastDayOfMonth,
                };
            case HumanFrequency.MonthlyOnWeekday:
                return HumanSchedule.Default(HumanFrequency.MonthlyOnWeekday) with { Times = times, Week = h.Week, Weekday = h.Weekday };
            case HumanFrequency.EveryNMinutes:
            {
                var (from, to) = Window(h.FromHour, h.ToHour, 1, 0);
                return HumanSchedule.Default(HumanFrequency.EveryNMinutes) with { Days = days, Interval = h.Interval, FromHour = from, ToHour = to };
            }
            case HumanFrequency.EveryNHours:
            {
                var (from, to) = Window(h.FromHour, h.ToHour, h.Interval, h.Interval);
                return HumanSchedule.Default(HumanFrequency.EveryNHours) with
                {
                    Days = days, Interval = h.Interval, MinuteOffset = h.MinuteOffset, FromHour = from, ToHour = to,
                };
            }
            default:
                return HumanSchedule.Default(HumanFrequency.AtSignIn);
        }
    }

    // Both ends or neither. A window whose runs reach the end of the day from midnight is no window at all.
    // `step` snaps the end to the last run (hour intervals); `dropIfCovers` is the interval used for the whole-day test (0 = whole hours).
    static (int? From, int? To) Window(int? from, int? to, int step, int dropIfCovers)
    {
        if (from is null || to is null) return (null, null);
        int f = from.Value, t = to.Value;
        if (f < 0 || t > 23 || f > t || step < 1) return (f, t);   // invalid: kept so IsValid can reject it
        t = f + ((t - f) / step) * step;
        if (f == 0 && t + Math.Max(dropIfCovers, 1) >= 24) return (null, null);
        return (f, t);
    }

    /// <summary>True when <see cref="ToCron"/> can express the (normalized) schedule.</summary>
    public static bool IsValid(HumanSchedule h)
    {
        ArgumentNullException.ThrowIfNull(h);
        var n = Normalize(h);
        bool TimesOk() => n.Times.Count is >= 1 and <= MaxTimes;
        bool WindowOk() => (n.FromHour is null && n.ToHour is null)
            || (n.FromHour is >= 0 and <= 23 && n.ToHour is >= 0 and <= 23 && n.FromHour <= n.ToHour);
        switch (n.Frequency)
        {
            case HumanFrequency.Daily:
            case HumanFrequency.Weekdays:
                return TimesOk();
            case HumanFrequency.SpecificDays:
                return TimesOk() && n.Days != WeekDays.None;
            case HumanFrequency.MonthlyOnDates:
                return TimesOk() && (n.MonthDates.Count > 0 || n.LastDayOfMonth) && n.MonthDates.All(d => d is >= 1 and <= 31);
            case HumanFrequency.MonthlyOnWeekday:
                return TimesOk() && Enum.IsDefined(n.Week) && Enum.IsDefined(n.Weekday);
            case HumanFrequency.EveryNMinutes:
                return MinuteIntervals.Contains(n.Interval) && n.Days != WeekDays.None && WindowOk();
            case HumanFrequency.EveryNHours:
                return HourIntervals.Contains(n.Interval) && n.MinuteOffset is >= 0 and <= 59 && n.Days != WeekDays.None
                    && WindowOk() && RunHours(n).Count >= 2;
            case HumanFrequency.AtSignIn:
                return true;
            default:
                return false;
        }
    }

    static List<int> RunHours(HumanSchedule n)
    {
        int from = n.FromHour ?? 0, to = n.ToHour ?? 23;
        var hours = new List<int>();
        for (int hour = from; hour <= to; hour += n.Interval) hours.Add(hour);
        return hours;
    }

    // ----- ToCron ---------------------------------------------------------------------------------

    /// <summary>Canonical numeric cron lines for the schedule; an empty list when <see cref="IsValid"/> is false.</summary>
    public static ValueList<string> ToCron(HumanSchedule h)
    {
        ArgumentNullException.ThrowIfNull(h);
        var n = Normalize(h);
        if (!IsValid(n)) return ValueList<string>.Empty;
        switch (n.Frequency)
        {
            case HumanFrequency.AtSignIn:
                return ValueList.Of("@logon");
            case HumanFrequency.EveryNMinutes:
            {
                string minute = n.Interval == 1 ? "*" : "*/" + Num(n.Interval);
                string hour = n.FromHour is { } f ? Num(f) + "-" + Num(n.ToHour!.Value) : "*";
                return ValueList.Of(minute + " " + hour + " * * " + DowField(n.Days));
            }
            case HumanFrequency.EveryNHours:
            {
                string hour;
                if (n.FromHour is { } f)
                {
                    hour = Num(f) + "-" + Num(n.ToHour!.Value) + (n.Interval == 1 ? "" : "/" + Num(n.Interval));
                }
                else
                {
                    hour = n.Interval == 1 ? "*" : "*/" + Num(n.Interval);
                }
                return ValueList.Of(Num(n.MinuteOffset) + " " + hour + " * * " + DowField(n.Days));
            }
            default:
            {
                string dom = n.Frequency == HumanFrequency.MonthlyOnDates
                    ? string.Join(",", n.MonthDates.Select(Num).Concat(n.LastDayOfMonth ? new[] { "L" } : Array.Empty<string>()))
                    : "*";
                string dow = n.Frequency switch
                {
                    HumanFrequency.Weekdays => "1-5",
                    HumanFrequency.SpecificDays => DowField(n.Days),
                    HumanFrequency.MonthlyOnWeekday => n.Week == WeekOfMonth.Last
                        ? Num((int)n.Weekday) + "L"
                        : Num((int)n.Weekday) + "#" + Num((int)n.Week),
                    _ => "*",
                };
                var lines = new List<string>();
                foreach (var group in n.Times.GroupBy(t => t.Minute).OrderBy(g => g.Key))
                {
                    string hours = string.Join(",", group.Select(t => t.Hour).Order().Select(Num));
                    lines.Add(Num(group.Key) + " " + hours + " " + dom + " * " + dow);
                }
                return new ValueList<string>(lines);
            }
        }
    }

    static string Num(int v) => v.ToString(CultureInfo.InvariantCulture);

    static string DowField(WeekDays days)
    {
        if (days == WeekDays.All) return "*";
        if (days == WeekDays.Weekdays) return "1-5";
        return string.Join(",", CronDays.Where(d => (days & d.Flag) != 0).Select(d => Num(d.Cron)));
    }

    // ----- TryFromCron ----------------------------------------------------------------------------

    /// <summary>
    /// Recognizes the shapes <see cref="ToCron"/> produces. False means "this schedule can only be edited in cron view";
    /// <paramref name="h"/> is then a neutral Daily placeholder.
    /// </summary>
    public static bool TryFromCron(ValueList<string> lines, out HumanSchedule h)
    {
        ArgumentNullException.ThrowIfNull(lines);
        h = HumanSchedule.Default(HumanFrequency.Daily);
        if (lines.Count is < 1 or > CronCompiler.MaxLines) return false;

        var raws = new List<string[]>();
        bool anyLogon = false;
        foreach (string line in lines)
        {
            string text = string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (string.Equals(text, "@logon", StringComparison.OrdinalIgnoreCase)) { anyLogon = true; continue; }
            string five = ExpandMacro(text);
            string[] fields = five.Split(' ');
            if (fields.Length == 6 && fields[0] == "0") fields = fields[1..];
            if (fields.Length != 5) return false;
            raws.Add(fields);
        }
        if (anyLogon)
        {
            if (lines.Count != 1) return false;
            h = HumanSchedule.Default(HumanFrequency.AtSignIn);
            return true;
        }

        var exprs = new List<CronExpr>();
        foreach (var fields in raws)
        {
            var diags = new List<CronDiagnostic>();
            var expr = CronSyntax.Parse(string.Join(' ', fields), exprs.Count + 1, diags);
            if (expr is null || expr.AtLogon) return false;
            exprs.Add(expr);
        }

        // One line of an EveryN shape, or one or more "single minute, hour list" lines of a times shape.
        var kinds = raws.Select(f => Kind(f)).ToList();
        if (kinds.Any(k => k == LineKind.None)) return false;
        HumanSchedule? result = null;
        if (kinds.All(k => k == LineKind.Times)) result = FromTimeLines(exprs);
        else if (raws.Count == 1 && kinds[0] == LineKind.HourPattern) result = FromHourPattern(raws[0], exprs[0]);
        else if (raws.Count == 1 && kinds[0] == LineKind.MinutePattern) result = FromMinutePattern(raws[0], exprs[0]);
        if (result is null) return false;

        result = Normalize(result);
        if (!IsValid(result)) return false;
        h = result;
        return true;
    }

    enum LineKind { None, Times, HourPattern, MinutePattern }

    static string ExpandMacro(string text)
    {
        if (text.Length == 0 || text[0] != '@') return text;
        return text.ToLowerInvariant() switch
        {
            "@hourly" => "0 * * * *",
            "@daily" or "@midnight" => "0 0 * * *",
            "@weekly" => "0 0 * * 0",
            "@monthly" => "0 0 1 * *",
            "@yearly" or "@annually" => "0 0 1 1 *",
            _ => text,
        };
    }

    static bool IsNumber(string s) => s.Length > 0 && s.All(char.IsAsciiDigit);

    static bool IsNumberList(string s) => s.Length > 0 && s.All(c => char.IsAsciiDigit(c) || c == ',');

    static LineKind Kind(string[] f)
    {
        string minute = f[0], hour = f[1];
        if (IsNumber(minute))
        {
            if (IsNumberList(hour)) return LineKind.Times;
            return ParseHourPattern(hour, out _, out _, out _) ? LineKind.HourPattern : LineKind.None;
        }
        bool minuteOk = minute == "*" || (minute.StartsWith("*/", StringComparison.Ordinal) && IsNumber(minute[2..]));
        bool hourOk = hour == "*" || (hour.Contains('-', StringComparison.Ordinal) && !hour.Contains('/', StringComparison.Ordinal)
            && ParseHourPattern(hour, out _, out _, out _));
        return minuteOk && hourOk ? LineKind.MinutePattern : LineKind.None;
    }

    // "*", "*/N", "a-b", "a-b/N"
    static bool ParseHourPattern(string raw, out int step, out int? from, out int? to)
    {
        step = 1; from = null; to = null;
        string body = raw;
        int slash = raw.IndexOf('/', StringComparison.Ordinal);
        if (slash >= 0)
        {
            if (!int.TryParse(raw.AsSpan(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out step) || step < 1) return false;
            body = raw[..slash];
        }
        if (body == "*") return true;
        int dash = body.IndexOf('-', StringComparison.Ordinal);
        if (dash <= 0 || dash == body.Length - 1) return false;
        if (!int.TryParse(body.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out int a)) return false;
        if (!int.TryParse(body.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int b)) return false;
        from = a; to = b;
        return true;
    }

    // The days an EveryN line is limited to; dom must be `*` and months all.
    static bool LimitDays(CronExpr e, out WeekDays days)
    {
        days = e.Dow.Days;
        return e.Dom.Star && e.Months == MonthSet.All && e.Dow.Nth.Count == 0 && days != WeekDays.None;
    }

    static HumanSchedule? FromHourPattern(string[] f, CronExpr e)
    {
        if (!LimitDays(e, out var days) || !ParseHourPattern(f[1], out int step, out int? from, out int? to)) return null;
        if (!HourIntervals.Contains(step)) return null;
        int last = to ?? 0;
        if (from is { } a && to is { } b)
        {
            if (a > b || b > 23) return null;
            last = a + ((b - a) / step) * step;
        }
        return HumanSchedule.Default(HumanFrequency.EveryNHours) with
        {
            Days = days,
            Interval = step,
            MinuteOffset = e.Minutes[0],
            FromHour = from,
            ToHour = from is null ? null : last,
        };
    }

    static HumanSchedule? FromMinutePattern(string[] f, CronExpr e)
    {
        if (!LimitDays(e, out var days)) return null;
        int step = f[0] == "*" ? 1 : int.Parse(f[0].AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture);
        if (!MinuteIntervals.Contains(step)) return null;
        int? from = null, to = null;
        if (f[1] != "*")
        {
            if (!ParseHourPattern(f[1], out _, out from, out to)) return null;
            if (from > to || to > 23) return null;
        }
        return HumanSchedule.Default(HumanFrequency.EveryNMinutes) with { Days = days, Interval = step, FromHour = from, ToHour = to };
    }

    static HumanSchedule? FromTimeLines(List<CronExpr> exprs)
    {
        HumanSchedule? shape = null;
        var times = new SortedSet<TimeOnly>();
        for (int i = 0; i < exprs.Count; i++)
        {
            var e = exprs[i];
            var s = TimeShape(e);
            if (s is null || (shape is not null && shape != s)) return null;
            shape = s;
            foreach (int hour in e.Hours) times.Add(new TimeOnly(hour, e.Minutes[0]));
        }
        if (shape is null || times.Count > MaxTimes) return null;
        return shape with { Times = new ValueList<TimeOnly>(times) };
    }

    // The frequency and day fields of a "times" line (no times yet).
    static HumanSchedule? TimeShape(CronExpr e)
    {
        if (e.Months != MonthSet.All || e.Minutes.Count != 1) return null;
        if (e.Dom.Star)
        {
            if (e.Dow.Nth.Count == 0)
            {
                var d = e.Dow.Days;
                if (d == WeekDays.All) return HumanSchedule.Default(HumanFrequency.Daily);
                if (d == WeekDays.Weekdays) return HumanSchedule.Default(HumanFrequency.Weekdays);
                return d == WeekDays.None ? null : HumanSchedule.Default(HumanFrequency.SpecificDays) with { Days = d };
            }
            if (e.Dow.Nth.Count == 1 && e.Dow.Days == WeekDays.None)
            {
                return HumanSchedule.Default(HumanFrequency.MonthlyOnWeekday) with { Week = e.Dow.Nth[0].Week, Weekday = e.Dow.Nth[0].Day };
            }
            return null;
        }
        if (!e.Dow.Star) return null;   // both restricted means OR in cron: not a Simple shape
        return HumanSchedule.Default(HumanFrequency.MonthlyOnDates) with
        {
            MonthDates = new ValueList<int>(e.Dom.Days),
            LastDayOfMonth = e.Dom.Last,
        };
    }
}
