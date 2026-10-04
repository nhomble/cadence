using System.Globalization;
using System.Text;

namespace Cadence.Core.TaskXml;

/// <summary>
/// ISO-8601 durations as Task Scheduler uses them: <c>P[nD][T[nH][nM][nS]]</c>, whole numbers only.
/// Years, months and weeks are rejected (Task Scheduler never emits them in these fields).
/// </summary>
public static class IsoDuration
{
    const long MaxSeconds = 922_337_203_685;   // TimeSpan.MaxValue in whole seconds

    public static TimeSpan Parse(string text) =>
        TryParse(text, out var v) ? v : throw new FormatException($"'{text}' is not a valid duration (expected something like PT1H30M or P1D).");

    public static bool TryParse(string? text, out TimeSpan value)
    {
        value = default;
        if (text is null) return false;
        string s = text.Trim();
        if (s.Length < 2 || s[0] != 'P') return false;

        int i = 1;
        long seconds = 0;
        bool any = false;
        bool inTime = false;
        int lastUnit = -1;   // 0 = D, 1 = H, 2 = M, 3 = S
        bool timeHasComponent = false;

        while (i < s.Length)
        {
            if (s[i] == 'T')
            {
                if (inTime) return false;
                inTime = true;
                i++;
                continue;
            }
            int start = i;
            while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
            int digits = i - start;
            if (digits == 0 || digits > 9 || i >= s.Length) return false;
            long n = long.Parse(s.AsSpan(start, digits), NumberStyles.None, CultureInfo.InvariantCulture);
            char unit = s[i++];
            int unitIndex;
            long factor;
            switch (unit)
            {
                case 'D' when !inTime: unitIndex = 0; factor = 86400; break;
                case 'H' when inTime: unitIndex = 1; factor = 3600; break;
                case 'M' when inTime: unitIndex = 2; factor = 60; break;
                case 'S' when inTime: unitIndex = 3; factor = 1; break;
                default: return false;   // Y, W, month, fractions, signs, wrong order of T
            }
            if (unitIndex <= lastUnit) return false;
            lastUnit = unitIndex;
            seconds += n * factor;
            any = true;
            if (inTime) timeHasComponent = true;
        }
        if (!any) return false;
        if (inTime && !timeHasComponent) return false;   // "P1DT"
        if (seconds > MaxSeconds) return false;
        value = TimeSpan.FromSeconds(seconds);
        return true;
    }

    /// <summary>Canonical form as Task Scheduler writes it (PT1H30M, P1D, P1DT2H, PT0S). Sub-second parts are dropped.</summary>
    public static string Format(TimeSpan value)
    {
        if (value < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(value), "A duration cannot be negative.");
        long total = (long)value.TotalSeconds;
        if (total == 0) return "PT0S";
        long days = total / 86400;
        long rem = total % 86400;
        long h = rem / 3600;
        long m = rem % 3600 / 60;
        long sec = rem % 60;
        var sb = new StringBuilder("P");
        if (days > 0) sb.Append(days.ToString(CultureInfo.InvariantCulture)).Append('D');
        if (h > 0 || m > 0 || sec > 0)
        {
            sb.Append('T');
            if (h > 0) sb.Append(h.ToString(CultureInfo.InvariantCulture)).Append('H');
            if (m > 0) sb.Append(m.ToString(CultureInfo.InvariantCulture)).Append('M');
            if (sec > 0) sb.Append(sec.ToString(CultureInfo.InvariantCulture)).Append('S');
        }
        return sb.ToString();
    }
}
