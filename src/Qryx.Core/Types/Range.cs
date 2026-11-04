using System.Globalization;

namespace Qryx.Core.Types;

/// <summary>
/// Non-generic, allocation-free view over <see cref="Range{T}"/> used by the query engine (Op.Between).
/// </summary>
public interface IRange
{
    /// <summary>Inclusive lower bound (boxed) or null when unbounded.</summary>
    object? From { get; }

    /// <summary>Inclusive upper bound (boxed) or null when unbounded.</summary>
    object? To { get; }

    bool HasValue { get; }
}

/// <summary>
/// Unified inclusive range value for a single field (dates, numbers, times) used with <c>Op.Between</c>.
/// Query string format: <c>from..to</c>, <c>from..</c> or <c>..to</c>
/// (e.g. <c>?age=20..35</c>, <c>?createdAt=2024-01-01..2024-12-31</c>).
/// Translates to index-friendly <c>column &gt;= @from AND column &lt;= @to</c>.
/// </summary>
/// <typeparam name="T">Comparable, parsable value type.</typeparam>
public readonly record struct Range<T> : IRange, IParsable<Range<T>>
    where T : struct, IComparable<T>, IParsable<T>
{
    public const string Separator = "..";

    public T? From { get; init; }
    public T? To { get; init; }

    public bool HasValue => From.HasValue || To.HasValue;

    object? IRange.From => From;
    object? IRange.To => To;

    public static Range<T> Parse(string s, IFormatProvider? provider)
    {
        if (TryParse(s, provider, out var result))
            return result;

        throw new FormatException($"'{s}' is not a valid range. Expected 'from..to', 'from..' or '..to'.");
    }

    public static bool TryParse(string? s, IFormatProvider? provider, out Range<T> result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(s))
            return true;

        var separatorIndex = s.IndexOf(Separator, StringComparison.Ordinal);
        if (separatorIndex < 0)
            return false;

        provider ??= CultureInfo.InvariantCulture;
        var fromText = s[..separatorIndex].Trim();
        var toText = s[(separatorIndex + Separator.Length)..].Trim();

        T? from = null;
        T? to = null;

        if (fromText.Length > 0)
        {
            if (!T.TryParse(fromText, provider, out var parsedFrom))
                return false;
            from = parsedFrom;
        }

        if (toText.Length > 0)
        {
            if (!T.TryParse(toText, provider, out var parsedTo))
                return false;
            to = parsedTo;
        }

        if (from.HasValue && to.HasValue && from.Value.CompareTo(to.Value) > 0)
            return false;

        result = new Range<T> { From = from, To = to };
        return true;
    }

    public static bool TryParse(string? s, out Range<T> result) => TryParse(s, null, out result);

    public override string ToString() => $"{From}{Separator}{To}";
}
