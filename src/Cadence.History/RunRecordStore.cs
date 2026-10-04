using System.Text;
using System.Text.RegularExpressions;
using Cadence.Core.History;

namespace Cadence.History;

/// <summary>
/// Reads the files the wrapper writes under <c>%LOCALAPPDATA%\Cadence\runs\&lt;jobId&gt;\</c>: <c>runs.jsonl</c> and <c>&lt;runId&gt;.out.txt</c> /
/// <c>.err.txt</c> (PLAN Appendix A). The root is injected so tests use a temp folder; <see cref="DefaultRoot"/> is the real one.
/// Every file is opened with <c>FileShare.ReadWrite | FileShare.Delete</c> so the wrapper can keep appending, rotating and pruning while we read.
/// </summary>
public sealed partial class RunRecordStore
{
    /// <summary>The folder name the wrapper uses for runs it could not attribute to a job (it failed before reading its own metadata).</summary>
    public const string UnknownFolder = "_unknown";

    public const string JournalFile = "runs.jsonl";
    const int Block = 64 * 1024;

    readonly string _root;

    public RunRecordStore(string runsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runsRoot);
        _root = runsRoot;
    }

    /// <summary><c>%LOCALAPPDATA%\Cadence\runs</c>, the folder the wrapper writes to for a task in the <c>\Cadence</c> folder.</summary>
    public static string DefaultRoot() => RootFor("Cadence");

    /// <summary><c>%LOCALAPPDATA%\&lt;runsRootName&gt;\runs</c> (the wrapper's <c>$rr</c>: "Cadence", or "CadenceTest" for the tests and the dev override).</summary>
    public static string RootFor(string runsRootName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runsRootName);
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), runsRootName, "runs");
    }

    public string Root => _root;

    /// <summary>
    /// The newest records of the job (at most <paramref name="maxLines"/> lines of the file's tail, oldest first; garbled and partial lines
    /// are skipped, so fewer may come back). Reads only the end of the file, so a multi-MB log costs the same as a small one. Missing
    /// folder or file gives an empty list. Records the wrapper filed under <c>_unknown</c> are NOT included; see the overload with a task path.
    /// </summary>
    public IReadOnlyList<RunRecord> ReadTail(string jobId, int maxLines = 500) => ReadFolder(CheckId(jobId), jobId, maxLines);

    /// <summary>
    /// As <see cref="ReadTail(string, int)"/>, plus the records in <c>_unknown\runs.jsonl</c> whose <c>task</c> equals
    /// <paramref name="taskPath"/> (case-insensitive), re-attributed to <paramref name="jobId"/>. Result is in wrapperStart order.
    /// </summary>
    public IReadOnlyList<RunRecord> ReadTail(string jobId, string taskPath, int maxLines = 500)
    {
        var all = new List<RunRecord>(ReadFolder(CheckId(jobId), jobId, maxLines));
        foreach (RunRecord r in ReadFolder(UnknownFolder, UnknownFolder, maxLines))
            if (string.Equals(r.TaskPath, taskPath, StringComparison.OrdinalIgnoreCase))
                all.Add(r with { JobId = jobId });
        all.Sort((a, b) => a.WrapperStart.CompareTo(b.WrapperStart));
        return all;
    }

    /// <summary>[Review R] The most of one output file that is ever read: 4x what the wrapper keeps (256 KB), so a normal file is read whole.</summary>
    public const int MaxOutputBytes = 1 << 20;

    /// <summary>
    /// The captured stdout (or stderr) of one run, or null when there is none (nothing was printed, or the wrapper already pruned the file).
    /// The files hold at most the last 256 KB of the stream. [Review R] A file larger than <see cref="MaxOutputBytes"/> (never written by the
    /// wrapper: tampering or a future bug) is not read whole: only its last <see cref="MaxOutputBytes"/> bytes, from the next character boundary,
    /// so the app and the MCP server can't be made to load hundreds of MB into memory.
    /// </summary>
    public string? ReadOutput(string jobId, string runId, bool stderr)
    {
        CheckId(jobId);
        if (!RunIdPattern().IsMatch(runId)) throw new ArgumentException("Not a run id: " + runId, nameof(runId));
        string path = Path.Combine(_root, jobId, runId + (stderr ? ".err.txt" : ".out.txt"));
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = fs.Length;
            if (length > MaxOutputBytes)
            {
                fs.Seek(length - MaxOutputBytes, SeekOrigin.Begin);
                var buf = new byte[MaxOutputBytes];
                int got = 0;
                while (got < buf.Length)
                {
                    int r = fs.Read(buf, got, buf.Length - got);
                    if (r <= 0) break;
                    got += r;
                }
                int start = 0;
                while (start < got && (buf[start] & 0xC0) == 0x80) start++;   // skip the tail of a character cut by the seek
                return Encoding.UTF8.GetString(buf, start, got - start);
            }
            using var sr = new StreamReader(fs, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            return sr.ReadToEnd();
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Deletes the output files (<c>*.txt</c>) of every job folder whose last write is older than <paramref name="age"/> before
    /// <paramref name="now"/> (default: the clock). Returns how many files went. <b>runs.jsonl is not rewritten</b>: the wrapper appends to
    /// it without a lock and caps it itself (4,000 lines / 2 MB), so rewriting it from here could lose a record; use the
    /// <c>notBefore</c> argument of <see cref="RunHistory"/> to ignore old lines.
    /// </summary>
    public int PruneOlderThan(TimeSpan age, DateTimeOffset? now = null)
    {
        if (!Directory.Exists(_root)) return 0;
        DateTime cutoff = (now ?? DateTimeOffset.UtcNow).UtcDateTime - age;
        int removed = 0;
        foreach (string dir in Directory.EnumerateDirectories(_root))
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*.txt").ToList(); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (string f in files)
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(f) >= cutoff) continue;
                    File.Delete(f);
                    removed++;
                }
                catch (IOException) { /* in use or already gone */ }
                catch (UnauthorizedAccessException) { }
            }
        }
        return removed;
    }

    /// <summary>
    /// Deletes <c>&lt;root&gt;\&lt;jobId&gt;</c> with everything in it (the history of a job that was deleted). The id must be a job id
    /// (<c>[a-z0-9]{8}</c>); <c>_unknown</c> is refused (the wrapper caps that folder itself). Files in use are skipped, never an error.
    /// A reparse point (junction or symbolic link) is removed itself and never followed, so nothing outside the root can be deleted.
    /// Returns true when the folder no longer exists afterwards (also when it never did).
    /// </summary>
    /// <exception cref="ArgumentException">The id is not a job id, or is <c>_unknown</c>.</exception>
    public bool DeleteJob(string jobId)
    {
        if (string.Equals(jobId, UnknownFolder, StringComparison.Ordinal)) throw new ArgumentException("The " + UnknownFolder + " folder is not a job.", nameof(jobId));
        CheckId(jobId);
        return DeleteFolder(Path.Combine(_root, jobId));
    }

    /// <summary>
    /// Deletes the folders of jobs that no longer exist: every <c>[a-z0-9]{8}</c> folder under the root whose id is not in
    /// <paramref name="liveJobIds"/> and whose newest file (the folder's own last write when it is empty) is older than
    /// <paramref name="minAge"/> before <paramref name="now"/>. Never touches <c>_unknown</c> or any other name. Returns how many folders went.
    /// </summary>
    public int PruneOrphans(IReadOnlySet<string> liveJobIds, TimeSpan minAge, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(liveJobIds);
        string[] dirs;
        try { dirs = Directory.GetDirectories(_root); }
        catch (DirectoryNotFoundException) { return 0; }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }

        DateTime cutoff = now.UtcDateTime - minAge;
        int removed = 0;
        foreach (string dir in dirs)
        {
            string id = Path.GetFileName(dir);
            if (!JobIdOnlyPattern().IsMatch(id) || liveJobIds.Contains(id)) continue;
            if (NewestWriteUtc(dir) >= cutoff) continue;
            if (DeleteFolder(dir)) removed++;
        }
        return removed;
    }

    static DateTime NewestWriteUtc(string dir)
    {
        try
        {
            DateTime own = new DirectoryInfo(dir).LastWriteTimeUtc;
            if (IsReparsePoint(dir)) return own;   // judged by the link itself, never by what it points to
            // The folder's own time moves whenever a file is added or removed, so it only counts when the folder holds no file.
            DateTime? newest = null;
            foreach (string f in Directory.EnumerateFiles(dir))
            {
                DateTime t = File.GetLastWriteTimeUtc(f);
                if (newest is null || t > newest) newest = t;
            }
            return newest ?? own;
        }
        catch (IOException) { return DateTime.MaxValue; }   // cannot tell: keep it
        catch (UnauthorizedAccessException) { return DateTime.MaxValue; }
    }

    static bool IsReparsePoint(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    /// <summary>Deletes a folder tree without ever entering a reparse point. True when the folder is gone.</summary>
    static bool DeleteFolder(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return true;
            if (IsReparsePoint(dir))
            {
                Directory.Delete(dir, recursive: false);   // removes the link only, not its target
                return !Directory.Exists(dir);
            }
            foreach (string entry in Directory.EnumerateFileSystemEntries(dir).ToList())
            {
                try
                {
                    FileAttributes a = File.GetAttributes(entry);
                    if ((a & FileAttributes.Directory) != 0) DeleteFolder(entry);
                    else File.Delete(entry);   // a file symlink is deleted itself
                }
                catch (IOException) { /* in use: leave it */ }
                catch (UnauthorizedAccessException) { }
            }
            try { Directory.Delete(dir, recursive: false); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return !Directory.Exists(dir);
        }
        catch (IOException) { return !Directory.Exists(dir); }
        catch (UnauthorizedAccessException) { return !Directory.Exists(dir); }
    }

    static string CheckId(string jobId)
    {
        if (!JobIdPattern().IsMatch(jobId)) throw new ArgumentException("Not a job id: " + jobId, nameof(jobId));
        return jobId;
    }

    List<RunRecord> ReadFolder(string folder, string jobIdForRecords, int maxLines)
    {
        if (maxLines <= 0) return [];
        string path = Path.Combine(_root, folder, JournalFile);
        for (int attempt = 0; ; attempt++)
        {
            try { return RunRecordParser.ParseText(TailText(path, maxLines), jobIdForRecords); }
            catch (FileNotFoundException) { return []; }
            catch (DirectoryNotFoundException) { return []; }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(25);   // the wrapper's rotation briefly holds the file
            }
            catch (IOException) { return []; }
            catch (UnauthorizedAccessException) { return []; }
        }
    }

    /// <summary>The last <paramref name="maxLines"/> complete-or-final lines of the file as text. The first (possibly cut) line is dropped unless the read reached the file start.</summary>
    static string TailText(string path, int maxLines)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        long length = fs.Length;   // snapshot: bytes appended after this are the next read's business
        long start = length;
        var chunks = new List<byte[]>();
        int newlines = 0;
        // Lines are separated by LF whichever way they end, so counting LF bytes is enough (UTF-8 never contains 0x0A inside a character).
        while (start > 0 && newlines <= maxLines)
        {
            int n = (int)Math.Min(Block, start);
            start -= n;
            var buf = new byte[n];
            fs.Seek(start, SeekOrigin.Begin);
            int got = 0;
            while (got < n)
            {
                int r = fs.Read(buf, got, n - got);
                if (r <= 0) break;
                got += r;
            }
            if (got < n) Array.Resize(ref buf, got);
            foreach (byte b in buf) if (b == (byte)'\n') newlines++;
            chunks.Add(buf);
        }

        int total = 0;
        foreach (byte[] c in chunks) total += c.Length;
        var all = new byte[total];
        int pos = 0;
        for (int i = chunks.Count - 1; i >= 0; i--)
        {
            Buffer.BlockCopy(chunks[i], 0, all, pos, chunks[i].Length);
            pos += chunks[i].Length;
        }

        int from = 0;
        if (start > 0)
        {
            int firstNl = Array.IndexOf(all, (byte)'\n');
            if (firstNl < 0) return "";   // one giant partial line
            from = firstNl + 1;
        }

        // Keep the last maxLines lines: walk back over LF bytes (a final line without LF counts as a line).
        int end = all.Length;
        int lines = 0;
        int cut = from;
        for (int i = end - 1; i >= from; i--)
        {
            if (all[i] != (byte)'\n' || i == end - 1) continue;
            if (++lines >= maxLines) { cut = i + 1; break; }
        }
        return Encoding.UTF8.GetString(all, cut, end - cut);
    }

    [GeneratedRegex(@"\A(?:[a-z0-9]{8}|_unknown)\z")]
    private static partial Regex JobIdPattern();

    [GeneratedRegex(@"\A[a-z0-9]{8}\z")]
    private static partial Regex JobIdOnlyPattern();

    [GeneratedRegex(@"\A\d{8}-\d{6}-\d{3}\z")]
    private static partial Regex RunIdPattern();
}
