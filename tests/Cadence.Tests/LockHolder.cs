using Cadence.Scheduler.Apply;

namespace Cadence.Tests;

/// <summary>
/// The helper process of the writer-lock tests (not a test class): the test exe started again with <c>--hold-writer-lock FOLDER</c> takes the
/// cross-process lock, prints "held", and keeps it until its stdin closes (a clean release) or it is killed (a crash: the mutex is abandoned).
/// Exit codes: 0 released, 3 could not take the lock within 10 seconds.
/// </summary>
internal static class LockHolder
{
    public const string Switch = "--hold-writer-lock";

    public static int Run(string folder)
    {
        using WriterLock? held = WriterLock.TryAcquire(folder, TimeSpan.FromSeconds(10));
        if (held is null)
        {
            Console.WriteLine("busy");
            return 3;
        }
        Console.WriteLine("held");
        Console.Out.Flush();
        while (Console.In.ReadLine() is not null) { }   // until the parent closes our stdin
        return 0;
    }
}
