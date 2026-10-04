using System.Diagnostics;
using System.Globalization;
using System.Text;
using Cadence.Core.History;
using Cadence.History;
using Cadence.TestHarness;

namespace Cadence.IntegrationTests;

/// <summary>
/// RunRecordStore against real files in a temp folder (never %LOCALAPPDATA%\Cadence): tail reads, mixed LF/CRLF after a rotation,
/// huge logs, the wrapper appending and rotating while we read, _unknown attribution, output files, pruning. No Task Scheduler involved.
/// </summary>
public static class RunRecordStoreTests
{
    const string JobA = "abcd1234";
    const string JobB = "wxyz5678";

    static string NewRoot() => Path.Combine(Path.GetTempPath(), "CadenceStoreTests-" + Guid.NewGuid().ToString("N"));

    static void WithRoot(Action<string, RunRecordStore> body)
    {
        string root = NewRoot();
        Directory.CreateDirectory(root);
        try { body(root, new RunRecordStore(root)); }
        finally { try { Directory.Delete(root, recursive: true); } catch (IOException) { } }
    }

    static string Line(int i, string status = "ok", string task = "\\Cadence\\job a", string? msg = null)
    {
        DateTimeOffset at = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.FromHours(-4)).AddSeconds(i);
        string id = at.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        string wrapperStart = at.ToString("o", CultureInfo.InvariantCulture);
        string start = at.AddMilliseconds(500).ToString("o", CultureInfo.InvariantCulture);
        string end = at.AddMilliseconds(1500).ToString("o", CultureInfo.InvariantCulture);
        string m = msg is null ? "null" : "\"" + msg + "\"";
        return "{\"v\":1,\"wv\":1,\"task\":\"" + task.Replace("\\", "\\\\", StringComparison.Ordinal) + "\",\"runId\":\"" + id + "\",\"wrapperStart\":\"" + wrapperStart + "\",\"start\":\"" + start + "\",\"end\":\"" + end + "\",\"exit\":0,\"status\":\"" + status + "\",\"pid\":1,\"outBytes\":5,\"errBytes\":0,\"msg\":" + m + "}";
    }

    static string Journal(string root, string job)
    {
        string dir = Path.Combine(root, job);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "runs.jsonl");
    }

    public static void MissingFilesGiveEmpty() => WithRoot((root, store) =>
    {
        T.Eq("no root content", 0, store.ReadTail(JobA).Count);
        Directory.CreateDirectory(Path.Combine(root, JobA));
        T.Eq("folder without a journal", 0, store.ReadTail(JobA).Count);
        File.WriteAllText(Journal(root, JobA), "");
        T.Eq("empty journal", 0, store.ReadTail(JobA).Count);
        T.Eq("missing root folder", 0, new RunRecordStore(Path.Combine(root, "nope")).ReadTail(JobA).Count);
        T.True("missing output is null", store.ReadOutput(JobA, "20261001-000000-000", false) is null);
    });

    public static void TailReadsOnlyTheLastLines() => WithRoot((root, store) =>
    {
        File.WriteAllText(Journal(root, JobA), string.Join("\n", Enumerable.Range(0, 100).Select(i => Line(i))) + "\n");
        IReadOnlyList<RunRecord> all = store.ReadTail(JobA);
        T.Eq("all 100 below the default 500", 100, all.Count);
        T.Eq("oldest first", Line(0).Contains(all[0].RunId, StringComparison.Ordinal), true);
        T.Eq("job id is the folder", JobA, all[0].JobId);

        IReadOnlyList<RunRecord> tail = store.ReadTail(JobA, 10);
        T.Eq("10 lines", 10, tail.Count);
        T.True("they are the newest 10, in order", Line(90).Contains(tail[0].RunId, StringComparison.Ordinal) && Line(99).Contains(tail[9].RunId, StringComparison.Ordinal));
        T.Eq("1 line", 1, store.ReadTail(JobA, 1).Count);
        T.Eq("0 lines", 0, store.ReadTail(JobA, 0).Count);

        // no trailing newline: the final line still counts
        File.WriteAllText(Journal(root, JobA), string.Join("\n", Enumerable.Range(0, 5).Select(i => Line(i))));
        T.Eq("no final newline, last 2", 2, store.ReadTail(JobA, 2).Count);
        T.True("and the last one is the newest", Line(4).Contains(store.ReadTail(JobA, 2)[1].RunId, StringComparison.Ordinal));
    });

    public static void MixedLineEndingsAfterRotation() => WithRoot((root, store) =>
    {
        // exactly what the wrapper leaves behind: WriteAllLines (CRLF) of the kept tail, then LF appends
        var sb = new StringBuilder();
        for (int i = 0; i < 20; i++) sb.Append(Line(i)).Append("\r\n");
        for (int i = 20; i < 30; i++) sb.Append(Line(i)).Append('\n');
        File.WriteAllText(Journal(root, JobA), sb.ToString(), new UTF8Encoding(false));
        IReadOnlyList<RunRecord> all = store.ReadTail(JobA);
        T.Eq("all 30 records", 30, all.Count);
        T.True("no record holds a CR", all.All(r => !r.RunId.Contains('\r', StringComparison.Ordinal) && (r.Message is null || !r.Message.Contains('\r', StringComparison.Ordinal))));
        IReadOnlyList<RunRecord> tail = store.ReadTail(JobA, 15);
        T.True("a tail that straddles the CRLF/LF seam is right", tail.Count == 15 && Line(15).Contains(tail[0].RunId, StringComparison.Ordinal) && Line(29).Contains(tail[14].RunId, StringComparison.Ordinal));
    });

    public static void GarbledAndPartialLinesAreSkipped() => WithRoot((root, store) =>
    {
        string text = Line(1) + "\n" + "{\"v\":1,\"runId\":\"cut" + "\n" + "garbage\n" + "\n" + Line(2) + "\r\n" + Line(3)[..80];
        File.WriteAllText(Journal(root, JobA), text);
        IReadOnlyList<RunRecord> r = store.ReadTail(JobA);
        T.Eq("two good records; a half-appended last line is ignored", 2, r.Count);
        File.AppendAllText(Journal(root, JobA), Line(3)[80..] + "\n");   // the append finishes: that line is now readable? it is joined to the partial text
        T.Eq("the finished line (partial text + rest) parses", 3, store.ReadTail(JobA).Count);
    });

    public static void UnicodeInRecordsAndOutput() => WithRoot((root, store) =>
    {
        string msg = "café 日本語 \U0001F600";
        File.WriteAllText(Journal(root, JobA), Line(1, "launchError", msg: "caf\\u00e9 日本語 \\ud83d\\ude00") + "\n", new UTF8Encoding(false));
        T.Eq("message decodes", msg, store.ReadTail(JobA)[0].Message);

        string runId = "20261001-000001-000";
        string text = "héllo wörld 日本語 😀\r\nline 2";
        File.WriteAllText(Path.Combine(root, JobA, runId + ".out.txt"), text, new UTF8Encoding(false));
        T.Eq("stdout round-trips", text, store.ReadOutput(JobA, runId, stderr: false));
        T.True("stderr absent -> null", store.ReadOutput(JobA, runId, stderr: true) is null);
        File.WriteAllText(Path.Combine(root, JobA, runId + ".err.txt"), "oops ünï", new UTF8Encoding(false));
        T.Eq("stderr", "oops ünï", store.ReadOutput(JobA, runId, stderr: true));

        // a multi-byte character exactly on a 64 KB block boundary must not corrupt the tail read
        var big = new StringBuilder();
        int n = 0;
        while (big.Length < 200_000) big.Append(Line(n++, "launchError", msg: "日本語日本語日本語")).Append('\n');
        File.WriteAllText(Journal(root, JobB), big.ToString(), new UTF8Encoding(false));
        IReadOnlyList<RunRecord> tail = store.ReadTail(JobB, n);
        T.True("every record read back across several blocks, none garbled", tail.Count == n && tail.All(r => r.Message == "日本語日本語日本語"));
    });

    public static void HugeLogReadsFast() => WithRoot((root, store) =>
    {
        string path = Journal(root, JobA);
        int total = 0;
        using (var w = new StreamWriter(path, false, new UTF8Encoding(false), 1 << 16))
        {
            while (w.BaseStream.Length < 30L * 1024 * 1024)
            {
                for (int k = 0; k < 1000; k++) { w.Write(Line(total % 86000)); w.Write('\n'); total++; }
                w.Flush();
            }
        }
        long size = new FileInfo(path).Length;
        // [CP-B] Best of 3 after a warm-up read (JIT, file cache), so a busy machine does not fail a test that is about NOT reading the whole file.
        IReadOnlyList<RunRecord> tail = store.ReadTail(JobA, 500);
        long best = long.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            tail = store.ReadTail(JobA, 500);
            sw.Stop();
            best = Math.Min(best, sw.ElapsedMilliseconds);
        }
        T.Eq("500 records of a " + size / (1024 * 1024) + " MB log", 500, tail.Count);
        T.True("tail read took " + best + " ms (best of 3 after a warm-up; limit 250)", best < 250);
        T.True("the records are the last ones", Line((total - 1) % 86000).Contains(tail[^1].RunId, StringComparison.Ordinal));
    });

    public static void ReadsWhileTheWrapperAppendsAndRotates() => WithRoot((root, store) =>
    {
        string path = Journal(root, JobA);
        File.WriteAllText(path, string.Join("\n", Enumerable.Range(0, 50).Select(i => Line(i))) + "\n");
        int appended = 0;
        Exception? writerError = null;
        using var stop = new CancellationTokenSource();
        var writer = new Thread(() =>
        {
            try
            {
                for (int i = 50; i < 650 && !stop.IsCancellationRequested; i++)
                {
                    // the wrapper: AppendAllText (shares Read only), and every so often WriteAllLines of its kept tail (CRLF)
                    File.AppendAllText(path, Line(i) + "\n");
                    Interlocked.Increment(ref appended);
                    if (i % 200 == 0)
                    {
                        string[] keep = File.ReadAllLines(path).TakeLast(100).ToArray();
                        File.WriteAllLines(path, keep);
                    }
                }
            }
            catch (Exception ex) { writerError = ex; }
        });
        writer.Start();

        int reads = 0, bad = 0;
        Exception? readerError = null;
        try
        {
            while (writer.IsAlive)
            {
                IReadOnlyList<RunRecord> r = store.ReadTail(JobA, 100);
                reads++;
                // never an exception, never a torn record; every record is a whole one (RunId ties back to the timestamp)
                foreach (RunRecord rec in r)
                    if (rec.RunId.Length != 19 || rec.Status != RunStatus.Ok) bad++;
            }
        }
        catch (Exception ex) { readerError = ex; }
        writer.Join();
        stop.Cancel();

        T.True("the writer did not fail: " + writerError?.Message, writerError is null);
        T.True("the reader never threw: " + readerError?.Message, readerError is null);
        T.Eq("no torn or garbled records came back (" + reads + " reads)", 0, bad);
        IReadOnlyList<RunRecord> last = store.ReadTail(JobA, 100);
        T.Eq("after the writer is done: the last 100 are there", 100, last.Count);
        T.True("ending with the final append", Line(649).Contains(last[^1].RunId, StringComparison.Ordinal));
    });

    public static void UnknownFolderIsAttributedByTaskPath() => WithRoot((root, store) =>
    {
        File.WriteAllText(Journal(root, JobA), Line(1, task: "\\Cadence\\job a") + "\n" + Line(3, task: "\\Cadence\\job a") + "\n");
        File.WriteAllText(Journal(root, RunRecordStore.UnknownFolder),
            Line(2, "launchError", "\\Cadence\\JOB A", "boom") + "\n" + Line(4, "launchError", "\\Cadence\\other job", "x") + "\n" + Line(5, "launchError", "", "no task") + "\n");

        IReadOnlyList<RunRecord> plain = store.ReadTail(JobA);
        T.Eq("without a task path _unknown is not read", 2, plain.Count);

        IReadOnlyList<RunRecord> merged = store.ReadTail(JobA, "\\Cadence\\job a");
        T.Eq("3 records: 2 own + 1 attributed (task path compared case-insensitively)", 3, merged.Count);
        T.True("in time order", merged[0].WrapperStart < merged[1].WrapperStart && merged[1].WrapperStart < merged[2].WrapperStart);
        T.Eq("the attributed one carries the job id", JobA, merged[1].JobId);
        T.Eq("and its message", "boom", merged[1].Message);
        T.Eq("another job's _unknown record is not ours", 0, store.ReadTail(JobB, "\\Cadence\\nobody").Count);
        T.Eq("_unknown itself can be read", 3, store.ReadTail(RunRecordStore.UnknownFolder).Count);
    });

    public static void BadIdsAreRefused() => WithRoot((root, store) =>
    {
        foreach (string bad in new[] { "..", "..\\x", "a/b", "", "ABCD1234", "short", "toolongid1", "abcd 123" })
            T.Throws<ArgumentException>("job id '" + bad + "'", () => store.ReadTail(bad));
        foreach (string bad in new[] { "..\\..\\x", "20261001-000000", "x", "20261001-000000-0000", "20261001-000000-000\\.." })
            T.Throws<ArgumentException>("run id '" + bad + "'", () => store.ReadOutput(JobA, bad, false));
        T.Throws<ArgumentException>("blank root", () => new RunRecordStore(" "));
    });

    public static void PrunesOldOutputFilesOnly() => WithRoot((root, store) =>
    {
        string dirA = Path.Combine(root, JobA), dirB = Path.Combine(root, JobB);
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        DateTimeOffset now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        void Make(string dir, string name, int daysOld)
        {
            string p = Path.Combine(dir, name);
            File.WriteAllText(p, "x");
            File.SetLastWriteTimeUtc(p, now.UtcDateTime.AddDays(-daysOld));
        }
        Make(dirA, "20260801-000000-000.out.txt", 60);
        Make(dirA, "20260801-000000-000.err.txt", 60);
        Make(dirA, "20260925-000000-000.out.txt", 6);
        Make(dirB, "20260701-000000-000.out.txt", 90);
        File.WriteAllText(Path.Combine(dirA, "runs.jsonl"), Line(1) + "\n");
        File.SetLastWriteTimeUtc(Path.Combine(dirA, "runs.jsonl"), now.UtcDateTime.AddDays(-400));

        int removed = store.PruneOlderThan(TimeSpan.FromDays(30), now);
        T.Eq("three old output files removed", 3, removed);
        T.True("the recent output stays", File.Exists(Path.Combine(dirA, "20260925-000000-000.out.txt")));
        T.True("the journal is never touched (the wrapper owns it)", File.Exists(Path.Combine(dirA, "runs.jsonl")) && store.ReadTail(JobA).Count == 1);
        T.Eq("a second pass has nothing to do", 0, store.PruneOlderThan(TimeSpan.FromDays(30), now));
        T.Eq("a missing root is fine", 0, new RunRecordStore(Path.Combine(root, "nope")).PruneOlderThan(TimeSpan.FromDays(1), now));
    });

    public static void OutputOpenedForWritingCanStillBeRead() => WithRoot((root, store) =>
    {
        string runId = "20261001-000001-000";
        Directory.CreateDirectory(Path.Combine(root, JobA));
        string path = Path.Combine(root, JobA, runId + ".out.txt");
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        fs.Write(Encoding.UTF8.GetBytes("partial output"));
        fs.Flush();
        T.Eq("shared read of a file the wrapper still holds", "partial output", store.ReadOutput(JobA, runId, false));
        using var j = new FileStream(Journal(root, JobA), FileMode.Create, FileAccess.Write, FileShare.Read);
        j.Write(Encoding.UTF8.GetBytes(Line(1) + "\n"));
        j.Flush();
        T.Eq("shared read of a journal held open for writing", 1, store.ReadTail(JobA).Count);
    });

    // ---- [CP-B] History folders of deleted jobs: DeleteJob and PruneOrphans (temp root only) ----

    static void MakeFile(string dir, string name, DateTime lastWriteUtc, string text = "x")
    {
        Directory.CreateDirectory(dir);
        string p = Path.Combine(dir, name);
        File.WriteAllText(p, text);
        File.SetLastWriteTimeUtc(p, lastWriteUtc);
    }

    static void Junction(string link, string target)
    {
        using Process p = Process.Start(new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, ArgumentList = { "/d", "/c", "mklink", "/J", link, target } })!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException("mklink /J failed (" + p.ExitCode + ")");
    }

    public static void DeleteJobRemovesTheFolder() => WithRoot((root, store) =>
    {
        string dir = Path.Combine(root, JobA);
        MakeFile(dir, "runs.jsonl", DateTime.UtcNow, Line(1) + "\n");
        MakeFile(dir, "20261001-000001-000.out.txt", DateTime.UtcNow);
        MakeFile(Path.Combine(dir, "nested"), "deep.txt", DateTime.UtcNow);   // the wrapper never makes one, but nothing may survive
        MakeFile(Path.Combine(root, JobB), "runs.jsonl", DateTime.UtcNow, Line(2) + "\n");

        T.Eq("DeleteJob reports the folder gone", true, store.DeleteJob(JobA));
        T.True("the folder and everything in it is gone", !Directory.Exists(dir));
        T.True("another job's folder is untouched", File.Exists(Journal(root, JobB)));
        T.Eq("a job that has no folder is fine (nothing to delete)", true, store.DeleteJob(JobA));
        T.Eq("a store whose root does not exist is fine", true, new RunRecordStore(Path.Combine(root, "nope")).DeleteJob(JobA));
    });

    public static void DeleteJobValidatesTheId() => WithRoot((root, store) =>
    {
        MakeFile(Path.Combine(root, RunRecordStore.UnknownFolder), "runs.jsonl", DateTime.UtcNow, Line(1) + "\n");
        MakeFile(root, "outside.txt", DateTime.UtcNow);
        foreach (string bad in new[] { "", " ", "..", ".", @"..\" + JobA, JobA + @"\..", "abcd123", "abcd12345", "ABCD1234", "abcd-234", @"C:\Windows", "a/b/c/d/e", "abcd1234\0", RunRecordStore.UnknownFolder })
            T.Throws<ArgumentException>("DeleteJob('" + bad.Replace("\0", "\\0", StringComparison.Ordinal) + "') is refused", () => store.DeleteJob(bad));
        T.True("_unknown was not deleted", File.Exists(Journal(root, RunRecordStore.UnknownFolder)));
        T.True("a file in the root was not deleted", File.Exists(Path.Combine(root, "outside.txt")));
    });

    public static void DeleteJobToleratesFilesInUse() => WithRoot((root, store) =>
    {
        string dir = Path.Combine(root, JobA);
        MakeFile(dir, "free.out.txt", DateTime.UtcNow);
        string held = Path.Combine(dir, "held.out.txt");
        using (var fs = new FileStream(held, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            fs.Write(Encoding.UTF8.GetBytes("the wrapper is writing"));
            fs.Flush();
            bool gone = true;
            try { gone = store.DeleteJob(JobA); }
            catch (Exception ex) { T.True("DeleteJob must not throw for a file in use", false, ex.GetType().Name + ": " + ex.Message); }
            T.Eq("reports that the folder is still there", false, gone);
            T.True("the free file went", !File.Exists(Path.Combine(dir, "free.out.txt")));
            T.True("the held file stays", File.Exists(held));
        }
        T.Eq("once the file is closed it works", true, store.DeleteJob(JobA));
        T.True("folder gone", !Directory.Exists(dir));
    });

    public static void DeleteJobNeverFollowsAReparsePointOutOfTheRoot() => WithRoot((root, store) =>
    {
        string outside = Path.Combine(Path.GetTempPath(), "CadenceStoreOutside-" + Guid.NewGuid().ToString("N"));
        try
        {
            MakeFile(outside, "precious.txt", DateTime.UtcNow, "keep me");

            // 1. the job folder itself is a junction to a folder outside the root
            Junction(Path.Combine(root, JobA), outside);
            T.True("setup: the link shows the outside file", File.Exists(Path.Combine(root, JobA, "precious.txt")));
            T.Eq("the link is removed", true, store.DeleteJob(JobA));
            T.True("the link is gone", !Directory.Exists(Path.Combine(root, JobA)));
            T.True("the target and its file are intact", File.ReadAllText(Path.Combine(outside, "precious.txt")) == "keep me");

            // 2. a junction INSIDE a job folder
            Directory.CreateDirectory(Path.Combine(root, JobB));
            MakeFile(Path.Combine(root, JobB), "runs.jsonl", DateTime.UtcNow, Line(1) + "\n");
            Junction(Path.Combine(root, JobB, "link"), outside);
            T.Eq("the folder with an inner link is removed", true, store.DeleteJob(JobB));
            T.True("the job folder is gone", !Directory.Exists(Path.Combine(root, JobB)));
            T.True("the target's file is intact after the inner link", File.ReadAllText(Path.Combine(outside, "precious.txt")) == "keep me");

            // 3. PruneOrphans treats a junction the same way
            Junction(Path.Combine(root, "link0001"), outside);
            int pruned = store.PruneOrphans(new HashSet<string>(), TimeSpan.Zero, DateTimeOffset.UtcNow.AddDays(1));
            T.Eq("the orphan link counts as one", 1, pruned);
            T.True("prune removed only the link", !Directory.Exists(Path.Combine(root, "link0001")) && File.ReadAllText(Path.Combine(outside, "precious.txt")) == "keep me");
        }
        finally
        {
            try { Directory.Delete(outside, recursive: true); } catch (IOException) { }
        }
    });

    public static void PruneOrphansKeepsEverythingThatIsNotAnOldOrphan() => WithRoot((root, store) =>
    {
        var now = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        DateTime old = now.UtcDateTime.AddDays(-40);
        DateTime fresh = now.UtcDateTime.AddDays(-2);
        MakeFile(Path.Combine(root, "live0001"), "runs.jsonl", old);                    // live: never pruned, however old
        MakeFile(Path.Combine(root, "orph0001"), "runs.jsonl", old);                    // old orphan: pruned
        MakeFile(Path.Combine(root, "orph0001"), "a.out.txt", old);
        MakeFile(Path.Combine(root, "orph0002"), "runs.jsonl", fresh);                  // recent orphan: kept
        MakeFile(Path.Combine(root, "orph0003"), "runs.jsonl", old);                    // one fresh file keeps the whole folder
        MakeFile(Path.Combine(root, "orph0003"), "b.out.txt", fresh);
        MakeFile(Path.Combine(root, RunRecordStore.UnknownFolder), "runs.jsonl", old);   // _unknown: the wrapper caps it, never pruned here
        MakeFile(Path.Combine(root, "Some Folder"), "x.txt", old);                      // not a job folder: never pruned
        MakeFile(Path.Combine(root, "orph001"), "x.txt", old);                          // 7 characters
        MakeFile(Path.Combine(root, "orph00001"), "x.txt", old);                        // 9 characters
        MakeFile(Path.Combine(root, "ORPH0004"), "x.txt", old);                         // upper case
        MakeFile(Path.Combine(root, "orph-005"), "x.txt", old);                         // not [a-z0-9]
        File.WriteAllText(Path.Combine(root, "orph0006"), "a file, not a folder");     // a file named like an id
        File.SetLastWriteTimeUtc(Path.Combine(root, "orph0006"), old);
        Directory.CreateDirectory(Path.Combine(root, "orph0007"));                      // empty orphan: judged by the folder's own time
        Directory.SetLastWriteTimeUtc(Path.Combine(root, "orph0007"), old);
        Directory.CreateDirectory(Path.Combine(root, "orph0008"));
        Directory.SetLastWriteTimeUtc(Path.Combine(root, "orph0008"), fresh);

        var live = new HashSet<string> { "live0001" };
        int removed = store.PruneOrphans(live, TimeSpan.FromDays(30), now);
        T.Eq("the old orphan and the old empty orphan went", 2, removed);

        string left = string.Join("|", Directory.GetFileSystemEntries(root).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal));
        T.Eq("what is left", "Some Folder|_unknown|live0001|orph-005|orph00001|orph0002|orph0003|orph0006|orph0008|orph001|ORPH0004".Split('|').OrderBy(n => n, StringComparer.Ordinal).Aggregate((a, b) => a + "|" + b), left);

        T.Eq("a second pass has nothing to do", 0, store.PruneOrphans(live, TimeSpan.FromDays(30), now));
        T.Eq("a missing root is fine", 0, new RunRecordStore(Path.Combine(root, "nope")).PruneOrphans(live, TimeSpan.FromDays(30), now));
        T.Throws<ArgumentNullException>("null live set", () => store.PruneOrphans(null!, TimeSpan.FromDays(30), now));

        // With no live jobs and no minimum age every job-shaped folder goes, and still nothing else.
        int all = store.PruneOrphans(new HashSet<string>(), TimeSpan.Zero, now.AddDays(1));
        T.Eq("all remaining job folders (live0001, orph0002, orph0003, orph0008)", 4, all);
        string rest = string.Join("|", Directory.GetFileSystemEntries(root).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal));
        T.Eq("only the non-job names stay", "Some Folder|_unknown|orph-005|orph00001|orph0006|orph001|ORPH0004".Split('|').OrderBy(n => n, StringComparer.Ordinal).Aggregate((a, b) => a + "|" + b), rest);
    });

    public static void RootForUsesTheRunsRootName()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        T.Eq("Cadence", Path.Combine(local, "Cadence", "runs"), RunRecordStore.RootFor("Cadence"));
        T.Eq("CadenceTest", Path.Combine(local, "CadenceTest", "runs"), RunRecordStore.RootFor("CadenceTest"));
        T.Eq("DefaultRoot is RootFor(Cadence)", RunRecordStore.RootFor("Cadence"), RunRecordStore.DefaultRoot());
        T.Throws<ArgumentException>("blank name", () => RunRecordStore.RootFor(" "));
    }
}
