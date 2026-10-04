namespace Cadence.Core.Scheduling;

/// <summary>Resolves a local wall-clock time to an instant under the Cadence DST policy (PLAN 3.4 item 6).</summary>
public static class WallClock
{
    /// <summary>
    /// Invalid (spring-forward gap) times are shifted forward by the DST delta and flagged as adjusted;
    /// ambiguous (fall-back) times take the first, daylight (larger) offset.
    /// </summary>
    public static (DateTimeOffset At, bool Adjusted) Resolve(DateTime local, TimeZoneInfo tz)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(local))
        {
            TimeSpan before = tz.GetUtcOffset(local.AddDays(-1));
            TimeSpan after = tz.GetUtcOffset(local.AddDays(1));
            TimeSpan delta = after - before;
            if (delta <= TimeSpan.Zero) delta = TimeSpan.FromHours(1);
            DateTime shifted = local + delta;
            return (new DateTimeOffset(shifted, tz.GetUtcOffset(shifted)), true);
        }
        if (tz.IsAmbiguousTime(local))
        {
            TimeSpan first = tz.GetAmbiguousTimeOffsets(local).Max();
            return (new DateTimeOffset(local, first), false);
        }
        return (new DateTimeOffset(local, tz.GetUtcOffset(local)), false);
    }
}
