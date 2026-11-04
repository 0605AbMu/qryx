using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Qryx.SourceGenerator;

/// <summary>
/// Immutable array with structural (element-wise) equality, so pipeline models that contain collections
/// compare by value and the incremental generator can cache unchanged steps.
/// </summary>
/// <typeparam name="T">Element type; must implement value equality.</typeparam>
internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IReadOnlyList<T>
    where T : IEquatable<T>
{
    public static readonly EquatableArray<T> Empty = new(ImmutableArray<T>.Empty);

    private readonly ImmutableArray<T> _items;

    public EquatableArray(ImmutableArray<T> items)
    {
        _items = items.IsDefault ? ImmutableArray<T>.Empty : items;
    }

    public EquatableArray(IEnumerable<T> items)
        : this(ImmutableArray.CreateRange(items))
    {
    }

    private ImmutableArray<T> Items => _items.IsDefault ? ImmutableArray<T>.Empty : _items;

    public int Count => Items.Length;

    public T this[int index] => Items[index];

    public bool IsEmpty => Items.Length == 0;

    public bool Equals(EquatableArray<T> other)
    {
        var left = Items;
        var right = other.Items;
        if (left.Length != right.Length)
            return false;

        for (int i = 0; i < left.Length; i++)
        {
            if (!EqualityComparer<T>.Default.Equals(left[i], right[i]))
                return false;
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 17;
            foreach (var item in Items)
            {
                hash = (hash * 31) + (item is null ? 0 : EqualityComparer<T>.Default.GetHashCode(item));
            }

            return hash;
        }
    }

    public ImmutableArray<T>.Enumerator GetEnumerator() => Items.GetEnumerator();

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => ((IEnumerable<T>)Items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)Items).GetEnumerator();

    public static bool operator ==(EquatableArray<T> left, EquatableArray<T> right) => left.Equals(right);

    public static bool operator !=(EquatableArray<T> left, EquatableArray<T> right) => !left.Equals(right);
}

/// <summary>Factory helpers for <see cref="EquatableArray{T}"/>.</summary>
internal static class EquatableArray
{
    public static EquatableArray<T> ToEquatableArray<T>(this IEnumerable<T> items)
        where T : IEquatable<T> => new(items);
}
