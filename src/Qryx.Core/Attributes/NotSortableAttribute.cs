namespace Qryx.Core.Attributes;

/// <summary>
/// Prevents an entity property from being used in dynamic sorting (OrderBy / SortBy).
/// Protects against information disclosure, side-channel attacks, or sorting on unindexed/sensitive columns.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class NotSortableAttribute : Attribute
{
}
