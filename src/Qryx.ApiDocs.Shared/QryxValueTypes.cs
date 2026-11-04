using System.Collections;
using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using Qryx.Core.Types;

namespace Qryx.ApiDocs.Internal;

/// <summary>How a DTO property is written in the query string.</summary>
internal enum QryxValueShape
{
    /// <summary>A single value (<c>?age=30</c>).</summary>
    Scalar,

    /// <summary><see cref="Range{T}"/>: one string <c>from..to</c>, <c>from..</c> or <c>..to</c>.</summary>
    Range,

    /// <summary><see cref="QryxList{T}"/>: comma-separated values in one key (<c>?status=Active,Pending</c>).</summary>
    CommaSeparatedList,

    /// <summary>Arrays and other collections: the key is repeated (<c>?ids=1&amp;ids=2</c>).</summary>
    RepeatedKeys
}

/// <summary>
/// Maps CLR types of Qryx DTO properties to inline OpenAPI schemas and short human-readable format hints.
/// Shared (linked source) by Qryx.Swashbuckle and Qryx.OpenApi.
/// </summary>
internal static class QryxValueTypes
{
    public static Type Unwrap(Type type) => Nullable.GetUnderlyingType(type) ?? type;

    /// <summary>Classifies <paramref name="propertyType"/> and returns its element (or range bound) type.</summary>
    public static QryxValueShape GetShape(Type propertyType, out Type elementType)
    {
        var type = Unwrap(propertyType);

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Range<>))
        {
            elementType = type.GetGenericArguments()[0];
            return QryxValueShape.Range;
        }

        for (var current = type; current != null && current != typeof(object); current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(QryxList<>))
            {
                elementType = current.GetGenericArguments()[0];
                return QryxValueShape.CommaSeparatedList;
            }
        }

        if (type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type))
        {
            elementType = GetEnumerableElementType(type);
            return QryxValueShape.RepeatedKeys;
        }

        elementType = type;
        return QryxValueShape.Scalar;
    }

    /// <summary>Creates an inline schema for a single (non-collection) value of <paramref name="type"/>.</summary>
    public static OpenApiSchema CreateScalarSchema(Type type)
    {
        type = Unwrap(type);

        if (type.IsEnum)
        {
            var names = Enum.GetNames(type);
            var values = new List<JsonNode>(names.Length);
            foreach (var name in names)
                values.Add(JsonValue.Create(name));

            return new OpenApiSchema { Type = JsonSchemaType.String, Enum = values };
        }

        return Type.GetTypeCode(type) switch
        {
            TypeCode.Boolean => new OpenApiSchema { Type = JsonSchemaType.Boolean },
            TypeCode.Byte or TypeCode.SByte or TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Int32 =>
                new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int32" },
            TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64 =>
                new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int64" },
            TypeCode.Single => new OpenApiSchema { Type = JsonSchemaType.Number, Format = "float" },
            TypeCode.Double or TypeCode.Decimal => new OpenApiSchema { Type = JsonSchemaType.Number, Format = "double" },
            TypeCode.Char => new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 1, MaxLength = 1 },
            TypeCode.DateTime => new OpenApiSchema { Type = JsonSchemaType.String, Format = "date-time" },
            _ when type == typeof(DateTimeOffset) => new OpenApiSchema { Type = JsonSchemaType.String, Format = "date-time" },
            _ when type == typeof(DateOnly) => new OpenApiSchema { Type = JsonSchemaType.String, Format = "date" },
            _ when type == typeof(TimeOnly) => new OpenApiSchema { Type = JsonSchemaType.String, Format = "time" },
            _ when type == typeof(Guid) => new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" },
            _ => new OpenApiSchema { Type = JsonSchemaType.String }
        };
    }

    /// <summary>Short name of a value type for descriptions (int, decimal, date, date-time ...).</summary>
    public static string DescribeType(Type type)
    {
        type = Unwrap(type);

        if (type.IsEnum)
            return type.Name;

        return Type.GetTypeCode(type) switch
        {
            TypeCode.Byte or TypeCode.SByte or TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Int32 or TypeCode.UInt32 => "int",
            TypeCode.Int64 or TypeCode.UInt64 => "long",
            TypeCode.Single or TypeCode.Double => "number",
            TypeCode.Decimal => "decimal",
            TypeCode.DateTime => "date-time",
            TypeCode.String => "string",
            _ when type == typeof(DateTimeOffset) => "date-time",
            _ when type == typeof(DateOnly) => "date",
            _ when type == typeof(TimeOnly) => "time",
            _ when type == typeof(TimeSpan) => "duration",
            _ when type == typeof(Guid) => "uuid",
            _ => type.Name
        };
    }

    /// <summary>Two sample values of <paramref name="type"/> (ascending) used in examples.</summary>
    /// <param name="type">Value type.</param>
    /// <param name="forRange">True for range bounds (<c>20..35</c>), false for list items (<c>1,2</c>).</param>
    public static (string Low, string High) GetSampleValues(Type type, bool forRange = false)
    {
        type = Unwrap(type);

        if (type.IsEnum)
        {
            var names = Enum.GetNames(type);
            return names.Length switch
            {
                0 => ("A", "B"),
                1 => (names[0], names[0]),
                _ => (names[0], names[1])
            };
        }

        return Type.GetTypeCode(type) switch
        {
            TypeCode.Single or TypeCode.Double or TypeCode.Decimal => ("10.5", "99.9"),
            TypeCode.DateTime => ("2024-01-01", "2024-12-31"),
            TypeCode.String => ("a", "b"),
            _ when type == typeof(DateTimeOffset) || type == typeof(DateOnly) => ("2024-01-01", "2024-12-31"),
            _ when type == typeof(TimeOnly) => ("08:00", "17:30"),
            _ when type == typeof(TimeSpan) => ("00:30:00", "02:00:00"),
            _ when type == typeof(Guid) => ("3fa85f64-5717-4562-b3fc-2c963f66afa6", "9b2d1c7e-0f4a-4b8e-a1d2-6c5e7f8a9b0c"),
            _ when IsInteger(type) => forRange ? ("20", "35") : ("1", "2"),
            _ => ("from", "to")
        };
    }

    private static bool IsInteger(Type type) => Type.GetTypeCode(type) is
        TypeCode.Byte or TypeCode.SByte or TypeCode.Int16 or TypeCode.UInt16 or
        TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64;

    private static Type GetEnumerableElementType(Type type)
    {
        if (type.IsArray)
            return type.GetElementType()!;

        if (type.IsGenericType && type.GetGenericArguments() is [var single])
            return single;

        foreach (var iface in type.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return iface.GetGenericArguments()[0];
        }

        return typeof(string);
    }
}
