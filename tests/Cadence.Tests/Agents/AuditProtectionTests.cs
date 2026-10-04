using System.Globalization;
using System.Text.Json;
using Cadence.Agent.Audit;
using Cadence.TestHarness;

namespace Cadence.Tests;

/// <summary>
/// Phase 23 [CP-D] D4 / red-team N1: an agent that floods the log must not erase the owner's entries. Owner and switch events live in their own file; refusals fold into counted
/// lines and are rate-limited per session and minute; Tail reads both streams merged in time order. All in a TEMP root with a fixed, injected clock.
/// </summary>
public static class AuditProtectionTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 1, 14, 2, 11, TimeSpan.FromHours(-4));

    sealed class Root : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cadence-auditp-" + Guid.NewGuid().ToString("N"));
        public DateTimeOffset Now { get; set; } = T0;

        public FileAuditLog Log(long rotateAt = FileAuditLog.DefaultRotateAtBytes, long? ownerRotateAt = null) => new("CadenceTest", rotateAt, Path, () => Now, ownerRotateAt);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* temp: leave it */ }
        }
    }

    static AuditEntry E(string outcome = "ok", string tool = "cadence_save_job", string client = "Claude Code", string detail = "Created", string session = "sess0001", string? code = null, DateTimeOffset? at = null) =>
        new(at ?? T0, session, client, null, tool, null, null, outcome, code, detail);

    static long Bytes(string dir) => Directory.GetFiles(dir).Sum(f => new FileInfo(f).Length);

    static string AgentsDir(Root r) => System.IO.Path.Combine(r.Path, "agents");

    static string[] Names(Root r) => [.. Directory.GetFiles(AgentsDir(r)).Select(System.IO.Path.GetFileName).Order()!];

    public static void OwnerAndSwitchEventsGoToTheirOwnFile()
    {
        using var root = new Root();
        FileAuditLog log = root.Log();
        log.Append(E("suspended", "cli", "Cadence (command line)", "Suspended from the command line"));
        log.Append(E("resumed", "app", "Cadence (the app)", "Resumed"));
        log.Append(E("ok", "cli", "Cadence (command line)", "Limits changed", code: "limits_changed"));
        log.Append(E("suspended", "cadence_suspend_agents", detail: "Agents suspended by an agent"));
        log.Append(E("resumed", "cadence_resume_agents", detail: "Agents resumed by an agent"));
        log.Append(E("error", "cadence_resume_agents", detail: "not verified", code: "not_verified"));
        log.Append(E("ok", "cadence_suspend_agents", detail: "Agents were already suspended", code: "unchanged"));
        log.Append(E("refused", "cadence_suspend_agents", detail: "rate limited", code: "rate_limited"));
        log.Append(E("ok", "cadence_save_job"));
        log.Append(E("connected", "session", detail: "protocol"));

        string[] owner = File.ReadAllLines(log.OwnerFilePath);
        string[] agent = File.ReadAllLines(log.FilePath);
        T.Eq("owner file: three owner lines and the three switch changes", 6, owner.Length);
        T.Eq("agent file: unchanged/refused switch calls, saves and sessions stay with the agents", 4, agent.Length);
        T.True("the file names are as documented", System.IO.Path.GetFileName(log.OwnerFilePath) == "audit-owner.jsonl" && System.IO.Path.GetFileName(log.FilePath) == "audit.jsonl");
        T.True("no agent tool call is in the owner file", owner.All(l => !l.Contains("cadence_save_job", StringComparison.Ordinal) && !l.Contains("\"tool\":\"session\"", StringComparison.Ordinal)));
        T.Eq("Tail shows all ten, merged", 10, log.Tail(100).Count);
    }

    public static void AFloodOfAgentLinesNeverRotatesOutAnOwnersEntry()
    {
        using var root = new Root();
        const int rotateAt = 4000;
        const int maxLine = 700;
        FileAuditLog log = root.Log(rotateAt);
        log.Append(E("suspended", "cli", "Cadence (command line)", "THE OLD OWNER ENTRY", session: "owner001", at: T0.AddHours(-1)));
        // 10,000 agent lines (the 31,500-request refusal flood of the red team is the next test): each carries a distinct job id and detail, so nothing folds and the agent files rotate over a hundred times.
        for (int i = 0; i < 10_000; i++)
            log.Append(new AuditEntry(T0.AddSeconds(i / 100), "sess0001", "Claude Code", null, "cadence_save_job", "j" + i.ToString("d7", CultureInfo.InvariantCulture), null, "ok", null, "Created " + i.ToString(CultureInfo.InvariantCulture)));

        T.True("the owner entry is still there", log.Tail(100_000).Any(e => e.Detail == "THE OLD OWNER ENTRY"));
        T.True("and in the owner file", File.ReadAllText(log.OwnerFilePath).Contains("THE OLD OWNER ENTRY", StringComparison.Ordinal));
        string[] files = Names(root);
        T.True("exactly audit.jsonl, audit.1..4.jsonl and the owner file (" + string.Join(",", files) + ")",
            files.SequenceEqual(["audit-owner.jsonl", "audit.1.jsonl", "audit.2.jsonl", "audit.3.jsonl", "audit.4.jsonl", "audit.jsonl"]));
        long total = Bytes(AgentsDir(root));
        T.True("the whole log stays bounded: five agent files of the limit plus one line each, and the small owner file (" + total + ")", total <= 5 * (rotateAt + maxLine) + 1000);
        foreach (string f in Directory.GetFiles(AgentsDir(root)).Where(f => !f.EndsWith("owner.jsonl", StringComparison.Ordinal)))
            T.True(System.IO.Path.GetFileName(f) + " is under the limit plus one line (" + new FileInfo(f).Length + ")", new FileInfo(f).Length < rotateAt + maxLine);
    }

    public static void AFloodOfMalformedRefusalsIsCountedNotLogged()
    {
        using var root = new Root();
        FileAuditLog log = root.Log();
        log.Append(E("suspended", "cli", "Cadence (command line)", "owner entry", session: "owner001", at: T0.AddMinutes(-5)));
        // 31,500 malformed requests in a few seconds (the clock moves 1 ms a call): all refused, 977 different texts.
        for (int i = 0; i < 31_500; i++)
        {
            root.Now = T0.AddMilliseconds(i);
            log.Append(E("refused", "cadence_save_job", detail: "arguments: bad field " + (i % 977).ToString(CultureInfo.InvariantCulture), code: "invalid_argument", at: root.Now));
        }
        long total = Bytes(AgentsDir(root));
        T.True("the log did not grow with the flood (" + total + " bytes)", total < 60 * 1024);
        IReadOnlyList<AuditEntry> all = log.Tail(100_000);
        T.True("the owner entry survives", all.Any(e => e.Detail == "owner entry"));
        T.True("a few dozen lines at most, not thousands (" + all.Count + ")", all.Count <= 1 + 2 * 61 + 4);
        int counted = all.Where(e => e.Tool == "cadence_save_job").Sum(e => e.Count);
        T.True("every refused call is accounted for in a count or a line (" + counted + ")", counted >= 31_500 - 60);
    }

    public static void IdenticalRefusalsCollapseIntoOneCountedLine()
    {
        using var root = new Root();
        FileAuditLog log = root.Log();
        for (int i = 0; i < 37; i++) log.Append(E("refused", detail: "program rejected", code: "program_rejected", at: T0.AddSeconds(i)));
        string[] lines = File.ReadAllLines(log.FilePath);
        T.Eq("one line", 1, lines.Length);
        AuditEntry e = log.Tail(10).Single();
        T.True("counted 37 times, with the time of the last one", e.Count == 37 && e.Outcome == "refused" && e.Code == "program_rejected" && e.At == T0.AddSeconds(36));
        using (JsonDocument d = JsonDocument.Parse(lines[0])) T.Eq("the line says count 37", 37, d.RootElement.GetProperty("count").GetInt32());

        log.Append(E("refused", detail: "something else", code: "invalid_argument"));
        log.Append(E("refused", detail: "program rejected", code: "program_rejected"));
        T.Eq("a different line in between breaks the run: three lines now", 3, File.ReadAllLines(log.FilePath).Length);
        log.Append(E("refused", detail: "program rejected", code: "program_rejected", session: "other002"));
        log.Append(E("refused", detail: "program rejected", code: "program_rejected", session: "other002"));
        IReadOnlyList<AuditEntry> tail = log.Tail(10);
        T.True("another session's refusals fold on their own", tail.Count == 4 && tail.Any(x => x.Count == 37) && tail.Any(x => x is { Session: "other002", Count: 2 }));

        // The same for identical "unchanged" calls, which are not refusals.
        for (int i = 0; i < 20; i++) log.Append(E("ok", "cadence_suspend_agents", detail: "Agents were already suspended", code: "unchanged"));
        T.True("twenty unchanged calls are one counted line", log.Tail(1)[0] is { Count: 20, Code: "unchanged" });
        // Ordinary ok lines never fold.
        log.Append(E("ok"));
        log.Append(E("ok"));
        T.Eq("two ok lines stay two", 2, log.Tail(2).Count(x => x.Count == 1 && x.Outcome == "ok"));
    }

    public static void RefusalsAreRateLimitedPerSessionAndMinute()
    {
        using var root = new Root();
        FileAuditLog log = root.Log();
        for (int i = 0; i < 100; i++) log.Append(E("refused", detail: "bad " + i.ToString(CultureInfo.InvariantCulture), code: "invalid_argument"));
        IReadOnlyList<AuditEntry> a = log.Tail(1000);
        T.Eq("60 refused lines and one suppressed line", 61, a.Count);
        T.True("the 61st is the suppressed line with the count 40", a[^1] is { Outcome: "refused", Code: "suppressed", Count: 40 } && a.Take(60).All(x => x.Code == "invalid_argument"));

        for (int i = 0; i < 100; i++) log.Append(E("refused", detail: "bad " + i.ToString(CultureInfo.InvariantCulture), code: "invalid_argument", session: "other002"));
        T.Eq("another session has its own budget of 60 (and its own suppressed line)", 61 + 61, log.Tail(1000).Count);
        T.Eq("session one's count is unchanged: still 40", 40, log.Tail(1000).Where(x => x.Session == "sess0001" && x.Code == "suppressed").Sum(x => x.Count));

        int before = log.Tail(1000).Count;
        for (int i = 0; i < 10; i++) log.Append(E("ok", detail: "ok " + i.ToString(CultureInfo.InvariantCulture)));
        T.Eq("ok lines are never limited", before + 10, log.Tail(1000).Count);

        root.Now = T0.AddMinutes(1);
        log.Append(E("refused", detail: "fresh minute", code: "invalid_argument"));
        T.True("a new minute: the refusal is logged again", log.Tail(1)[0].Detail == "fresh minute");
        T.Eq("and the old minute's count is kept", 40, log.Tail(1000).Where(x => x.Session == "sess0001" && x.Code == "suppressed").Sum(x => x.Count));
        for (int i = 0; i < 70; i++) log.Append(E("refused", detail: "minute two " + i.ToString(CultureInfo.InvariantCulture), code: "invalid_argument"));
        T.Eq("again 60 lines a minute: 59 more fit, 11 are counted into the last line", 11, log.Tail(1)[0].Count);
        T.Eq("the suppressed total of the session is 40 + 11", 51, log.Tail(1000).Where(x => x.Session == "sess0001" && x.Code == "suppressed").Sum(x => x.Count));
    }

    public static void TailReadsBothStreamsMergedInTimeOrder()
    {
        using var root = new Root();
        FileAuditLog log = root.Log(rotateAt: 600, ownerRotateAt: 600);
        // Interleaved: an even second is an owner line, an odd one an agent line; both streams rotate.
        for (int i = 0; i < 40; i++)
        {
            DateTimeOffset at = T0.AddSeconds(i);
            log.Append(i % 2 == 0 ? E("ok", "cli", "Cadence (command line)", "owner " + i.ToString(CultureInfo.InvariantCulture), session: "owner001", at: at)
                                  : E("ok", detail: "agent " + i.ToString(CultureInfo.InvariantCulture), at: at));
        }
        IReadOnlyList<AuditEntry> all = log.Tail(1000);
        T.True("oldest first, by time", all.Zip(all.Skip(1), (x, y) => x.At <= y.At).All(b => b));
        T.True("both streams are in it", all.Any(e => e.Tool == "cli") && all.Any(e => e.Tool == "cadence_save_job"));
        T.Eq("the newest entry is last", "agent 39", all[^1].Detail);

        IReadOnlyList<AuditEntry> five = log.TailMerged(5);
        T.True("TailMerged(5) is the newest five, oldest first", five.Count == 5 && five.Select(e => e.At).SequenceEqual(all.TakeLast(5).Select(e => e.At)));
        IReadOnlyList<AuditEntry> newest = log.TailMerged(5, newestFirst: true);
        T.True("newestFirst reverses it", newest.Select(e => e.At).SequenceEqual(five.Reverse().Select(e => e.At)) && newest[0].Detail == "agent 39");
        T.True("Tail(5) equals TailMerged(5)", log.Tail(5).Select(e => e.Detail).SequenceEqual(five.Select(e => e.Detail)));
        T.Eq("max below one is empty", 0, log.TailMerged(0).Count);
        using var empty = new Root();
        T.Eq("an empty log is empty", 0, empty.Log().TailMerged(10).Count);
        T.True("through the interface too", ((IAuditLog)log).TailMerged(3, true)[0].Detail == "agent 39");
        T.Eq("the default interface method works for another log", 0, ((IAuditLog)new MemoryAuditLog()).TailMerged(2).Count);
    }

    public static void TheOwnerFileIsBoundedToo()
    {
        using var root = new Root();
        FileAuditLog log = root.Log(ownerRotateAt: 1500);
        for (int i = 0; i < 400; i++) log.Append(E("ok", "cli", "Cadence (command line)", "owner change " + i.ToString(CultureInfo.InvariantCulture), session: "owner001"));
        T.True("two owner files at most (" + string.Join(",", Names(root)) + ")", Names(root).SequenceEqual(["audit-owner.1.jsonl", "audit-owner.jsonl"]));
        T.True("bounded by two limits plus a line each", Bytes(AgentsDir(root)) < 2 * (1500 + 700));
        T.Eq("the newest owner entry is last", "owner change 399", log.Tail(5)[^1].Detail);
    }
}
