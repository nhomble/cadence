using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Cadence.Scheduler;

namespace Cadence.Agent.Audit;

/// <summary>
/// The append-only audit log (PLAN 12.4.3, [CP-R3], [CP-D] D4): two streams under <c>%LOCALAPPDATA%\&lt;Folder&gt;\agents</c>, UTF-8 without BOM, one JSON object per line.
/// <list type="bullet">
/// <item>Lines are written with <see cref="Utf8JsonWriter"/> only (never string concatenation) and its default encoder, which escapes quotes, backslashes,
/// control characters, U+2028/U+2029 and every non-ASCII character, so a field can never start a second line; every string is also cleaned and capped first (<see cref="AuditText"/>).</item>
/// <item>Appends and rotation run under the named mutex <c>Local\CadenceAudit.&lt;Folder&gt;</c>; the file is opened with <c>FileMode.Append</c> and
/// <c>FileShare.ReadWrite | Delete</c>. The directory is created on the first append only (under <c>agents</c>, never anywhere else).</item>
/// <item>The AGENT stream is <c>audit.jsonl</c> plus <c>audit.1..4.jsonl</c> (about 5 MiB: a file of <c>rotateAtBytes</c> (1 MiB) or more is shifted down before an append).</item>
/// <item>The OWNER stream is <c>audit-owner.jsonl</c> plus <c>audit-owner.1.jsonl</c>: lines of the CLI and the app, and the changes of the agent switch (also those made by the
/// two switch tools). Agent lines never go there, so an agent that floods the log cannot rotate an owner's entry out.</item>
/// <item>Repeated identical refusals (and identical "unchanged" calls) in a row collapse into one line whose <see cref="AuditEntry.Count"/> grows ("refused x37"). Per session at most
/// <see cref="RefusalsPerMinute"/> refused lines are written a minute; the rest are counted into one <c>refused</c>/<c>suppressed</c> line (the clock is injectable).</item>
/// </list>
/// </summary>
public sealed class FileAuditLog : IAuditLog
{
    public const string FileName = "audit.jsonl";
    public const string RotatedFileName = "audit.1.jsonl";
    public const string OwnerFileName = "audit-owner.jsonl";
    public const string OwnerRotatedFileName = "audit-owner.1.jsonl";
    public const int Version = 1;
    public const long DefaultRotateAtBytes = 1 << 20;

    /// <summary>Rotated agent files kept (audit.1 .. audit.4).</summary>
    public const int AgentRotatedFiles = 4;

    /// <summary>Rotated owner files kept (audit-owner.1).</summary>
    public const int OwnerRotatedFiles = 1;

    /// <summary>Refused lines one session may write in one minute before the rest are counted into one line.</summary>
    public const int RefusalsPerMinute = 60;

    /// <summary>The code of the one line that counts refused calls that were not logged one by one.</summary>
    public const string SuppressedCode = "suppressed";

    /// <summary>Tail never reads more than this from one file (a hand-made huge file must not exhaust memory).</summary>
    const long MaxReadBytes = 8L << 20;

    const int MaxSessions = 256;
    const string SuspendTool = "cadence_suspend_agents";
    const string ResumeTool = "cadence_resume_agents";
    static readonly TimeSpan MutexWait = TimeSpan.FromSeconds(5);
    static readonly TimeSpan SuppressedNewLineGap = TimeSpan.FromSeconds(5);
    static readonly TimeSpan RewriteGap = TimeSpan.FromMilliseconds(100);
    static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>Where the last line this instance wrote sits in its file (so an identical next line can be folded into it).</summary>
    sealed record LineRef(string Key, long Start, long End, byte[] Bytes, AuditEntry Entry);

    sealed class Lane(string dir, string baseName, int rotated, long rotateAt)
    {
        public string Current { get; } = Path.Combine(dir, baseName + ".jsonl");
        public int Rotated { get; } = rotated;
        public long RotateAt { get; } = rotateAt;
        public LineRef? Last { get; set; }

        public string Old(int i) => Path.Combine(dir, baseName + "." + i.ToString(CultureInfo.InvariantCulture) + ".jsonl");
    }

    sealed class SessionState
    {
        public long Minute = long.MinValue;
        public int Logged;
        public int Pending;
        public LineRef? Suppressed;
        public DateTimeOffset SuppressedAt;
        public AuditEntry? Sample;
        public Lane? Lane;
    }

    readonly string _dir;
    readonly string _mutexName;
    readonly Func<DateTimeOffset> _clock;
    readonly Lane _agent;
    readonly Lane _owner;
    readonly Dictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);

    /// <param name="folderName">Exactly <c>Cadence</c> or <c>CadenceTest</c> (<see cref="ManagedFolder"/>).</param>
    /// <param name="rotateAtBytes">The size at which an agent file is rotated (tests use a small value).</param>
    /// <param name="rootDirectory">Tests: a directory that replaces <c>%LOCALAPPDATA%\&lt;Folder&gt;</c>; the log lives in <c>&lt;root&gt;\agents</c>.</param>
    /// <param name="clock">The clock of the per-minute refusal limit (default: the system clock).</param>
    /// <param name="ownerRotateAtBytes">The size at which an owner file is rotated (default: <paramref name="rotateAtBytes"/>).</param>
    public FileAuditLog(string folderName, long rotateAtBytes = DefaultRotateAtBytes, string? rootDirectory = null, Func<DateTimeOffset>? clock = null, long? ownerRotateAtBytes = null)
    {
        if (!ManagedFolder.IsCanonical(folderName)) throw new ArgumentException("The folder must be exactly Cadence or CadenceTest.", nameof(folderName));
        if (rotateAtBytes < 1) throw new ArgumentOutOfRangeException(nameof(rotateAtBytes));
        if (ownerRotateAtBytes is < 1) throw new ArgumentOutOfRangeException(nameof(ownerRotateAtBytes));
        string root = rootDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), folderName);
        _dir = Path.Combine(root, "agents");
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _agent = new Lane(_dir, "audit", AgentRotatedFiles, rotateAtBytes);
        _owner = new Lane(_dir, "audit-owner", OwnerRotatedFiles, ownerRotateAtBytes ?? rotateAtBytes);
        // A test root gets its own mutex so two tests in one process never wait on each other.
        _mutexName = "Local\\CadenceAudit." + folderName + (rootDirectory is null ? "" : "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Utf8.GetBytes(Path.GetFullPath(root).ToUpperInvariant())))[..12]);
    }

    /// <summary>The agents' current file (<c>audit.jsonl</c>); the rotated ones are <see cref="RotatedFilePath"/> and <see cref="RotatedFilePaths"/>.</summary>
    public string FilePath => _agent.Current;

    /// <summary>The owner's current file (<c>audit-owner.jsonl</c>): CLI, app and switch changes, never rotated out by agent lines.</summary>
    public string OwnerFilePath => _owner.Current;

    /// <summary>The named mutex that serializes appends and rotation: <c>Local\CadenceAudit.&lt;Folder&gt;</c> (a test root adds a suffix).</summary>
    public string MutexName => _mutexName;

    /// <summary>The first rotated agent file (<c>audit.1.jsonl</c>).</summary>
    public string RotatedFilePath => _agent.Old(1);

    /// <summary>All rotated files of both streams (audit.1..4.jsonl, audit-owner.1.jsonl); they may not exist.</summary>
    public IReadOnlyList<string> RotatedFilePaths => [.. Enumerable.Range(1, AgentRotatedFiles).Select(_agent.Old), .. Enumerable.Range(1, OwnerRotatedFiles).Select(_owner.Old)];

    /// <summary>The owner stream takes the CLI's and the app's lines and the changes of the switch (also by the switch tools); everything else an agent does is the agent stream.</summary>
    public static bool IsOwnerEvent(AuditEntry e) =>
        e.Tool is "cli" or "app"
        || (e.Tool is SuspendTool or ResumeTool && e.Outcome is "suspended" or "resumed" or "error");

    public void Append(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        AuditEntry clean = AuditText.Clean(entry);
        Lane lane = IsOwnerEvent(clean) ? _owner : _agent;
        using var mutex = new Mutex(false, _mutexName);
        bool held;
        try { held = mutex.WaitOne(MutexWait); }
        catch (AbandonedMutexException) { held = true; }   // the previous holder died: the file is still a valid append-only log
        if (!held) throw new IOException("the agent log is busy");
        try
        {
            Directory.CreateDirectory(_dir);
            AppendLocked(lane, clean);
        }
        finally { mutex.ReleaseMutex(); }
    }

    void AppendLocked(Lane lane, AuditEntry e)
    {
        bool refusal = e.Outcome == "refused" && e.Code != SuppressedCode;
        if ((refusal || e.Code == "unchanged") && TryFold(lane, e)) return;

        // Before another line of a session whose refusals are being counted, the count is made visible (the counted line stays before it in time order).
        if (!refusal && _sessions.TryGetValue(e.Session, out SessionState? own) && own.Pending > 0 && own.Lane is { } ownLane) FlushPending(ownLane, own, _clock(), force: true);

        if (refusal)
        {
            DateTimeOffset now = _clock();
            SessionState st = SessionOf(e.Session, now, lane);
            if (st.Logged >= RefusalsPerMinute)
            {
                Suppress(lane, st, e, now);
                return;
            }
            st.Logged++;
        }
        WriteNew(lane, e);
    }

    SessionState SessionOf(string session, DateTimeOffset now, Lane lane)
    {
        long minute = now.UtcTicks / TimeSpan.TicksPerMinute;
        if (!_sessions.TryGetValue(session, out SessionState? st))
        {
            if (_sessions.Count >= MaxSessions)
                foreach (string k in _sessions.Where(p => p.Value.Minute < minute - 1).Select(p => p.Key).ToList()) _sessions.Remove(k);
            _sessions[session] = st = new SessionState();
        }
        if (st.Minute != minute)
        {
            // A new minute: what the old one still had pending is written once, then the budget starts again.
            if (st.Pending > 0 && st.Sample is not null) WriteSuppressed(lane, st, st.Sample, now, st.Pending);
            st.Minute = minute;
            st.Logged = 0;
            st.Pending = 0;
            st.Suppressed = null;
        }
        return st;
    }

    void Suppress(Lane lane, SessionState st, AuditEntry e, DateTimeOffset now)
    {
        st.Pending++;
        st.Sample = e;
        st.Lane = lane;
        if (st.Suppressed is null)
        {
            WriteSuppressed(lane, st, e, now, st.Pending);
            st.Pending = 0;
        }
        else if (now - st.SuppressedAt >= RewriteGap)
        {
            // At most about ten rewrites a second however fast the flood is; what is still pending is written by the next rewrite, at the end of the minute, before another
            // line of the session, by Flush and by Tail.
            FlushPending(lane, st, now, force: false);
        }
    }

    /// <summary>Writes the count of refused calls that is still only in memory: into the suppressed line while it is the last line of its file, else (not before 5 s, or when forced) as a new line.</summary>
    void FlushPending(Lane lane, SessionState st, DateTimeOffset now, bool force)
    {
        if (st.Pending == 0 || st.Sample is null) return;
        if (st.Suppressed is { } line && ReferenceEquals(lane.Last, line) && Verify(lane, line))
        {
            Rewrite(lane, line, line.Entry with { At = st.Sample.At, Count = line.Entry.Count + st.Pending });
            st.SuppressedAt = now;
            st.Pending = 0;
        }
        else if (force || st.Suppressed is null || now - st.SuppressedAt >= SuppressedNewLineGap)
        {
            WriteSuppressed(lane, st, st.Sample, now, st.Pending);
            st.Pending = 0;
        }
        // else: counted in memory (two interleaving sessions must not make a line per call)
    }

    /// <summary>
    /// Writes every refusal count that is still pending in this process. Called before another line of the same session is written, by <see cref="TailMerged"/>, and by hosts that are
    /// about to exit; a reader in another process sees counts that are at most a fraction of a second old.
    /// </summary>
    public void Flush()
    {
        if (_sessions.Count == 0) return;
        using var mutex = new Mutex(false, _mutexName);
        bool held;
        try { held = mutex.WaitOne(MutexWait); }
        catch (AbandonedMutexException) { held = true; }
        if (!held) return;
        try
        {
            DateTimeOffset now = _clock();
            foreach (SessionState st in _sessions.Values.ToList())
                if (st.Pending > 0 && st.Lane is { } lane) FlushPending(lane, st, now, force: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the counts stay pending.
        }
        finally { mutex.ReleaseMutex(); }
    }

    void WriteSuppressed(Lane lane, SessionState st, AuditEntry sample, DateTimeOffset now, int count)
    {
        var line = new AuditEntry(sample.At, sample.Session, sample.Client, sample.ClientVersion, sample.Tool, null, null, "refused", SuppressedCode,
            "More refused calls in this minute were not logged one by one", count);
        st.Suppressed = WriteNew(lane, AuditText.Clean(line));
        st.SuppressedAt = now;
    }

    static string KeyOf(AuditEntry e) =>
        string.Join('\u001f', e.Session, e.Client, e.ClientVersion, e.Tool, e.JobId, e.JobName, e.Outcome, e.Code, e.Detail);

    /// <summary>Folds <paramref name="e"/> into the last line this instance wrote when that line is identical and still the last line of the file.</summary>
    bool TryFold(Lane lane, AuditEntry e)
    {
        if (lane.Last is not { } last || last.Key != KeyOf(e) || !Verify(lane, last)) return false;
        Rewrite(lane, last, last.Entry with { At = e.At, Count = last.Entry.Count + Math.Max(1, e.Count) });
        return true;
    }

    /// <summary>True when the last line is still exactly where and what it was (nobody appended, rotated or edited since).</summary>
    static bool Verify(Lane lane, LineRef last)
    {
        try
        {
            using var fs = new FileStream(lane.Current, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length != last.End) return false;
            fs.Seek(last.Start, SeekOrigin.Begin);
            byte[] now = new byte[last.Bytes.Length];
            int read = 0;
            while (read < now.Length)
            {
                int n = fs.Read(now, read, now.Length - read);
                if (n <= 0) return false;
                read += n;
            }
            return now.AsSpan().SequenceEqual(last.Bytes);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    void Rewrite(Lane lane, LineRef last, AuditEntry updated)
    {
        byte[] bytes = Serialize(updated);
        using (var fs = new FileStream(lane.Current, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            fs.Seek(last.Start, SeekOrigin.Begin);
            fs.Write(bytes, 0, bytes.Length);
            fs.SetLength(last.Start + bytes.Length);
            fs.Flush();
        }
        LineRef next = last with { End = last.Start + bytes.Length, Bytes = bytes, Entry = updated };
        lane.Last = next;
        // A suppressed-line reference held by a session must follow the new object.
        foreach (SessionState s in _sessions.Values)
            if (ReferenceEquals(s.Suppressed, last)) s.Suppressed = next;
    }

    LineRef WriteNew(Lane lane, AuditEntry e)
    {
        byte[] line = Serialize(e);
        var info = new FileInfo(lane.Current);
        if (info.Exists && info.Length >= lane.RotateAt) Rotate(lane);
        using var fs = new FileStream(lane.Current, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        long start = fs.Length;
        fs.Write(line, 0, line.Length);
        fs.Flush();
        var written = new LineRef(KeyOf(e), start, start + line.Length, line, e);
        lane.Last = written;
        return written;
    }

    static void Rotate(Lane lane)
    {
        for (int i = lane.Rotated; i >= 2; i--)
            if (File.Exists(lane.Old(i - 1))) File.Move(lane.Old(i - 1), lane.Old(i), overwrite: true);
        File.Move(lane.Current, lane.Old(1), overwrite: true);
        lane.Last = null;
    }

    /// <summary>The line (with its "\n") for an entry that is already clean. Public so the CLI's --json prints the very same shape.</summary>
    public static byte[] Serialize(AuditEntry e)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = JavaScriptEncoder.Default, Indented = false }))
        {
            w.WriteStartObject();
            w.WriteNumber("v", Version);
            w.WriteString("at", e.At.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture));
            w.WriteString("session", e.Session);
            w.WriteString("client", e.Client);
            if (e.ClientVersion is not null) w.WriteString("clientVersion", e.ClientVersion);
            w.WriteString("tool", e.Tool);
            if (e.JobId is not null) w.WriteString("jobId", e.JobId);
            if (e.JobName is not null) w.WriteString("job", e.JobName);
            w.WriteString("outcome", e.Outcome);
            if (e.Code is not null) w.WriteString("code", e.Code);
            w.WriteString("detail", e.Detail);
            if (e.Count > 1) w.WriteNumber("count", e.Count);
            w.WriteEndObject();
        }
        ms.WriteByte((byte)'\n');
        return ms.ToArray();
    }

    public IReadOnlyList<AuditEntry> Tail(int max) => TailMerged(max);

    /// <summary>
    /// The newest <paramref name="max"/> entries of the agent and the owner stream (every file of both) merged by time, oldest first (newest first when asked); equal times keep
    /// file order (older file first, the agent stream before the owner stream).
    /// </summary>
    public IReadOnlyList<AuditEntry> TailMerged(int max, bool newestFirst = false)
    {
        if (max < 1) return [];
        Flush();   // this process's own pending refusal counts are in what it reads
        List<AuditEntry> ordered = [.. ReadLane(_agent, max).Concat(ReadLane(_owner, max)).OrderBy(e => e.At.UtcTicks)];   // OrderBy is stable
        List<AuditEntry> result = [.. ordered.Skip(Math.Max(0, ordered.Count - max))];
        if (newestFirst) result.Reverse();
        return result;
    }

    /// <summary>One stream's newest <paramref name="max"/> entries, oldest first: older files are read only while the newer ones hold too few.</summary>
    static List<AuditEntry> ReadLane(Lane lane, int max)
    {
        var files = new List<string> { lane.Current };
        for (int i = 1; i <= lane.Rotated; i++) files.Add(lane.Old(i));
        var result = new List<AuditEntry>();
        foreach (string f in files)
        {
            var parsed = new List<AuditEntry>();
            foreach (string l in ReadLines(f))
                if (TryParse(l, out AuditEntry? e)) parsed.Add(e!);
            result.InsertRange(0, parsed.Skip(Math.Max(0, parsed.Count - (max - result.Count))));
            if (result.Count >= max) break;
        }
        return result;
    }

    static List<string> ReadLines(string path)
    {
        var lines = new List<string>();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long len = fs.Length;
            long start = Math.Max(0, len - MaxReadBytes);
            fs.Seek(start, SeekOrigin.Begin);
            byte[] bytes = new byte[len - start];
            int read = 0;
            while (read < bytes.Length)
            {
                int n = fs.Read(bytes, read, bytes.Length - read);
                if (n <= 0) break;
                read += n;
            }
            string text = Utf8.GetString(bytes, 0, read);
            if (start > 0)
            {
                int cut = text.IndexOf('\n', StringComparison.Ordinal);   // drop the partial first line
                text = cut >= 0 ? text[(cut + 1)..] : "";
            }
            foreach (string l in text.Split('\n'))
                if (l.Length > 0) lines.Add(l.TrimEnd('\r'));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            // No log yet, or it cannot be read right now: nothing to show.
        }
        return lines;
    }

    static bool TryParse(string line, out AuditEntry? entry)
    {
        entry = null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 8 });
            JsonElement r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return false;
            if (!r.TryGetProperty("v", out JsonElement v) || v.ValueKind != JsonValueKind.Number || v.GetInt32() != Version) return false;
            if (!DateTimeOffset.TryParse(Str(r, "at"), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset at)) return false;
            string? version = Str(r, "clientVersion");
            int count = r.TryGetProperty("count", out JsonElement c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out int n) && n > 1 ? n : 1;
            entry = AuditText.Clean(new AuditEntry(at, Str(r, "session") ?? "", Str(r, "client") ?? "", version, Str(r, "tool") ?? "", Str(r, "jobId"), Str(r, "job"),
                Str(r, "outcome") ?? "", Str(r, "code"), Str(r, "detail") ?? "", count));
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return false;   // a damaged or foreign line is skipped
        }
    }

    static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
}
