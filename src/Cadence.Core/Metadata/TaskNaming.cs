using System.Text;

namespace Cadence.Core.Metadata;

/// <summary>Turns a display name into a Task Scheduler task name that the wrapper's task-path rule accepts (PLAN 3.8).</summary>
public static class TaskNaming
{
    public const int MaxLength = 80;
    const string Fallback = "job";

    public static string ToTaskName(string displayName, IReadOnlySet<string> existingNames)
    {
        ArgumentNullException.ThrowIfNull(displayName);
        ArgumentNullException.ThrowIfNull(existingNames);

        string name = Sanitize(displayName);
        if (!IsTaken(name, existingNames)) return name;
        for (int n = 2; ; n++)
        {
            string suffix = " (" + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
            string candidate = Cut(name, MaxLength - suffix.Length) + suffix;
            if (!IsTaken(candidate, existingNames)) return candidate;
        }
    }

    /// <summary>Keeps [A-Za-z0-9 _.,()+-], maps everything else to '-', collapses runs of '-', of spaces and of dots, trims spaces, caps at 80.</summary>
    public static string Sanitize(string displayName)
    {
        var sb = new StringBuilder(displayName.Length);
        foreach (char c in displayName)
        {
            char o = IsAllowed(c) ? c : '-';
            if (sb.Length > 0 && sb[^1] == o && (o == '-' || o == ' ' || o == '.')) continue;   // [AG-P22] a run of dots too: Task Scheduler refuses any task name containing ".." (path traversal guard), so "a..b" could never be registered
            sb.Append(o);
        }
        // [CP-A] Task Scheduler keeps each task as a file under System32\Tasks: Windows drops a trailing dot from file names,
        // and device names (CON, NUL, COM1, ... also with an extension) can't be file names at all.
        string s = Cut(sb.ToString().Trim(' '), MaxLength).TrimEnd(' ', '.');
        if (s.Length == 0) return Fallback;   // empty, or only dots and spaces ("." / "..")
        int dot = s.IndexOf('.', StringComparison.Ordinal);
        string stem = (dot < 0 ? s : s[..dot]).TrimEnd(' ');
        if (IsDeviceName(stem)) s = Cut(stem + "-job" + (dot < 0 ? "" : s[dot..]), MaxLength).TrimEnd(' ', '.');
        return s;
    }

    static bool IsDeviceName(string stem) =>
        stem.ToUpperInvariant() is "CON" or "PRN" or "AUX" or "NUL"
        || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9');

    static string Cut(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd(' ', '.');

    static bool IsAllowed(char c) =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' || c is ' ' or '_' or '.' or ',' or '(' or ')' or '+' or '-';

    static bool IsTaken(string name, IReadOnlySet<string> existing)
    {
        foreach (string e in existing)
            if (string.Equals(e, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
