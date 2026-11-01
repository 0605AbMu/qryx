using Qryx.Core.Enums;

namespace Qryx.Core.Attributes;

/// <summary>
/// Declarative attribute mapping a DTO property to entity property/properties with a filter operator.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class FilterAttribute : Attribute
{
    public Op Operator { get; }

    /// <summary>
    /// Single target property path on the Entity (e.g. "Department.Name" or nameof(User.Age)).
    /// If neither TargetProperty nor TargetProperties is specified, defaults to the DTO property name.
    /// </summary>
    public string? TargetProperty { get; init; }

    /// <summary>
    /// Multiple target properties on the Entity (e.g. [nameof(User.FirstName), nameof(User.LastName)]).
    /// Conditions for these columns will be combined with logical OR (Expression.OrElse),
    /// ensuring database B-Tree index friendliness for computed/composite fields.
    /// </summary>
    public string[]? TargetProperties { get; init; }

    public bool CaseSensitive { get; init; } = false;

    public FilterAttribute(Op @operator)
    {
        Operator = @operator;
    }

    public FilterAttribute(Op @operator, string targetProperty)
    {
        Operator = @operator;
        TargetProperty = targetProperty;
    }

    public FilterAttribute(string targetProperty, Op @operator)
    {
        TargetProperty = targetProperty;
        Operator = @operator;
    }
}
