using System.Reflection;
using Qryx.Core.Enums;

namespace Qryx.Expressions.Metadata;

/// <summary>
/// Pre-analyzed metadata and compiled fast accessors for a [Filter] decorated property on a DTO.
/// </summary>
public sealed class FilterPropertyMetadata
{
    public required PropertyInfo DtoProperty { get; init; }
    public required string DtoPropertyName { get; init; }
    public required Type DtoPropertyType { get; init; }

    /// <summary>
    /// Target entity property paths (e.g., ["Age"], ["Department.Name"], or ["FirstName", "LastName"]).
    /// Multiple paths are combined via logical OR (Expression.OrElse).
    /// </summary>
    public required IReadOnlyList<string> TargetPropertyPaths { get; init; }

    public required Op Operator { get; init; }
    public required bool Invariant { get; init; }

    /// <summary>Compiled fast getter: (dtoInstance) => propertyValue</summary>
    public required Func<object, object?> Getter { get; init; }

    /// <summary>Compiled fast setter: (dtoInstance, value) => dtoInstance.Property = value</summary>
    public Action<object, object?>? Setter { get; init; }

    public required bool IsCollection { get; init; }
    public Type? CollectionElementType { get; init; }
}
