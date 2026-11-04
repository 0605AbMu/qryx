using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using Qryx.Core.Types;

namespace Qryx.AspNetCore.Internal;

/// <summary>Parses a raw query string value into a boxed value of a specific type.</summary>
internal delegate bool QryxValueParserDelegate(string value, out object? result);

/// <summary>
/// Cached, compiled per-type parsers for query string values. Every parser uses the invariant culture.
/// Supported: <see cref="string"/>, enums (case-insensitive names or defined numeric values),
/// <see cref="bool"/>, any <see cref="IParsable{TSelf}"/> (numbers, <see cref="Guid"/>, <see cref="DateTime"/>,
/// <see cref="DateTimeOffset"/>, <see cref="DateOnly"/>, <see cref="TimeOnly"/>, <see cref="TimeSpan"/>,
/// <see cref="Range{T}"/>, ...), <see cref="Nullable{T}"/> wrappers of those, and as a last resort
/// a <see cref="TypeConverter"/> that converts from string.
/// </summary>
internal static class QryxValueParser
{
    private const int MaxEchoedValueLength = 64;

    private static readonly ConcurrentDictionary<Type, QryxValueParserDelegate?> Cache = new();

    private static readonly QryxValueParserDelegate StringParser = static (string value, out object? result) =>
    {
        result = value;
        return true;
    };

    private static readonly QryxValueParserDelegate BoolParser = static (string value, out object? result) =>
    {
        if (bool.TryParse(value.AsSpan().Trim(), out var parsed))
        {
            result = parsed;
            return true;
        }

        result = null;
        return false;
    };

    /// <summary>Returns the cached parser for <paramref name="targetType"/>, or null when the type cannot be parsed from a string.</summary>
    public static QryxValueParserDelegate? GetParser(Type targetType)
    {
        ArgumentNullException.ThrowIfNull(targetType);
        return Cache.GetOrAdd(targetType, static type => Create(type));
    }

    /// <summary>Builds the client-facing error message for an unparsable value.</summary>
    public static string FormatInvalidValue(string key, string value, Type targetType)
    {
        var shown = value.Length > MaxEchoedValueLength
            ? string.Concat(value.AsSpan(0, MaxEchoedValueLength), "...")
            : value;

        return $"Value '{shown}' is not valid for '{key}'. Expected {DescribeExpected(targetType)}.";
    }

    /// <summary>Human-readable description of the accepted format of <paramref name="targetType"/>.</summary>
    public static string DescribeExpected(Type targetType)
    {
        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (underlying.IsEnum)
            return $"one of: {string.Join(", ", Enum.GetNames(underlying))}";

        if (underlying.IsGenericType && underlying.GetGenericTypeDefinition() == typeof(Range<>))
        {
            var bound = underlying.GetGenericArguments()[0];
            return $"a range 'from..to', 'from..' or '..to' of {DescribeExpected(bound)}";
        }

        return underlying.IsGenericType
            ? underlying.Name[..underlying.Name.IndexOf('`')]
            : underlying.Name;
    }

    private static QryxValueParserDelegate? Create(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (underlying == typeof(string) || underlying == typeof(object))
            return StringParser;

        if (underlying == typeof(bool))
            return BoolParser;

        if (underlying.IsEnum)
            return CreateGeneric(nameof(ParseEnum), underlying);

        if (IsSelfParsable(underlying))
        {
            var parser = CreateGeneric(nameof(ParseParsable), underlying);
            if (parser != null)
                return parser;
        }

        var converter = TypeDescriptor.GetConverter(underlying);
        if (converter.CanConvertFrom(typeof(string)))
        {
            return (string value, out object? result) =>
            {
                try
                {
                    result = converter.ConvertFromString(null, CultureInfo.InvariantCulture, value.Trim());
                    return result != null;
                }
                catch (Exception ex) when (ex is FormatException or ArgumentException or NotSupportedException or OverflowException or InvalidCastException)
                {
                    result = null;
                    return false;
                }
            };
        }

        return null;
    }

    private static bool IsSelfParsable(Type type)
    {
        foreach (var iface in type.GetInterfaces())
        {
            if (iface.IsGenericType &&
                iface.GetGenericTypeDefinition() == typeof(IParsable<>) &&
                iface.GenericTypeArguments[0] == type)
            {
                return true;
            }
        }

        return false;
    }

    private static QryxValueParserDelegate? CreateGeneric(string methodName, Type typeArgument)
    {
        try
        {
            return typeof(QryxValueParser)
                .GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(typeArgument)
                .CreateDelegate<QryxValueParserDelegate>();
        }
        catch (ArgumentException)
        {
            // Generic constraint not satisfied -> caller falls back to the next strategy
            return null;
        }
    }

    private static bool ParseEnum<TEnum>(string value, out object? result)
        where TEnum : struct, Enum
    {
        // Enum.TryParse reads "Active,Blocked" as the flags combination Active|Blocked; for a non-[Flags] enum
        // that silently becomes a different single value (e.g. Blocked), so comma-separated input is rejected.
        if (!EnumInfo<TEnum>.IsFlags && value.Contains(','))
        {
            result = null;
            return false;
        }

        if (Enum.TryParse<TEnum>(value.AsSpan().Trim(), ignoreCase: true, out var parsed) &&
            (EnumInfo<TEnum>.IsFlags || Enum.IsDefined(parsed)))
        {
            result = parsed;
            return true;
        }

        result = null;
        return false;
    }

    private static bool ParseParsable<T>(string value, out object? result)
        where T : IParsable<T>
    {
        if (T.TryParse(value.Trim(), CultureInfo.InvariantCulture, out var parsed))
        {
            result = parsed;
            return true;
        }

        result = null;
        return false;
    }

    private static class EnumInfo<TEnum>
        where TEnum : struct, Enum
    {
        public static readonly bool IsFlags = typeof(TEnum).IsDefined(typeof(FlagsAttribute), inherit: false);
    }
}
