using System.Text;
using Cadence.Agent;
using Cadence.Core.Metadata;
using Cadence.History;
using Cadence.Scheduler;
using Cadence.TestHarness;

namespace Cadence.Tests;

/// <summary>Regression tests for the defects fixed in the Review R (PLAN.md section 11, docs\REVIEW-2026-10-01.md).</summary>
public static class ReviewRTests
{
    public static void OneFolderRuleEverywhere()
    {
        T.True("Cadence", ManagedFolder.TryNormalize("cadence", out string f) && f == "Cadence");
        T.True("CadenceTest", ManagedFolder.TryNormalize("CADENCETEST", out f) && f == "CadenceTest");
        foreach (string? bad in new[] { null, "", " ", "Microsoft", "Cadence\\..\\Microsoft", "Cadence ", "Cadence.", "\\Cadence", "CadenceTest2", "Cadence" + (char)0x200B })
            T.True("refused: '" + (bad ?? "null") + "'", !ManagedFolder.TryNormalize(bad, out _));
        // The MCP host uses the same rule (it used to keep its own copy).
        foreach (string? s in new[] { "cadence", "CadenceTest", "Microsoft", null, "Cadence." })
            T.Eq("McpHost agrees on '" + (s ?? "null") + "'", ManagedFolder.TryNormalize(s, out string a), McpHost.TryNormalizeFolder(s, out string b) && a == b);
    }

    public static void TheRealGatewayOnlyManagesTheTwoFolders()
    {
        // Constructing the gateway validates only; it opens no COM session.
        T.True("Cadence accepted", new TaskSchedulerGateway(new SchedulerOptions("Cadence", "Cadence")).FolderPath == "\\Cadence");
        T.True("CadenceTest accepted", new TaskSchedulerGateway(new SchedulerOptions("CadenceTest", "CadenceTest")).FolderPath == "\\CadenceTest");
        T.Throws<ArgumentException>("\\Microsoft refused", () => new TaskSchedulerGateway(new SchedulerOptions("Microsoft", "Microsoft")));
        T.Throws<ArgumentException>("non-canonical case refused (it would get another writer-lock mutex)", () => new TaskSchedulerGateway(new SchedulerOptions("cadencetest", "cadencetest")));
        T.Throws<ArgumentException>("a runs root outside the two names refused", () => new TaskSchedulerGateway(new SchedulerOptions("CadenceTest", "..\\Elsewhere")));
        T.Throws<ArgumentException>("a path refused", () => new TaskSchedulerGateway(new SchedulerOptions("Cadence\\Sub", "Cadence")));
    }

    public static void AgentNamesLoseInvisibleFormatCharacters()
    {
        string rlo = "x" + (char)0x202E + "ppa";
        T.Eq("bidi override dropped", "xppa", Actor.SanitizeName(rlo));
        T.Eq("zero-width space and BOM dropped", "Claude Code", Actor.SanitizeName("Cla" + (char)0x200B + "ude" + (char)0xFEFF + " Code"));
        T.Eq("only format characters -> unknown agent", Actor.UnknownAgentName, Actor.SanitizeName(new string((char)0x2066, 3)));
        T.Eq("a stored token is cleaned on read too", "xppa", Actor.Parse("agent:" + rlo).Describe());
        T.Eq("ordinary non-ASCII names are kept", "Agent " + (char)0x00E9 + (char)0x00E8, Actor.SanitizeName("Agent " + (char)0x00E9 + (char)0x00E8));
    }

    public static void AnAgentCannotPassItselfOffAsTheAppOrTheCommandLine()
    {
        T.Eq("'Cadence (the app)'", "Cadence (the app) (AI agent)", Actor.Agent("Cadence (the app)").Describe());
        T.Eq("case-insensitive", "cadence (THE app) (AI agent)", Actor.Agent("cadence (THE app)").Describe());
        T.Eq("'the command line'", "the command line (AI agent)", Actor.Agent("the command line").Describe());
        T.Eq("'unknown'", "unknown (AI agent)", Actor.Agent("unknown").Describe());
        T.Eq("via a bidi trick too", "Cadence (the app) (AI agent)", Actor.Agent("Cadence" + (char)0x200B + " (the app)").Describe());
        T.Eq("ordinary names unchanged", "Claude Code", Actor.Agent("Claude Code").Describe());
        T.Eq("the stored token is not changed", "agent:Cadence (the app)", Actor.Agent("Cadence (the app)").Token);
        T.Eq("the real app still reads as the app", "Cadence (the app)", Actor.App.Describe());
    }

    public static void HugeOutputFilesAreReadOnlyFromTheTail()
    {
        string root = Path.Combine(Path.GetTempPath(), "cadence-reviewr-" + Guid.NewGuid().ToString("N"));
        try
        {
            const string job = "rvwr0002", run = "20261001-010203-004";
            Directory.CreateDirectory(Path.Combine(root, job));
            string file = Path.Combine(root, job, run + ".out.txt");
            var store = new RunRecordStore(root);

            // 3 MiB of 3-byte characters with a marker at the very end: the read is capped and starts on a character boundary.
            var sb = new StringBuilder();
            while (sb.Length * 3 < 3 * 1024 * 1024) sb.Append((char)0x20AC);
            sb.Append("END-MARKER");
            File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
            string? text = store.ReadOutput(job, run, false);
            T.True("read something", text is not null);
            T.True("at most MaxOutputBytes were decoded", Encoding.UTF8.GetByteCount(text!) <= RunRecordStore.MaxOutputBytes);
            T.True("it is the tail of the file", text!.EndsWith("END-MARKER", StringComparison.Ordinal));
            T.True("no broken character at the cut", text[0] == (char)0x20AC);

            // A normal file (the wrapper keeps at most 256 KB) is read whole, BOM handling unchanged.
            File.WriteAllText(file, "small " + (char)0x00FC, new UTF8Encoding(true));
            T.Eq("small file whole, BOM removed", "small " + (char)0x00FC, store.ReadOutput(job, run, false));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
