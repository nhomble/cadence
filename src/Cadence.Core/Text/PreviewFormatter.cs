using System.Globalization;
using Cadence.Core.Scheduling;

namespace Cadence.Core.Text;

/// <summary>Formats next-run previews (PLAN.md 3.5): "Mon 5 Oct 18:00", year only when it differs from now's.</summary>
public static class PreviewFormatter
{
    /// <summary>
    /// "Thu 1 Oct 8:00", "Sun 14 Mar 2027 3:30 (clock change)", "Thu 26 Nov 18:00 - skipped (Thanksgiving Day)".
    /// A random delay adds " (+ up to N min)" after the time. Weekday and month are abbreviated; the time is H:mm.
    /// </summary>
    public static string FormatOne(Occurrence run, DateTimeOffset now, TimeZoneInfo tz)
    {
        ArgumentNullException.ThrowIfNull(tz);
        var local = TimeZoneInfo.ConvertTime(run.At, tz);
        int nowYear = TimeZoneInfo.ConvertTime(now, tz).Year;
        string format = local.Year == nowYear ? "ddd d MMM H:mm" : "ddd d MMM yyyy H:mm";
        string text = local.ToString(format, CultureInfo.InvariantCulture);
        if (run.RandomDelayUpTo > TimeSpan.Zero)
        {
            int minutes = (int)Math.Ceiling(run.RandomDelayUpTo.TotalMinutes);
            text += " (+ up to " + minutes.ToString(CultureInfo.InvariantCulture) + " min)";
        }
        if (run.DstAdjusted) text += " (clock change)";
        if (run.SkippedHoliday is not null) text += " - skipped (" + run.SkippedHoliday + ")";
        return text;
    }

    /// <summary>The runs joined with ", " (empty list gives an empty string).</summary>
    public static string FormatList(IReadOnlyList<Occurrence> runs, DateTimeOffset now, TimeZoneInfo tz)
    {
        ArgumentNullException.ThrowIfNull(runs);
        return string.Join(", ", runs.Select(r => FormatOne(r, now, tz)));
    }
}
