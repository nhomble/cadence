using Cadence.Agent;
using Cadence.Core.Metadata;
using Cadence.Mcp;
using Cadence.TestHarness;

namespace Cadence.Tests;

/// <summary>PLAN 12.4.1: the Actor token grammar, tolerant parsing, name sanitizing and the texts shown to people.</summary>
public static class ActorTests
{
    public static void TokensAndTexts()
    {
        T.Eq("app token", "app", Actor.App.Token);
        T.Eq("cli token", "cli", Actor.Cli.Token);
        T.Eq("agent token", "agent:Claude Code", Actor.Agent("Claude Code").Token);
        T.Eq("unknown token is null (nothing is written)", null, Actor.Unknown.Token);

        T.Eq("app describe", "Cadence (the app)", Actor.App.Describe());
        T.Eq("cli describe", "the command line", Actor.Cli.Describe());
        T.Eq("agent describe", "Claude Code", Actor.Agent("Claude Code").Describe());
        T.Eq("unknown describe", "unknown", Actor.Unknown.Describe());

        T.Eq("app kind name", "app", Actor.App.KindName);
        T.Eq("cli kind name", "cli", Actor.Cli.KindName);
        T.Eq("agent kind name", "agent", Actor.Agent("x").KindName);
        T.Eq("unknown kind name", "unknown", Actor.Unknown.KindName);

        T.True("agents with the same name are equal", Actor.Agent("X") == Actor.Agent("  X "));
        T.True("agents with other names differ", Actor.Agent("X") != Actor.Agent("Y"));
    }

    public static void ParseIsTolerant()
    {
        T.Eq("app", Actor.App, Actor.Parse("app"));
        T.Eq("cli", Actor.Cli, Actor.Parse("cli"));
        T.Eq("agent", Actor.Agent("Claude Code"), Actor.Parse("agent:Claude Code"));
        T.Eq("surrounding blanks are ignored", Actor.App, Actor.Parse("  app\t"));
        T.Eq("agent name is sanitized on parse", Actor.Agent("A B"), Actor.Parse("agent:  A \t  B  "));

        string?[] garbage = [null, "", " ", "App", "APP", "cli2", "admin", "agent", "agent:", "agent:   ", "agent:\u0001\u0002", "agents:x", "{\"a\":1}", "app;cli", "unknown"];
        foreach (string? g in garbage)
            T.Eq("garbage '" + (g ?? "null").Replace("\u0001", "^A", StringComparison.Ordinal).Replace("\u0002", "^B", StringComparison.Ordinal) + "' is Unknown", ActorKind.Unknown, Actor.Parse(g).Kind);

        string longName = new('n', 200);
        Actor cut = Actor.Parse("agent:" + longName);
        T.Eq("a 200 character name is cut to 64", 64, cut.AgentName!.Length);
    }

    public static void SanitizeNameTable()
    {
        (string? Raw, string Expected)[] table =
        [
            (null, "unknown agent"),
            ("", "unknown agent"),
            ("   ", "unknown agent"),
            ("\u0001\u0002\u001f", "unknown agent"),
            ("Claude Code", "Claude Code"),
            ("  Claude   Code  ", "Claude Code"),
            ("Claude\tCode\r\nAgent", "ClaudeCodeAgent"),   // tab and line breaks are control characters: dropped (PLAN 12.4.1), not turned into spaces
            ("Cla\u0001ude\u007f Code", "Claude Code"),
            ("café 日本", "café 日本"),
            (new string('a', 64), new string('a', 64)),
            (new string('a', 65), new string('a', 64)),
            (new string('a', 200), new string('a', 64)),
        ];
        foreach ((string? raw, string expected) in table)
            T.Eq("sanitize '" + (raw is null ? "null" : raw.Length > 20 ? raw[..20] + "..." : raw).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal) + "'", expected, Actor.SanitizeName(raw));

        // A surrogate pair is never cut in half at the limit.
        string pair = new string('a', 63) + char.ConvertFromUtf32(0x1F600);
        string cutPair = Actor.SanitizeName(pair);
        T.True("a pair straddling the limit is dropped whole", cutPair.Length == 63 && !char.IsHighSurrogate(cutPair[^1]), cutPair.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        T.Eq("a pair that fits is kept", 65 - 1, Actor.SanitizeName(new string('a', 62) + char.ConvertFromUtf32(0x1F600)).Length);

        T.Eq("the protocol's ClientIdentity uses the same rule", Actor.SanitizeName("  X  Y "), new ClientIdentity("  X  Y ", null, null).DisplayName);
        T.Eq("identity display name: title first", "Claude Code", new ClientIdentity("claude-code", "Claude Code", "2.1").DisplayName);
        T.Eq("identity display name: name when no title", "claude-code", new ClientIdentity("claude-code", null, null).DisplayName);
        T.Eq("identity display name: nothing at all", "unknown agent", new ClientIdentity("", "  ", null).DisplayName);
    }

    public static void AgentTokenIsAlwaysSafeEvenForAHandBuiltActor()
    {
        var raw = new Actor(ActorKind.Agent, "x\r\ny\u0001");
        T.Eq("token is sanitized again (control characters dropped)", "agent:xy", raw.Token);
        T.Eq("describe is sanitized again", "xy", raw.Describe());
        T.Eq("an agent with no name reads as unknown agent", "agent:unknown agent", new Actor(ActorKind.Agent, null).Token);
        T.Eq("round trip of a token", Actor.Agent("Claude Code"), Actor.Parse(Actor.Agent("Claude Code").Token));
    }
}
