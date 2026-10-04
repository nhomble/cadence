using Cadence.Agent.Tools;
using Cadence.TestHarness;

namespace Cadence.Tests.Mcp;

/// <summary>Phase 21: the rolling-window rate limiter (PLAN 12.3.2 gate 3). The clock is injected: nothing sleeps.</summary>
public static class RateLimiterTests
{
    static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    public static void ThirtyPerMinuteAndTheThirtyFirstWaits()
    {
        DateTimeOffset now = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        var limiter = new RateLimiter(() => now);
        for (int i = 1; i <= 30; i++) T.True("take " + i, limiter.TryTake("write", 30, Minute) is null);
        TimeSpan? wait = limiter.TryTake("write", 30, Minute);
        T.True("the 31st is refused", wait is not null);
        T.Eq("it must wait the whole window (nothing has left it yet)", Minute, wait!.Value);
        now = now.AddSeconds(20);
        T.Eq("20 seconds later: 40 seconds", TimeSpan.FromSeconds(40), limiter.TryTake("write", 30, Minute)!.Value);
        now = now.AddSeconds(40);
        T.True("exactly one window after the first take it is free again", limiter.TryTake("write", 30, Minute) is null);
    }

    public static void ARefusedTakeIsNotCountedAndBucketsAreIndependent()
    {
        DateTimeOffset now = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        var limiter = new RateLimiter(() => now);
        T.True("1", limiter.TryTake("a", 2, Minute) is null);
        now = now.AddSeconds(10);
        T.True("2", limiter.TryTake("a", 2, Minute) is null);
        for (int i = 0; i < 5; i++) T.True("refused " + i, limiter.TryTake("a", 2, Minute) is not null);
        T.True("another bucket is separate", limiter.TryTake("b", 1, Minute) is null);
        T.True("and has its own limit", limiter.TryTake("b", 1, Minute) is not null);
        now = now.AddSeconds(50);   // the first take of "a" has left the window; the refused ones did not extend it
        T.True("the window slid: one slot is free", limiter.TryTake("a", 2, Minute) is null);
        T.True("then it is full again (the take at +10 s is still inside)", limiter.TryTake("a", 2, Minute) is not null);
    }

    public static void TheWaitIsAtLeastOneSecondAndTheArgumentsAreChecked()
    {
        DateTimeOffset now = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        var limiter = new RateLimiter(() => now);
        T.True("take", limiter.TryTake("x", 1, TimeSpan.FromSeconds(10)) is null);
        now = now.AddMilliseconds(9800);
        T.Eq("a wait of 200 ms is reported as one second", TimeSpan.FromSeconds(1), limiter.TryTake("x", 1, TimeSpan.FromSeconds(10))!.Value);
        T.Throws<ArgumentException>("an empty bucket name", () => limiter.TryTake("", 1, Minute));
        T.Throws<ArgumentOutOfRangeException>("a maximum below one", () => limiter.TryTake("x", 0, Minute));
    }

    public static void ManyThreadsNeverExceedTheLimit()
    {
        var limiter = new RateLimiter(() => new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero));
        int allowed = 0;
        Parallel.For(0, 200, _ => { if (limiter.TryTake("w", 30, Minute) is null) Interlocked.Increment(ref allowed); });
        T.Eq("exactly 30 got through", 30, allowed);
    }
}
