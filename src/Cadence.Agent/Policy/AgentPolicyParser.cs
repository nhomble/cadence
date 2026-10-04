using System.Globalization;
using System.Text;
using Cadence.Core.Metadata;

namespace Cadence.Agent.Policy;

/// <summary>The registry types the policy parser distinguishes (a REG_SZ "0" is not a DWORD 0).</summary>
public enum PolicyValueKind { DWord, String, Other }

/// <summary>One registry value as read: its kind and its value (an <see cref="int"/> for a DWORD, a <see cref="string"/> for a string, anything for Other).</summary>
public sealed record PolicyValue(PolicyValueKind Kind, object? Value);

/// <summary>
/// Turns the raw registry values of <c>HKCU\Software\&lt;Folder&gt;\Agents</c> into an <see cref="AgentPolicy"/> (PLAN 12.4.2, [CP-R4]). Pure, so the
/// hostile-value rules are unit-tested without the registry. Only the suspension value fails closed: a missing key or value means "not suspended"
/// (the owner opted in by configuring the server), but <c>Suspended</c> that is not a REG_DWORD 0 or 1 means "refuse". A limit that is the wrong
/// type or out of range falls back to its default and leaves a note in <see cref="AgentPolicy.ReadProblem"/>. Text read from the registry is never
/// echoed into a message (only whole numbers and fixed words are), because anything running as the user can write it.
/// </summary>
public static class AgentPolicyParser
{
    public const string SuspendedName = "Suspended";
    public const string SuspendedAtName = "SuspendedAt";
    public const string SuspendedByName = "SuspendedBy";
    public const string MinIntervalName = "MinIntervalMinutes";
    public const string MaxJobsName = "MaxAgentJobs";
    public const string ChangedByName = "ChangedBy";
    public const string ChangedAtName = "ChangedAt";
    public const string ChangedFromName = "ChangedFrom";
    public const string ChangedReasonName = "ChangedReason";

    public const int MaxFromLength = 200;

    /// <param name="values">The values by name (null = the key does not exist).</param>
    public static AgentPolicy Parse(IReadOnlyDictionary<string, PolicyValue>? values)
    {
        if (values is null) return AgentPolicy.Defaults;
        var notes = new List<string>();

        bool suspended = false;
        string? failClosed = null;
        if (values.TryGetValue(SuspendedName, out PolicyValue? s))
        {
            if (s.Kind != PolicyValueKind.DWord || s.Value is not int n)
                failClosed = "Suspended is not a number (it must be a DWORD 0 or 1)";
            else if (n == 1) suspended = true;
            else if (n != 0)
                failClosed = "Suspended is " + n.ToString(CultureInfo.InvariantCulture) + " (it must be 0 or 1)";
        }
        if (failClosed is not null) suspended = true;

        DateTimeOffset? suspendedAt = ReadTime(values, SuspendedAtName);
        if (suspended && failClosed is null && values.ContainsKey(SuspendedAtName) && suspendedAt is null) notes.Add("SuspendedAt is not a date");
        string? suspendedBy = ReadWho(values, SuspendedByName);

        int min = ReadInt(values, MinIntervalName, AgentPolicy.DefaultMinIntervalMinutes, AgentPolicy.IsValidMinInterval, "1 to 1440", notes);
        int max = ReadInt(values, MaxJobsName, AgentPolicy.DefaultMaxAgentJobs, AgentPolicy.IsValidMaxAgentJobs, "0 to 500", notes);

        string? changedBy = ReadWho(values, ChangedByName);
        DateTimeOffset? changedAt = ReadTime(values, ChangedAtName);
        string? changedFrom = values.TryGetValue(ChangedFromName, out PolicyValue? f) && f.Kind == PolicyValueKind.String && f.Value is string fs ? CleanFrom(fs) : null;
        string? changedReason = values.TryGetValue(ChangedReasonName, out PolicyValue? rv) && rv.Kind == PolicyValueKind.String && rv.Value is string rs ? CleanFrom(rs) : null;

        string? problem = failClosed;
        if (notes.Count > 0) problem = problem is null ? string.Join("; ", notes) : problem + "; " + string.Join("; ", notes);
        return new AgentPolicy(suspended, suspendedAt, suspendedBy, min, max, problem, failClosed is not null, changedBy, changedAt, changedFrom, changedReason);
    }

    /// <summary>
    /// [OWNER-Q14] The route token of a switch change: "app", "cli" or "agent:&lt;name&gt;" (the name cleaned and capped by <see cref="Actor.SanitizeName"/>).
    /// Anything else (blank, "agent" without a name, a made-up word) is null.
    /// </summary>
    public static string? NormalizeWho(string? by)
    {
        if (by is null) return null;
        Actor a = Actor.Parse(by);
        return a.Kind == ActorKind.Unknown ? null : a.Token;
    }

    static int ReadInt(IReadOnlyDictionary<string, PolicyValue> values, string name, int fallback, Func<int, bool> valid, string range, List<string> notes)
    {
        if (!values.TryGetValue(name, out PolicyValue? v)) return fallback;
        if (v.Kind != PolicyValueKind.DWord || v.Value is not int n)
        {
            notes.Add(name + " is not a number (using " + fallback.ToString(CultureInfo.InvariantCulture) + ")");
            return fallback;
        }
        if (!valid(n))
        {
            notes.Add(name + " is " + n.ToString(CultureInfo.InvariantCulture) + " (allowed " + range + "; using " + fallback.ToString(CultureInfo.InvariantCulture) + ")");
            return fallback;
        }
        return n;
    }

    static DateTimeOffset? ReadTime(IReadOnlyDictionary<string, PolicyValue> values, string name)
    {
        if (!values.TryGetValue(name, out PolicyValue? v) || v.Kind != PolicyValueKind.String || v.Value is not string text || text.Length > 64) return null;
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset t) ? t : null;
    }

    /// <summary>Only the route tokens Cadence writes ("app", "cli", "agent:&lt;name&gt;", the name cleaned and capped); anything else reads as unknown (null).</summary>
    static string? ReadWho(IReadOnlyDictionary<string, PolicyValue> values, string name)
    {
        if (!values.TryGetValue(name, out PolicyValue? v) || v.Kind != PolicyValueKind.String || v.Value is not string text || text.Length > 200) return null;
        return NormalizeWho(text);
    }

    /// <summary>Control and format characters dropped, whitespace (including tabs and line breaks) collapsed to single spaces, cut to <see cref="MaxFromLength"/>.</summary>
    public static string? CleanFrom(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var sb = new StringBuilder(Math.Min(text.Length, MaxFromLength));
        bool space = false;
        foreach (char c in text)
        {
            // Tabs and line breaks are whitespace: they become one space, never a join of the words around them.
            if (char.IsWhiteSpace(c)) { space = sb.Length > 0; continue; }
            if (char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format) continue;
            if (space) { sb.Append(' '); space = false; }
            if (sb.Length >= MaxFromLength) break;
            sb.Append(c);
        }
        if (sb.Length > 0 && char.IsHighSurrogate(sb[^1])) sb.Length--;
        string s = sb.ToString().Trim();
        return s.Length == 0 ? null : s;
    }
}
