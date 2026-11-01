using System.Text.Json.Serialization;

namespace Qryx.EntityFramework.Models;

/// <summary>
/// Compact pagination result envelope containing only items, total, and hasNext.
/// </summary>
/// <typeparam name="T">Item element type.</typeparam>
public record PagedResult<T>
{
    public IReadOnlyList<T> Items { get; init; } = [];
    public int Total { get; init; }
    public bool HasNext => PageSize > 0 && Page < TotalPages;

    [JsonIgnore]
    public int Page { get; init; }

    [JsonIgnore]
    public int PageSize { get; init; }

    [JsonIgnore]
    public int TotalCount => Total;

    [JsonIgnore]
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling(Total / (double)PageSize) : 0;

    [JsonIgnore]
    public bool HasPrevious => Page > 1;
}
