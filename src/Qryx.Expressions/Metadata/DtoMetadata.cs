namespace Qryx.Expressions.Metadata;

/// <summary>
/// Cached inspection metadata for a DTO type.
/// </summary>
public sealed class DtoMetadata
{
    public required Type DtoType { get; init; }
    public required Func<object> Factory { get; init; }
    public required IReadOnlyList<FilterPropertyMetadata> Filters { get; init; }
    public IReadOnlySet<string> SortableProperties { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, Action<object, object?>> StandardSetters { get; init; } = new Dictionary<string, Action<object, object?>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>False when the DTO is marked with <c>[NoTotal]</c>: <c>includeTotal</c> is ignored.</summary>
    public bool AllowTotal { get; init; } = true;
}
