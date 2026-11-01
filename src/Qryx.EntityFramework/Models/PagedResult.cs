using System.Text.Json.Serialization;

namespace Qryx.EntityFramework.Models;

/// <summary>
/// Compact pagination result envelope containing items, optional total, hasNext, and nextCursor.
/// </summary>
/// <typeparam name="T">Item element type.</typeparam>
public record PagedResult<T>
{
    private readonly bool? _hasNext;

    public IReadOnlyList<T> Items { get; init; } = [];

    /// <summary>
    /// Total count of matching items. Null when IncludeTotalCount = false or keyset pagination is used.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Total { get; init; }

    /// <summary>
    /// Whether there are more items on subsequent pages or cursors.
    /// </summary>
    public bool HasNext
    {
        get => _hasNext ?? (Total.HasValue && PageSize > 0 && Page < TotalPages);
        init => _hasNext = value;
    }

    /// <summary>
    /// Keyset cursor value for the next page, if available.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? NextCursor { get; init; }

    [JsonIgnore]
    public int Page { get; init; }

    [JsonIgnore]
    public int PageSize { get; init; }

    [JsonIgnore]
    public int TotalCount => Total ?? 0;

    [JsonIgnore]
    public int TotalPages => Total.HasValue && PageSize > 0 ? (int)Math.Ceiling(Total.Value / (double)PageSize) : 0;

    [JsonIgnore]
    public bool HasPrevious => Page > 1;
}
