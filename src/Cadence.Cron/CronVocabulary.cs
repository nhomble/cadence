using System.Collections;

namespace Cadence.Cron;

/// <summary>Days of the week as a set of flags: Sunday = 1 ... Saturday = 64, so bit n is cron's weekday number n (0 = Sunday ... 6 = Saturday).</summary>
[Flags]
public enum CronDays
{
    None = 0, Sunday = 1, Monday = 2, Tuesday = 4, Wednesday = 8, Thursday = 16, Friday = 32, Saturday = 64, All = 127,
}

/// <summary>Months of the year as a set of flags: January = 1 ... December = 2048, so bit n is month n + 1.</summary>
[Flags]
public enum CronMonths
{
    None = 0, January = 1, February = 2, March = 4, April = 8, May = 16, June = 32,
    July = 64, August = 128, September = 256, October = 512, November = 1024, December = 2048, All = 4095,
}

/// <summary>Immutable list with structural equality and hash code (the cron module's own small copy of the idea, so it needs nothing from the rest of the app).</summary>
public sealed class CronList<T> : IReadOnlyList<T>, IEquatable<CronList<T>>
{
    public static readonly CronList<T> Empty = new([]);

    readonly T[] _items;

    public CronList(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items = items.ToArray();
    }

    public int Count => _items.Length;

    public T this[int index] => _items[index];

    public bool Equals(CronList<T>? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return _items.AsSpan().SequenceEqual(other._items);
    }

    public override bool Equals(object? obj) => obj is CronList<T> other && Equals(other);

    public override int GetHashCode()
    {
        var h = new HashCode();
        foreach (var item in _items) h.Add(item);
        return h.ToHashCode();
    }

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => "[" + string.Join(", ", _items) + "]";
}