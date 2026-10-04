using System.Collections;

namespace Cadence.Core.Model;

/// <summary>Immutable list with structural equality and hash code; serializes as an array.</summary>
public sealed class ValueList<T> : IReadOnlyList<T>, IEquatable<ValueList<T>>
{
    public static readonly ValueList<T> Empty = new([]);

    readonly T[] _items;

    public ValueList(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items = items.ToArray();
    }

    public int Count => _items.Length;

    public T this[int index] => _items[index];

    public bool Equals(ValueList<T>? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return _items.AsSpan().SequenceEqual(other._items);
    }

    public override bool Equals(object? obj) => obj is ValueList<T> other && Equals(other);

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

public static class ValueList
{
    public static ValueList<T> Of<T>(params T[] items) => new(items);
}
