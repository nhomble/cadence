using System.Globalization;

namespace Cadence.Cron;

/// <summary>
/// Parses one cron line (PLAN.md 3.2 dialect). Returns null when the line has an Error diagnostic; every
/// problem found is added to <c>diags</c> (all field errors of a line are reported, not only the first).
/// </summary>
public static class CronParser
{
    sealed record FieldDef(string Name, int Min, int Max, int StarMax, int Span, string Unit, string[]? Names, int NameBase);

    static readonly FieldDef Minute = new("minute", 0, 59, 59, 59, "minutes", null, 0);
    static readonly FieldDef Hour = new("hour", 0, 23, 23, 23, "hours", null, 0);
    static readonly FieldDef Dom = new("day-of-month", 1, 31, 31, 30, "days", null, 0);
    static readonly FieldDef Month = new("month", 1, 12, 12, 11, "months", ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"], 1);
    // Day of week accepts 0-7 (7 = Sunday); `*` and `*/s` cover 0-6.
    static readonly FieldDef Dow = new("day-of-week", 0, 7, 6, 6, "weekdays", ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"], 0);

    static readonly Dictionary<string, string> Macros = new(StringComparer.OrdinalIgnoreCase)
    {
        ["@hourly"] = "0 * * * *",
        ["@daily"] = "0 0 * * *",
        ["@midnight"] = "0 0 * * *",
        ["@weekly"] = "0 0 * * 0",
        ["@monthly"] = "0 0 1 * *",
        ["@yearly"] = "0 0 1 1 *",
        ["@annually"] = "0 0 1 1 *",
    };

    sealed class Ctx(int line, List<CronNote> diags)
    {
        public int Errors;
        public void Error(string field, string message)
        {
            Errors++;
            diags.Add(new CronNote(CronNoteLevel.Error, line, field, $"line {line}, {field}: {message}"));
        }
        public void Info(string field, string message) =>
            diags.Add(new CronNote(CronNoteLevel.Info, line, field, $"line {line}, {field}: {message}"));
    }

    public static CronExpression? Parse(string line, int lineNo, List<CronNote> diags)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(diags);
        var c = new Ctx(lineNo, diags);
        var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length == 0)
        {
            c.Error("line", "the line is empty; expected 5 fields (minute hour day-of-month month day-of-week)");
            return null;
        }

        if (tokens[0].StartsWith('@'))
            return ParseMacro(tokens, c, lineNo, diags);

        switch (tokens.Length)
        {
            case 5:
                return ParseFields(tokens, 0, c);
            case 6:
            {
                string last = tokens[5];
                if (LooksLikeYear(last))
                {
                    c.Error("year", $"\"{last}\" looks like a year, which Task Scheduler can't do (it can't run in specific years)");
                    return null;
                }
                string sec = tokens[0];
                if (!IsAllDigits(sec) || sec.Length > 9 || int.Parse(sec, CultureInfo.InvariantCulture) != 0)
                {
                    c.Error("seconds", $"\"{sec}\" is a seconds value, which Task Scheduler can't do (it can't run at a specific second; only 0 is accepted)");
                    return null;
                }
                var expr = ParseFields(tokens, 1, c);
                if (expr is not null)
                    c.Info("seconds", $"the seconds field 0 was dropped; the line is treated as \"{string.Join(' ', tokens.Skip(1))}\"");
                return expr;
            }
            case 7:
                c.Error("seconds/year", $"7 fields means seconds and year (\"{string.Join(' ', tokens)}\"), which Task Scheduler can't do (it can't run at a specific second or in specific years)");
                return null;
            default:
                c.Error("line", $"expected 5 fields (minute hour day-of-month month day-of-week) but found {tokens.Length}: \"{string.Join(' ', tokens)}\"");
                return null;
        }
    }

    static CronExpression? ParseMacro(string[] tokens, Ctx c, int lineNo, List<CronNote> diags)
    {
        string macro = tokens[0];
        if (tokens.Length > 1)
        {
            c.Error("macro", $"\"{macro}\" is a shortcut and must stand alone on its line, but \"{string.Join(' ', tokens.Skip(1))}\" follows it");
            return null;
        }
        if (macro.Equals("@logon", StringComparison.OrdinalIgnoreCase))
        {
            return new CronExpression(CronList<int>.Empty, CronList<int>.Empty,
                new CronDom(true, Range(1, 31), false), CronMonths.All,
                new CronDow(true, CronDays.All, CronList<CronNth>.Empty), true);
        }
        if (macro.Equals("@reboot", StringComparison.OrdinalIgnoreCase))
        {
            c.Error("macro", "\"@reboot\" needs a start-up trigger, and start-up triggers need administrator rights; use @logon (when you sign in) instead");
            return null;
        }
        if (Macros.TryGetValue(macro, out var expansion))
            return Parse(expansion, lineNo, diags);
        c.Error("macro", $"\"{macro}\" isn't a known shortcut (use @hourly, @daily, @midnight, @weekly, @monthly, @yearly, @annually or @logon)");
        return null;
    }

    static CronExpression? ParseFields(string[] t, int o, Ctx c)
    {
        int before = c.Errors;
        var minutes = ParseSet(c, Minute, t[o], null);
        var hours = ParseSet(c, Hour, t[o + 1], null);

        bool lastDay = false;
        var days = ParseSet(c, Dom, t[o + 2], item => DomSpecial(c, item, ref lastDay));

        var months = ParseSet(c, Month, t[o + 3], null);

        var nth = new SortedSet<(int Day, int Week)>();
        var dow = ParseSet(c, Dow, t[o + 4], item => DowSpecial(c, item, nth));

        if (c.Errors > before || minutes is null || hours is null || days is null || months is null || dow is null)
            return null;

        bool domStar = IsStar(t[o + 2]);
        bool dowStar = IsStar(t[o + 4]);

        int monthBits = 0;
        foreach (int m in months) monthBits |= 1 << (m - 1);
        int dowBits = 0;
        foreach (int d in dow) dowBits |= 1 << (d % 7);

        return new CronExpression(
            new CronList<int>(minutes), new CronList<int>(hours),
            new CronDom(domStar, new CronList<int>(days), lastDay),
            (CronMonths)monthBits,
            new CronDow(dowStar, (CronDays)dowBits,
                new CronList<CronNth>(nth.Select(n => new CronNth(n.Week, (DayOfWeek)n.Day)))),
            false);
    }

    static bool IsStar(string s) => s == "*" || s == "?";

    // ----- field-specific items -----

    // Returns true when the item was recognised here (handled or reported); false to fall through to the generic parser.
    static bool DomSpecial(Ctx c, string item, ref bool last)
    {
        string u = item.ToUpperInvariant();
        if (u == "L") { last = true; return true; }
        if (u == "LW" || (u.Length > 1 && u[^1] == 'W' && IsAllDigits(u[..^1])))
        {
            c.Error(Dom.Name, $"\"{item}\" means \"nearest weekday\", which Task Scheduler can't do");
            return true;
        }
        if (u.StartsWith("L-", StringComparison.Ordinal))
        {
            c.Error(Dom.Name, $"\"{item}\" means \"{u[2..]} days before the end of the month\" (an offset from month end), which Task Scheduler can't do");
            return true;
        }
        if (u.Contains('#') || (u.Length > 1 && u[^1] == 'L' && IsAllDigits(u[..^1])))
        {
            c.Error(Dom.Name, $"\"{item}\" is a weekday pattern; # and nL only work in day-of-week (day-of-month accepts L for the last day)");
            return true;
        }
        return false;
    }

    static bool DowSpecial(Ctx c, string item, SortedSet<(int Day, int Week)> nth)
    {
        string u = item.ToUpperInvariant();
        int hash = u.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0)
        {
            string left = u[..hash], right = u[(hash + 1)..];
            bool dayOk = TryValue(c, Dow, left, out int day);
            if (!IsAllDigits(right) || right.Length > 3)
            {
                c.Error(Dow.Name, $"\"{item}\": the part after # must be a number from 1 to 4 (the k-th weekday of the month)");
                return true;
            }
            int k = int.Parse(right, CultureInfo.InvariantCulture);
            if (k == 5)
                c.Error(Dow.Name, $"\"{item}\": Task Scheduler can't target the 5th weekday of a month; use {left}L (last) instead");
            else if (k < 1 || k > 4)
                c.Error(Dow.Name, $"\"{item}\": the week number {k} is out of range 1-4");
            else if (dayOk)
                nth.Add((day % 7, k));
            return true;
        }
        if (u == "L")
        {
            c.Error(Dow.Name, "\"L\" (last day) only works in day-of-month; for the last Friday of the month write 5L here");
            return true;
        }
        if (u.Length > 1 && u[^1] == 'L')
        {
            string left = u[..^1];
            if (left.IndexOfAny(['-', '/', '*', '?']) >= 0)
            {
                c.Error(Dow.Name, $"\"{item}\": nL needs a single weekday before the L (for example 5L for the last Friday)");
                return true;
            }
            if (TryValue(c, Dow, left, out int day))
                nth.Add((day % 7, CronNth.Last));
            return true;
        }
        return false;
    }

    // ----- generic list / item parsing -----

    static SortedSet<int>? ParseSet(Ctx c, FieldDef f, string text, Func<string, bool>? special)
    {
        int before = c.Errors;
        var set = new SortedSet<int>();
        if (IsStar(text))
        {
            AddRange(set, f, f.Min, f.StarMax, 1);
            return set;
        }
        foreach (string item in text.Split(','))
        {
            if (item.Length == 0)
            {
                c.Error(f.Name, $"\"{text}\" has an empty entry (check the commas)");
                continue;
            }
            if (special is not null && special(item)) continue;
            ParseItem(c, f, item, set);
        }
        return c.Errors > before ? null : set;
    }

    static void ParseItem(Ctx c, FieldDef f, string item, SortedSet<int> set)
    {
        string baseText = item;
        int step = 1;
        bool hasStep = false;
        int slash = item.IndexOf('/', StringComparison.Ordinal);
        if (slash >= 0)
        {
            baseText = item[..slash];
            string stepText = item[(slash + 1)..];
            hasStep = true;
            if (!IsAllDigits(stepText) || stepText.Length > 6)
            {
                c.Error(f.Name, $"\"{item}\": the step \"{stepText}\" isn't a positive number");
                return;
            }
            step = int.Parse(stepText, CultureInfo.InvariantCulture);
            if (step == 0)
            {
                c.Error(f.Name, $"\"{item}\": a step of 0 isn't allowed");
                return;
            }
            if (step > f.Span)
            {
                c.Error(f.Name, $"\"{item}\": a step of {step} is larger than the {f.Name} range {f.Min}-{f.StarMax}, so cron can't express \"every {step} {f.Unit}\"");
                return;
            }
        }

        int lo, hi;
        if (IsStar(baseText))
        {
            lo = f.Min;
            hi = f.StarMax;
        }
        else if (baseText.Contains('-'))
        {
            int dash = baseText.IndexOf('-', StringComparison.Ordinal);
            string a = baseText[..dash], b = baseText[(dash + 1)..];
            if (a.Length == 0 || b.Length == 0 || b.Contains('-'))
            {
                c.Error(f.Name, $"\"{item}\" isn't a valid range (write it as start-end, for example 1-5)");
                return;
            }
            bool okA = TryValue(c, f, a, out lo);
            bool okB = TryValue(c, f, b, out hi);
            if (!okA || !okB) return;
            if (ReferenceEquals(f, Dow) && hi == 0 && lo > 0 && b.Equals("SUN", StringComparison.OrdinalIgnoreCase))
                hi = 7;   // FRI-SUN
            if (hi < lo)
            {
                string hint = IsAllDigits(a) && IsAllDigits(b) ? $"; split it into {lo}-{f.StarMax},{f.Min}-{hi}" : "";
                c.Error(f.Name, $"\"{item}\" is a descending range (cron ranges don't wrap around{hint})");
                return;
            }
        }
        else
        {
            if (!TryValue(c, f, baseText, out lo)) return;
            hi = hasStep ? f.Max : lo;
        }
        AddRange(set, f, lo, hi, step);
    }

    static void AddRange(SortedSet<int> set, FieldDef f, int lo, int hi, int step)
    {
        for (int v = lo; v <= hi; v += step)
            set.Add(ReferenceEquals(f, Dow) ? v % 7 : v);
    }

    static bool TryValue(Ctx c, FieldDef f, string token, out int value)
    {
        value = 0;
        token = token.ToUpperInvariant();
        if (IsAllDigits(token))
        {
            if (token.Length > 6 || (value = int.Parse(token, CultureInfo.InvariantCulture)) < f.Min || value > f.Max)
            {
                c.Error(f.Name, $"{token} is out of range {f.Min}-{f.Max}");
                return false;
            }
            return true;
        }
        if (f.Names is not null)
        {
            int idx = Array.IndexOf(f.Names, token);
            if (idx >= 0)
            {
                value = f.NameBase + idx;
                return true;
            }
            c.Error(f.Name, $"\"{token}\" isn't a number or a name ({f.Names[0]}-{f.Names[^1]})");
            return false;
        }
        c.Error(f.Name, $"\"{token}\" isn't a number");
        return false;
    }

    static bool IsAllDigits(string s)
    {
        if (s.Length == 0) return false;
        foreach (char ch in s) if (ch < '0' || ch > '9') return false;
        return true;
    }

    static bool LooksLikeYear(string s)
    {
        int run = 0;
        foreach (char ch in s)
        {
            if (ch >= '0' && ch <= '9') { if (++run >= 4) return true; }
            else run = 0;
        }
        return false;
    }

    static CronList<int> Range(int lo, int hi) => new(Enumerable.Range(lo, hi - lo + 1));
}
