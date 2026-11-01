namespace Qryx.Expressions.Metadata;

/// <summary>
/// Cached inspection metadata for a DTO type.
/// </summary>
public sealed class DtoMetadata
{
    public required Type DtoType { get; init; }
    public required IReadOnlyList<FilterPropertyMetadata> Filters { get; init; }
    public IReadOnlyDictionary<string, Action<object, object?>> StandardSetters { get; init; } = new Dictionary<string, Action<object, object?>>(StringComparer.OrdinalIgnoreCase);
}
