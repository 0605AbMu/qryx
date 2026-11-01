using System.Globalization;

namespace Qryx.Core.Types;

/// <summary>
/// A List&lt;T&gt; implementation that supports IParsable for ASP.NET Core Minimal API query string binding.
/// Accepts comma-separated values (e.g. "Active,Pending") or single values.
/// </summary>
/// <typeparam name="T">Element type.</typeparam>
public class QryxList<T> : List<T>, IParsable<QryxList<T>>
{
    public QryxList() { }

    public QryxList(IEnumerable<T> collection) : base(collection) { }

    public static QryxList<T> Parse(string s, IFormatProvider? provider = null)
    {
        if (TryParse(s, provider, out var result))
            return result;

        throw new FormatException($"String '{s}' was not recognized as a valid QryxList<{typeof(T).Name}>.");
    }

    public static bool TryParse(string? s, IFormatProvider? provider, out QryxList<T> result)
    {
        result = [];
        if (string.IsNullOrWhiteSpace(s))
            return true;

        try
        {
            var parts = s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var elementType = typeof(T);
            var underlying = Nullable.GetUnderlyingType(elementType) ?? elementType;

            foreach (var part in parts)
            {
                if (underlying.IsEnum)
                {
                    result.Add((T)Enum.Parse(underlying, part, ignoreCase: true));
                }
                else if (underlying == typeof(Guid))
                {
                    result.Add((T)(object)Guid.Parse(part));
                }
                else
                {
                    result.Add((T)Convert.ChangeType(part, underlying, provider ?? CultureInfo.InvariantCulture));
                }
            }

            return true;
        }
        catch
        {
            result = [];
            return false;
        }
    }

    public static bool TryParse(string? s, out QryxList<T> result) => TryParse(s, null, out result);
}
