namespace Cadence.Scheduler.Apply;

/// <summary>
/// The cross-process writer lock (PLAN 12.1.3): a named mutex <c>Local\CadenceWrite.&lt;FolderName&gt;</c> that every Cadence writer (the app, the
/// command line, the MCP server) holds around "check, write, verify", so two processes cannot interleave a live check and a write.
/// <list type="bullet">
/// <item>A mutex is thread-affine: acquire and dispose on the same thread, inside synchronous code (never across an <c>await</c>).</item>
/// <item>It is re-entrant for the owning thread, so a caller may hold the lock around "plan, then apply" while <see cref="Applier.Apply"/> takes it again.</item>
/// <item>A holder that died leaves the mutex abandoned: that counts as acquired (the previous writer is gone; the guard re-reads the live state anyway).</item>
/// <item>Per Windows session (<c>Local\</c>) and per managed folder, so a test folder never blocks the real one.</item>
/// </list>
/// </summary>
public sealed class WriterLock : IDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    /// <summary>What a writer is told when the lock could not be taken in time.</summary>
    public const string BusyMessage = "Another Cadence window or agent is saving right now; try again in a moment.";

    Mutex? _mutex;

    WriterLock(Mutex mutex) => _mutex = mutex;

    /// <summary><c>Local\CadenceWrite.&lt;folder&gt;</c> (a backslash in the folder name is replaced, a mutex name allows only one).</summary>
    public static string MutexName(string folderName)
    {
        ArgumentException.ThrowIfNullOrEmpty(folderName);
        return "Local\\CadenceWrite." + folderName.Replace('\\', '_');
    }

    /// <summary>Takes the lock; null when it is still held by another thread or process after <paramref name="timeout"/> (or can't be opened at all).</summary>
    public static WriterLock? TryAcquire(string folderName, TimeSpan timeout)
    {
        Mutex mutex;
        try { mutex = new Mutex(false, MutexName(folderName)); }
        catch (UnauthorizedAccessException) { return null; }   // created by a process with another access token (for example an elevated one): not ours to wait on
        bool owned;
        try
        {
            try { owned = mutex.WaitOne(timeout); }
            catch (AbandonedMutexException) { owned = true; }   // the previous holder died; we own it now
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
        if (!owned)
        {
            mutex.Dispose();
            return null;
        }
        return new WriterLock(mutex);
    }

    public void Dispose()
    {
        Mutex? m = Interlocked.Exchange(ref _mutex, null);
        if (m is null) return;
        try { m.ReleaseMutex(); }
        catch (ApplicationException) { }   // released from another thread than the one that took it: nothing sensible left to do
        m.Dispose();
    }
}
