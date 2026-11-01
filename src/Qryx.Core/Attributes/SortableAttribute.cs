namespace Qryx.Core.Attributes;

/// <summary>
/// Explicitly permits sorting on an entity or DTO property.
/// Implements secure-by-default Opt-In access control.
/// Sortable attribute is the single source of truth for sorting:
/// only properties marked with this attribute can be sorted by external client queries.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class SortableAttribute : Attribute
{
    /// <summary>
    /// Optional target property name on the underlying entity (if different from the DTO property name).
    /// </summary>
    public string? TargetProperty { get; }

    public SortableAttribute()
    {
    }

    public SortableAttribute(string targetProperty)
    {
        TargetProperty = targetProperty;
    }
}
