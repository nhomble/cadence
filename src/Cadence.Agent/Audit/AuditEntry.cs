using System.Globalization;
using System.Text;

namespace Cadence.Agent.Audit;

/// <summary>
/// One line of the agent audit log (PLAN 12.4.3). <c>Outcome</c> is ok, refused, error, connected, disconnected, suspended or resumed; <c>Tool</c> is a
/// tool name, "session", "app" or "cli". There is deliberately NO field for environment values or argument text: the log carries names, ids and
/// short sentences only (Q11). Every string is cleaned and capped when it is written (<see cref="AuditText"/>), whoever supplied it.
/// <c>Count</c> ([CP-D] D4) is how many identical consecutive refusals (or "unchanged" calls) this line stands for (1 for an ordinary line); a rate-limited flood is one
/// <c>refused</c>/<c>suppressed</c> line with the number of calls that were not logged one by one.
/// </summary>
public sealed record AuditEntry(DateTimeOffset At, string Session, string Client, string? ClientVersion, string Tool,
    string? JobId, string? JobName, string Outcome, string? Code, string Detail, int Count = 1);

public interface IAuditLog
{
    /// <summary>Appends one line. May throw <see cref="IOException"/> (or a subclass); tools use <see cref="AuditLogExtensions.TryAppend"/>, which never throws.</summary>
    void Append(AuditEntry entry);

    /// <summary>The newest <paramref name="max"/> entries, oldest first (an unreadable or foreign line is skipped).</summary>
    IReadOnlyList<AuditEntry> Tail(int max);

    /// <summary>
    /// The newest <paramref name="max"/> entries of every file of the log merged in time order ([CP-D] D4: the owner's file and the agents' file), oldest first, or newest first
    /// when <paramref name="newestFirst"/>. The default is <see cref="Tail"/> (a log with one stream).
    /// </summary>
    IReadOnlyList<AuditEntry> TailMerged(int max, bool newestFirst = false)
    {
        IReadOnlyList<AuditEntry> t = Tail(max);
        return newestFirst ? [.. t.Reverse()] : t;
    }

    string FilePath { get; }
}

public static class AuditLogExtensions
{
    /// <summary>Appends and never throws. Returns null on success, otherwise the warning the tool result gains: "The agent log could not be written: &lt;reason&gt;".</summary>
    public static string? TryAppend(this IAuditLog log, AuditEntry entry)
    {
        try
        {
            log.Append(entry);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException or TimeoutException)
        {
            // [RT-2] The type name only: the exception text names the log file (a path in the owner's profile), which an agent must not be told.
            return "The agent log could not be written: " + ex.GetType().Name + ".";
        }
    }
}

/// <summary>An audit log that keeps nothing (a host that has no log, tests that do not care).</summary>
public sealed class NullAuditLog : IAuditLog
{
    public static readonly NullAuditLog Instance = new();

    public string FilePath => "";

    public void Append(AuditEntry entry) { }

    public IReadOnlyList<AuditEntry> Tail(int max) => [];
}

/// <summary>An audit log in memory (unit tests). Entries are cleaned exactly as the file log cleans them.</summary>
public sealed class MemoryAuditLog : IAuditLog
{
    readonly object _gate = new();
    readonly List<AuditEntry> _entries = [];

    public string FilePath => "(memory)";

    /// <summary>When set, <see cref="Append"/> throws this (tests of "an audit failure never fails a tool call").</summary>
    public Exception? ThrowOnAppend { get; set; }

    public IReadOnlyList<AuditEntry> All { get { lock (_gate) return [.. _entries]; } }

    public void Append(AuditEntry entry)
    {
        if (ThrowOnAppend is { } ex) throw ex;
        AuditEntry clean = AuditText.Clean(entry);
        lock (_gate) _entries.Add(clean);
    }

    public IReadOnlyList<AuditEntry> Tail(int max)
    {
        lock (_gate) return [.. _entries.Skip(Math.Max(0, _entries.Count - Math.Max(0, max)))];
    }
}

/// <summary>
/// Cleaning of every string that reaches the audit log ([CP-R3]): control and invisible format characters are dropped (so no line break, no U+2028/NEL
/// and no bidi trick survives), whitespace collapses to one space, the text is cut to its cap without splitting a surrogate pair. The same rules as
/// <c>Actor.SanitizeName</c>, with a cap per field.
/// </summary>
public static class AuditText
{
    public const int MaxClient = 64;
    public const int MaxClientVersion = 32;
    public const int MaxJobName = 100;
    public const int MaxTool = 64;
    public const int MaxCode = 40;
    public const int MaxDetail = 300;
    public const int MaxSession = 16;
    public const int MaxJobId = 16;
    public const int MaxOutcome = 20;

    public static string Clean(string? raw, int max)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var sb = new StringBuilder(Math.Min(raw.Length, max));
        bool space = false;
        foreach (char c in raw)
        {
            if (char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format) continue;
            if (char.IsWhiteSpace(c)) { space = sb.Length > 0; continue; }
            if (space) { if (sb.Length >= max) break; sb.Append(' '); space = false; }
            if (sb.Length >= max) break;
            sb.Append(c);
        }
        if (sb.Length > 0 && char.IsHighSurrogate(sb[^1])) sb.Length--;
        return sb.ToString().Trim();
    }

    /// <summary>The entry with every field cleaned and capped (the client name by <c>Actor.SanitizeName</c>, so it is never empty).</summary>
    public static AuditEntry Clean(AuditEntry e)
    {
        string tool = Clean(e.Tool, MaxTool);
        string outcome = Clean(e.Outcome, MaxOutcome);
        string? version = Clean(e.ClientVersion, MaxClientVersion);
        string? jobId = Clean(e.JobId, MaxJobId);
        string? job = Clean(e.JobName, MaxJobName);
        string? code = Clean(e.Code, MaxCode);
        return new AuditEntry(e.At, Clean(e.Session, MaxSession), Cadence.Core.Metadata.Actor.SanitizeName(e.Client), version.Length == 0 ? null : version,
            tool.Length == 0 ? "unknown" : tool, jobId.Length == 0 ? null : jobId, job.Length == 0 ? null : job, outcome.Length == 0 ? "error" : outcome,
            code.Length == 0 ? null : code, Clean(e.Detail, MaxDetail), Math.Max(1, e.Count));
    }

    /// <summary>For printing a line read from a file someone may have edited: every control character shown as \xNN, \n, \r or \t instead of acting on the terminal.</summary>
    public static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c == '\n') sb.Append("\\n");
            else if (c == '\r') sb.Append("\\r");
            else if (c == '\t') sb.Append("\\t");
            else if (char.IsControl(c) || c == (char)0x2028 || c == (char)0x2029 || char.GetUnicodeCategory(c) == UnicodeCategory.Format)
                sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
