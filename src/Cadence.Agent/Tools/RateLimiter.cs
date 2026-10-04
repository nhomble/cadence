namespace Cadence.Agent.Tools;

/// <summary>
/// A rolling-window rate limit per bucket (PLAN 12.3.2 gate 3): at most <c>max</c> takes in any <c>window</c>, counted per server process (the state is in memory
/// and dies with the process, on purpose: it limits a runaway agent loop, it is not a security boundary). The clock is injected, so tests never sleep.
/// </summary>
public sealed class RateLimiter(Func<DateTimeOffset> clock)
{
    readonly object _gate = new();
    readonly Dictionary<string, Queue<DateTimeOffset>> _buckets = new(StringComparer.Ordinal);

    /// <summary>
    /// Takes one slot from <paramref name="bucket"/>. Returns null when the call may go ahead; otherwise how long to wait until the oldest take leaves the window
    /// (never less than one second, so the message is always a usable number). A refused call is not counted.
    /// </summary>
    public TimeSpan? TryTake(string bucket, int max, TimeSpan window)
    {
        ArgumentException.ThrowIfNullOrEmpty(bucket);
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        DateTimeOffset now = clock();
        lock (_gate)
        {
            if (!_buckets.TryGetValue(bucket, out Queue<DateTimeOffset>? q)) _buckets[bucket] = q = new Queue<DateTimeOffset>();
            while (q.Count > 0 && now - q.Peek() >= window) q.Dequeue();
            if (q.Count >= max)
            {
                TimeSpan wait = q.Peek() + window - now;
                return wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait;
            }
            q.Enqueue(now);
            return null;
        }
    }
}
