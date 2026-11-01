namespace Qryx.Expressions.Metadata;

/// <summary>
/// Cached inspection metadata for a DTO type.
/// </summary>
public sealed class DtoMetadata
{
    public required Type DtoType { get; init; }
    public required IReadOnlyList<FilterPropertyMetadata> Filters { get; init; }
    public IReadOnlySet<string> SortableProperties { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, Action<object, object?>> StandardSetters { get; init; } = new Dictionary<string, Action<object, object?>>(StringComparer.OrdinalIgnoreCase);
}
