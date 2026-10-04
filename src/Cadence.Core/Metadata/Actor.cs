using System.Text;

namespace Cadence.Core.Metadata;

public enum ActorKind { Unknown, App, Cli, Agent }

/// <summary>
/// Who made a write (PLAN 12.4.1): the Cadence app, the command line, or an AI agent identified by its MCP client's name. Stored in the
/// task's metadata as a token (<c>app</c>, <c>cli</c>, <c>agent:&lt;name&gt;</c>); a job written by v0.1.0 has none (<see cref="Unknown"/>).
/// Pure; parsing is tolerant: anything outside the grammar is <see cref="Unknown"/>, never an error.
/// </summary>
public sealed record Actor(ActorKind Kind, string? AgentName)
{
    public const int MaxNameLength = 64;
    public const string UnknownAgentName = "unknown agent";
    const string AgentPrefix = "agent:";

    public static readonly Actor App = new(ActorKind.App, null);
    public static readonly Actor Cli = new(ActorKind.Cli, null);
    public static readonly Actor Unknown = new(ActorKind.Unknown, null);

    /// <summary>An agent actor; the client's name is cleaned by <see cref="SanitizeName"/> (null or blank becomes "unknown agent").</summary>
    public static Actor Agent(string? clientName) => new(ActorKind.Agent, SanitizeName(clientName));

    /// <summary>
    /// Drops control and format (bidi, zero-width) characters, trims, collapses runs of whitespace to one space, cuts to 64 UTF-16 characters (never in the middle of a
    /// surrogate pair) and falls back to "unknown agent" when nothing is left.
    /// </summary>
    public static string SanitizeName(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return UnknownAgentName;
        var sb = new StringBuilder(raw.Length);
        bool space = false;
        foreach (char c in raw)
        {
            if (char.IsControl(c)) continue;
            // [Review R] Invisible format characters (bidi overrides such as U+202E, zero-width spaces and joiners, U+FEFF) are dropped too:
            // they let a client name display as something else ("ppa" reversed) in the UI, the stale-save message and the logs.
            if (char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format) continue;
            if (char.IsWhiteSpace(c)) { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(c);
        }
        string s = sb.Length > MaxNameLength ? sb.ToString(0, MaxNameLength) : sb.ToString();
        if (s.Length > 0 && char.IsHighSurrogate(s[^1]) && sb.Length > MaxNameLength) s = s[..^1];
        s = s.Trim();
        return s.Length == 0 ? UnknownAgentName : s;
    }

    /// <summary>The metadata token: "app", "cli", "agent:&lt;name&gt;"; null for <see cref="ActorKind.Unknown"/> (nothing is written).</summary>
    public string? Token => Kind switch
    {
        ActorKind.App => "app",
        ActorKind.Cli => "cli",
        ActorKind.Agent => AgentPrefix + SanitizeName(AgentName),
        _ => null,
    };

    /// <summary>Tolerant: null, empty or anything outside the grammar is Unknown (a hand edit, a future writer, garbage).</summary>
    public static Actor Parse(string? token)
    {
        if (token is null) return Unknown;
        string t = token.Trim();
        if (t == "app") return App;
        if (t == "cli") return Cli;
        if (t.StartsWith(AgentPrefix, StringComparison.Ordinal))
        {
            string name = t[AgentPrefix.Length..];
            // "agent:" alone, or a name that is only control characters / blanks, is not a name.
            bool anything = false;
            foreach (char c in name) if (!char.IsControl(c) && !char.IsWhiteSpace(c)) { anything = true; break; }
            return anything ? Agent(name) : Unknown;
        }
        return Unknown;
    }

    /// <summary>Text for a person: "Cadence (the app)", "the command line", the agent's name, "unknown".</summary>
    public string Describe() => Kind switch
    {
        ActorKind.App => "Cadence (the app)",
        ActorKind.Cli => "the command line",
        ActorKind.Agent => AgentDisplay(SanitizeName(AgentName)),
        _ => "unknown",
    };

    /// <summary>
    /// [Review R] clientInfo is self-reported: an agent calling itself "Cadence (the app)" must not read like the owner's own change in the
    /// stale-save message or the UI. A name equal (ignoring case) to one of the non-agent descriptions gets " (AI agent)" appended.
    /// </summary>
    static string AgentDisplay(string name) => LooksReserved(name) ? name + " (AI agent)" : name;

    /// <summary>The descriptions an agent's name must never pass for (compared as <see cref="Skeleton"/>s).</summary>
    static readonly string[] ReservedNames = ["cadence (the app)", "the command line", "unknown", "cadence", "app", "cli", "in the app", "you"];

    /// <summary>
    /// [CP-E] True when the name equals, or merely LOOKS like, one of the owner's descriptions: compared after NFKC (fullwidth brackets and letters become ASCII),
    /// with invisible characters dropped, whitespace collapsed, case ignored and the common Cyrillic, Greek and Armenian look-alikes of the letters in those
    /// names read as Latin. Before, "Cadence (the аpp)" with a Cyrillic "а" or "Cadence （the app）" with fullwidth brackets was shown unmarked, exactly like
    /// the owner's own lines in the Agent activity panel.
    /// </summary>
    static bool LooksReserved(string name)
    {
        string key = Skeleton(name);
        foreach (string r in ReservedNames)
            if (key == r) return true;
        return false;
    }

    static string Skeleton(string s)
    {
        string n;
        try { n = s.Normalize(NormalizationForm.FormKC); }
        catch (ArgumentException) { n = s; }   // a lone surrogate: compare the text as it is
        var sb = new StringBuilder(n.Length);
        bool space = false;
        foreach (char raw in n.ToLowerInvariant())
        {
            if (char.IsControl(raw) || char.GetUnicodeCategory(raw) == System.Globalization.UnicodeCategory.Format) continue;
            if (char.IsWhiteSpace(raw)) { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(Latin(raw));
        }
        return sb.ToString();
    }

    /// <summary>Look-alikes of the letters used in <see cref="ReservedNames"/>, by code point (Cyrillic, Greek, Armenian, dotless i).</summary>
    static char Latin(char c) => (int)c switch
    {
        0x0430 or 0x03B1 => 'a',
        0x0441 or 0x03F2 => 'c',
        0x0501 => 'd',
        0x0435 or 0x03B5 => 'e',
        0x04BB => 'h',
        0x0456 or 0x03B9 or 0x0131 => 'i',
        0x043A or 0x03BA => 'k',
        0x04CF => 'l',
        0x0578 => 'n',
        0x043E or 0x03BF or 0x0585 => 'o',
        0x0440 or 0x03C1 => 'p',
        0x057D or 0x03C5 => 'u',
        0x0443 => 'y',
        _ => c,
    };

    /// <summary>"app", "cli", "agent" or "unknown" (the <c>byKind</c> of the agent views).</summary>
    public string KindName => Kind switch
    {
        ActorKind.App => "app",
        ActorKind.Cli => "cli",
        ActorKind.Agent => "agent",
        _ => "unknown",
    };
}
