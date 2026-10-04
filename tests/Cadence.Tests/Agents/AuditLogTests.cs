using System.Text;
using System.Text.Json;
using Cadence.Agent.Audit;
using Cadence.Core.Metadata;
using Cadence.TestHarness;

namespace Cadence.Tests;

/// <summary>
/// Phase 20 (PLAN 12.4.3, [CP-R3]): the append-only audit log in a TEMP root (never %LOCALAPPDATA%\Cadence): append, tail, rotation and its size cap,
/// concurrent appends, JSON-escaped and capped fields (log injection), a log file someone edited by hand, and failures that must never stop a tool.
/// </summary>
public static class AuditLogTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 1, 14, 2, 11, TimeSpan.FromHours(-4));

    sealed class Root : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cadence-audit-" + Guid.NewGuid().ToString("N"));

        public FileAuditLog Log(long rotateAt = FileAuditLog.DefaultRotateAtBytes) => new("CadenceTest", rotateAt, Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* temp: leave it */ }
        }
    }

    static AuditEntry E(string outcome = "ok", string tool = "cadence_save_job", string client = "Claude Code", string detail = "Created \\CadenceTest\\Backup notes", string? jobId = "x7k2m9qa",
        string? job = "Backup notes", string? code = null, string? version = "2.1.286", string session = "a1b2c3d4", DateTimeOffset? at = null) =>
        new(at ?? T0, session, client, version, tool, jobId, job, outcome, code, detail);

    static string[] FileLines(string path) =>
        File.ReadAllText(path, new UTF8Encoding(false)).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    public static void AppendAndTail()
    {
        using var root = new Root();
        FileAuditLog log = root.Log();
        T.True("constructing creates nothing", !Directory.Exists(root.Path));
        T.Eq("tail of a missing log is empty", 0, log.Tail(10).Count);
        T.True("reading creates nothing", !Directory.Exists(root.Path));
        T.Eq("the file path is <root>\\agents\\audit.jsonl", System.IO.Path.Combine(root.Path, "agents", "audit.jsonl"), log.FilePath);

        log.Append(E(outcome: "connected", tool: "session", jobId: null, job: null, detail: "protocol 2025-11-25"));
        log.Append(E(outcome: "refused", code: "stale_revision", detail: "The job changed"));
        log.Append(E(outcome: "ok"));
        T.True("the directory exists after the first append", Directory.Exists(System.IO.Path.Combine(root.Path, "agents")));

        byte[] raw = File.ReadAllBytes(log.FilePath);
        T.True("UTF-8 without a BOM", raw.Length > 3 && !(raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF));
        string[] lines = FileLines(log.FilePath);
        T.Eq("three lines", 3, lines.Length);
        T.True("each line ends with a bare \\n (no CR)", !Encoding.UTF8.GetString(raw).Contains('\r', StringComparison.Ordinal) && raw[^1] == (byte)'\n');
        foreach (string l in lines)
        {
            using JsonDocument d = JsonDocument.Parse(l);
            T.True("a JSON object with v:1", d.RootElement.GetProperty("v").GetInt32() == 1 && d.RootElement.ValueKind == JsonValueKind.Object);
        }

        using (JsonDocument d = JsonDocument.Parse(lines[1]))
        {
            JsonElement r = d.RootElement;
            T.Eq("at", "2026-10-01T14:02:11-04:00", r.GetProperty("at").GetString());
            T.Eq("session", "a1b2c3d4", r.GetProperty("session").GetString());
            T.Eq("client", "Claude Code", r.GetProperty("client").GetString());
            T.Eq("clientVersion", "2.1.286", r.GetProperty("clientVersion").GetString());
            T.Eq("tool", "cadence_save_job", r.GetProperty("tool").GetString());
            T.Eq("jobId", "x7k2m9qa", r.GetProperty("jobId").GetString());
            T.Eq("job", "Backup notes", r.GetProperty("job").GetString());
            T.Eq("outcome", "refused", r.GetProperty("outcome").GetString());
            T.Eq("code", "stale_revision", r.GetProperty("code").GetString());
            T.Eq("detail", "The job changed", r.GetProperty("detail").GetString());
        }
        using (JsonDocument d = JsonDocument.Parse(lines[0]))
            T.True("null fields are omitted", !d.RootElement.TryGetProperty("jobId", out _) && !d.RootElement.TryGetProperty("job", out _) && !d.RootElement.TryGetProperty("code", out _));

        IReadOnlyList<AuditEntry> tail = log.Tail(2);
        T.Eq("tail(2) returns two", 2, tail.Count);
        T.True("oldest first, the newest last", tail[0].Outcome == "refused" && tail[1].Outcome == "ok");
        T.True("round trip of the fields", tail[0] == E(outcome: "refused", code: "stale_revision", detail: "The job changed"));
        T.Eq("tail(100) returns all three", 3, log.Tail(100).Count);
        T.Eq("tail(0) is empty", 0, log.Tail(0).Count);
        T.True("a log with a root override uses its own mutex, the product one is per folder", log.MutexName.StartsWith("Local\\CadenceAudit.CadenceTest.", StringComparison.Ordinal) && new FileAuditLog("CadenceTest").MutexName == "Local\\CadenceAudit.CadenceTest" && new FileAuditLog("Cadence").MutexName == "Local\\CadenceAudit.Cadence");
    }

    public static void TheFolderRuleApplies()
    {
        foreach (string bad in new[] { "Microsoft", "cadence", "", "Cadence\\x", "..\\Cadence" })
            T.Throws<ArgumentException>("refuses '" + bad + "'", () => _ = new FileAuditLog(bad));
        T.Throws<ArgumentOutOfRangeException>("rotation size must be positive", () => _ = new FileAuditLog("CadenceTest", 0));
        string real = new FileAuditLog("CadenceTest").FilePath;
        T.True("the default path is under %LOCALAPPDATA%\\CadenceTest\\agents", real.EndsWith("\\CadenceTest\\agents\\audit.jsonl", StringComparison.OrdinalIgnoreCase));
        T.True("and the product's is under %LOCALAPPDATA%\\Cadence\\agents (nothing is created by looking)", new FileAuditLog("Cadence").FilePath.EndsWith("\\Cadence\\agents\\audit.jsonl", StringComparison.OrdinalIgnoreCase));
    }

    public static void LogInjectionCannotStartASecondLine()
    {
        using var root = new Root();
        FileAuditLog log = root.Log();
        string nasty = "x\n{\"v\":1,\"outcome\":\"resumed\"}";
        log.Append(E(client: nasty));
        string[] lines = FileLines(log.FilePath);
        T.Eq("exactly one line", 1, lines.Length);
        IReadOnlyList<AuditEntry> tail = log.Tail(1);
        T.Eq("tail(1) returns the cleaned name", Actor.SanitizeName(nasty), tail[0].Client);
        T.Eq("it is the one entry, not a forged 'resumed'", "ok", tail[0].Outcome);
        T.Eq("the whole log holds one entry", 1, log.Tail(100).Count);

        string hostile = "a\r\nb\"c\\d" + (char)0x2028 + "e" + (char)0x2029 + "f" + (char)0x85 + "g\0h\t\u0007{\"v\":1,\"outcome\":\"resumed\"}\n{\"v\":1}";
        log.Append(new AuditEntry(T0, hostile, hostile, hostile, hostile, hostile, hostile, hostile, hostile, hostile));
        string text = File.ReadAllText(log.FilePath, new UTF8Encoding(false));
        T.Eq("still one line per entry (2)", 2, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        T.True("no raw U+2028, U+2029 or NEL in the file", !text.Contains((char)0x2028, StringComparison.Ordinal) && !text.Contains((char)0x2029, StringComparison.Ordinal) && !text.Contains((char)0x85, StringComparison.Ordinal));
        T.True("no NUL or other control character except the line ends", text.All(c => c == '\n' || !char.IsControl(c)));
        T.Eq("every field of the last entry parses back, cleaned", 2, log.Tail(5).Count);
        AuditEntry last = log.Tail(1)[0];
        T.True("a client name cannot carry a control character", !last.Client.Any(char.IsControl));
        T.True("a detail cannot carry a control character", !last.Detail.Any(char.IsControl));
        T.True("outcome is cut to its own cap", last.Outcome.Length <= AuditText.MaxOutcome);
        foreach (string l in FileLines(log.FilePath)) { using JsonDocument d = JsonDocument.Parse(l); T.True("each line is one JSON object", d.RootElement.ValueKind == JsonValueKind.Object); }
    }

    public static void FieldsAreCapped()
    {
        using var root = new Root();
        FileAuditLog log = root.Log();
        string big = new('a', 1000);
        log.Append(new AuditEntry(T0, big, big, big, big, big, big, "ok", big, big));
        AuditEntry e = log.Tail(1)[0];
        T.Eq("client 64", 64, e.Client.Length);
        T.Eq("clientVersion 32", 32, e.ClientVersion!.Length);
        T.Eq("job name 100", 100, e.JobName!.Length);
        T.Eq("tool 64", 64, e.Tool.Length);
        T.Eq("code 40", 40, e.Code!.Length);
        T.Eq("detail 300", 300, e.Detail.Length);
        T.True("session and job id are short too", e.Session.Length <= 16 && e.JobId!.Length <= 16);

        // The cap never splits a surrogate pair.
        string emoji = string.Concat(Enumerable.Repeat("\U0001F600", 200));
        log.Append(new AuditEntry(T0, "s", "c", null, "t", null, emoji, "ok", null, emoji));
        AuditEntry f = log.Tail(1)[0];
        T.True("detail: no lone surrogate at the cut", f.Detail.Length is <= 300 and >= 298 && IsWellFormed(f.Detail));
        T.True("job name: well formed", IsWellFormed(f.JobName!) && f.JobName!.Length <= 100);

        // Blank and null fields get sane values; the client is never empty.
        log.Append(new AuditEntry(T0, "", "   ", "  ", "", "", "", "", "", ""));
        AuditEntry g = log.Tail(1)[0];
        T.Eq("an empty client reads as unknown agent", "unknown agent", g.Client);
        T.Eq("an empty tool reads as unknown", "unknown", g.Tool);
        T.Eq("an empty outcome reads as error", "error", g.Outcome);
        T.True("blank optional fields are dropped", g.ClientVersion is null && g.JobId is null && g.JobName is null && g.Code is null);
        T.Eq("an empty detail stays empty", "", g.Detail);
    }

    static bool IsWellFormed(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i])) { if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1])) return false; i++; }
            else if (char.IsLowSurrogate(s[i])) return false;
        }
        return true;
    }

    public static void RotationKeepsOneOldFileAndCapsTheSize()
    {
        using var root = new Root();
        FileAuditLog log = root.Log(rotateAt: 1500);
        int n = 60;
        for (int i = 0; i < n; i++) log.Append(E(detail: "entry number " + i.ToString(System.Globalization.CultureInfo.InvariantCulture), session: "s" + i.ToString("d7", System.Globalization.CultureInfo.InvariantCulture)));

        string dir = System.IO.Path.Combine(root.Path, "agents");
        string[] files = [.. Directory.GetFiles(dir).Select(System.IO.Path.GetFileName).Order()!];
        T.True("the current file and up to four rotated ones (" + string.Join(",", files) + ")", files.Length is >= 2 and <= 5 && files.Contains("audit.1.jsonl") && files.Contains("audit.jsonl") && files.All(f => f is "audit.jsonl" or "audit.1.jsonl" or "audit.2.jsonl" or "audit.3.jsonl" or "audit.4.jsonl"));
        long current = new FileInfo(log.FilePath).Length;
        long old = new FileInfo(log.RotatedFilePath).Length;
        int maxLine = Encoding.UTF8.GetByteCount(new string('x', 400));
        T.True("the current file stays under the limit plus one entry (" + current + ")", current < 1500 + maxLine);
        T.True("the old file is about the limit (" + old + ")", old >= 1500 && old < 1500 + maxLine);
        T.True("old entries were dropped by later rotations (the whole log is smaller than all of it)", log.Tail(1000).Count < n);

        IReadOnlyList<AuditEntry> all = log.Tail(1000);
        T.True("the newest entry is last", all[^1].Session == "s0000059");
        T.True("entries are consecutive across both files (oldest first, no gaps)", all.Zip(all.Skip(1), (a, b) => int.Parse(b.Session[1..], System.Globalization.CultureInfo.InvariantCulture) - int.Parse(a.Session[1..], System.Globalization.CultureInfo.InvariantCulture) == 1).All(x => x));
        IReadOnlyList<AuditEntry> few = log.Tail(3);
        T.True("tail(3) is the last three", few.Select(e => e.Session).SequenceEqual(["s0000057", "s0000058", "s0000059"]));
        int inCurrent = FileLines(log.FilePath).Length;
        IReadOnlyList<AuditEntry> across = log.Tail(inCurrent + 2);
        T.Eq("a tail longer than the current file reaches into the old one", inCurrent + 2, across.Count);

        // A much longer run still never holds more than about two files.
        for (int i = 0; i < 2000; i++) log.Append(E(detail: new string('d', 250)));
        long total = Directory.GetFiles(dir).Sum(f => new FileInfo(f).Length);
        T.True("the total stays about five files of the limit (" + total + ")", total < 5 * (1500 + 900));
        T.True("at most five files (current plus audit.1..4)", Directory.GetFiles(dir).Length <= 5);
    }

    public static void ConcurrentAppendsProduceCompleteLines()
    {
        using var root = new Root();
        FileAuditLog log = root.Log();
        const int threads = 8, each = 40;
        var failures = new List<string>();
        var workers = Enumerable.Range(0, threads).Select(t => new Thread(() =>
        {
            try
            {
                for (int i = 0; i < each; i++)
                    log.Append(E(client: "client " + t, detail: "t" + t + " n" + i + " " + new string('z', 150), session: "t" + t + "n" + i));
            }
            catch (Exception ex) { lock (failures) failures.Add(ex.Message); }
        })).ToList();
        foreach (Thread w in workers) w.Start();
        foreach (Thread w in workers) w.Join();

        T.Eq("no append failed", 0, failures.Count);
        string[] lines = FileLines(log.FilePath);
        T.Eq("every append is one complete line", threads * each, lines.Length);
        var sessions = new HashSet<string>();
        foreach (string l in lines)
        {
            using JsonDocument d = JsonDocument.Parse(l);
            sessions.Add(d.RootElement.GetProperty("session").GetString()!);
        }
        T.Eq("every entry is there once", threads * each, sessions.Count);

        // Same with rotation going on: no entry may be torn.
        using var root2 = new Root();
        FileAuditLog small = root2.Log(rotateAt: 3000);
        var workers2 = Enumerable.Range(0, threads).Select(t => new Thread(() => { for (int i = 0; i < each; i++) small.Append(E(detail: new string('q', 100), session: "r" + t + "n" + i)); })).ToList();
        foreach (Thread w in workers2) w.Start();
        foreach (Thread w in workers2) w.Join();
        int ok = 0;
        foreach (string f in new[] { small.FilePath, small.RotatedFilePath })
            foreach (string l in FileLines(f)) { using JsonDocument d = JsonDocument.Parse(l); ok++; }
        T.True("with rotation every surviving line is a whole entry (" + ok + ")", ok > 0 && ok <= threads * each);
    }

    public static void AFileEditedByHandIsReadSafely()
    {
        using var root = new Root();
        FileAuditLog log = root.Log();
        log.Append(E(detail: "first"));
        string good = Encoding.UTF8.GetString(FileAuditLog.Serialize(AuditText.Clean(E(detail: "good one"))));
        string forged = "{\"v\":1,\"at\":\"2026-10-01T14:02:11-04:00\",\"session\":\"s\",\"client\":\"evil\\nname\\u202eX\",\"tool\":\"t\",\"outcome\":\"" + new string('o', 500) + "\",\"detail\":\"line1\\nline2\\u0007\"}\n";
        File.AppendAllText(log.FilePath, "this is not json\n" + "{\"v\":2,\"at\":\"2026-10-01T14:02:11-04:00\"}\n" + "[1,2,3]\n" + "{\"v\":1,\"at\":\"nonsense\"}\n" + "{\"v\":1}\n" + forged + good, new UTF8Encoding(false));
        IReadOnlyList<AuditEntry> tail = log.Tail(50);
        T.Eq("garbage, foreign versions and entries without a date are skipped", 3, tail.Count);
        T.True("the forged line is cleaned on the way in", !tail[1].Client.Any(char.IsControl) && !tail[1].Client.Contains((char)0x202e, StringComparison.Ordinal) && tail[1].Outcome.Length <= AuditText.MaxOutcome && !tail[1].Detail.Any(char.IsControl));
        T.Eq("the last good line is last", "good one", tail[^1].Detail);

        // Escape: what the CLI prints for a control character is visible text, not an action.
        T.Eq("Escape shows line breaks and tabs", "a\\nb\\r\\tc", AuditText.Escape("a\nb\r\tc"));
        T.Eq("Escape shows other control and invisible characters as \\uXXXX", "x\\u0007y\\u2028z\\u202ew", AuditText.Escape("x\u0007y" + (char)0x2028 + "z" + (char)0x202e + "w"));
        T.Eq("Escape of null is empty", "", AuditText.Escape(null));

        // A file bigger than the read limit is read from its end only.
        using var root2 = new Root();
        FileAuditLog big = root2.Log(rotateAt: long.MaxValue);
        big.Append(E(detail: "tiny"));
        using (var fs = new FileStream(big.FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            byte[] filler = Encoding.UTF8.GetBytes(new string('f', 1 << 20) + "\n");
            for (int i = 0; i < 9; i++) fs.Write(filler, 0, filler.Length);   // 9 MiB of one-megabyte lines that are not entries
        }
        big.Append(E(detail: "the newest"));
        IReadOnlyList<AuditEntry> bt = big.Tail(5);
        T.Eq("the newest entry is still found in an oversized file", "the newest", bt[^1].Detail);
    }

    public static void AnAuditFailureNeverFailsATool()
    {
        using var root = new Root();
        Directory.CreateDirectory(root.Path);
        File.WriteAllText(System.IO.Path.Combine(root.Path, "agents"), "a file where the folder should be");
        FileAuditLog log = root.Log();
        T.Throws<IOException>("Append reports the problem", () => log.Append(E()));
        string? warning = log.TryAppend(E());
        T.True("TryAppend returns the warning text instead (" + warning + ")", warning is not null && warning.StartsWith("The agent log could not be written: ", StringComparison.Ordinal));
        T.True("TryAppend does not throw for any file error", true);

        var mem = new MemoryAuditLog { ThrowOnAppend = new UnauthorizedAccessException("denied") };
        string? w2 = mem.TryAppend(E());
        T.True("an access error is a warning too, naming the error type", w2 is not null && w2.Contains("UnauthorizedAccessException", StringComparison.Ordinal));
        T.True("[RT-2] and not the error text (it names the log file, a path in the owner's profile)", w2 is not null && !w2.Contains("denied", StringComparison.Ordinal));
        string? w3 = log.TryAppend(E());
        T.True("[RT-2] the real file error does not show the path of the log", w3 is not null && !w3.Contains(root.Path, StringComparison.OrdinalIgnoreCase) && !w3.Contains("agents", StringComparison.Ordinal), w3);
        mem.ThrowOnAppend = null;
        T.True("success returns null", mem.TryAppend(E()) is null && mem.All.Count == 1);
        T.Eq("the null log does nothing", 0, NullAuditLog.Instance.Tail(5).Count);
        NullAuditLog.Instance.Append(E());
    }

    public static void NoEnvironmentValuesAreEverWritten()
    {
        // The entry type has no field for them (Q11), and a value that leaks into free text would be the caller's bug: assert the shape.
        string[] props = [.. typeof(AuditEntry).GetProperties().Select(p => p.Name)];
        T.True("AuditEntry has exactly the documented fields", props.Order().SequenceEqual(new[] { "At", "Client", "ClientVersion", "Code", "Count", "Detail", "JobId", "JobName", "Outcome", "Session", "Tool" }.Order()));
        T.True("no field is about the environment or arguments", props.All(p => !p.Contains("Env", StringComparison.OrdinalIgnoreCase) && !p.Contains("Arg", StringComparison.OrdinalIgnoreCase)));
        using var root = new Root();
        FileAuditLog log = root.Log();
        const string secret = "s3cr3t-v4lue-9f2c";
        Environment.SetEnvironmentVariable("CADENCE_AUDIT_TEST_SECRET", secret);
        try
        {
            log.Append(E(detail: "Saved job with 1 environment variable(s): API_KEY"));
            T.True("the log carries names only", !File.ReadAllText(log.FilePath).Contains(secret, StringComparison.Ordinal));
        }
        finally { Environment.SetEnvironmentVariable("CADENCE_AUDIT_TEST_SECRET", null); }
    }
}
