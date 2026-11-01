namespace Qryx.Core.Attributes;

/// <summary>
/// Explicitly permits sorting on an entity property, DTO property, or DTO class.
/// Implements secure-by-default Opt-in (Whitelist) access control.
/// Only properties marked with this attribute or specified in the whitelist can be sorted.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class SortableAttribute : Attribute
{
    public string[] AllowedProperties { get; }

    public SortableAttribute()
    {
        AllowedProperties = Array.Empty<string>();
    }

    public SortableAttribute(params string[] allowedProperties)
    {
        AllowedProperties = allowedProperties ?? Array.Empty<string>();
    }
}
