using System.Text.Json.Serialization;

namespace Qryx.EntityFramework.Models;

/// <summary>
/// Pagination envelope: <c>{ "items": [...], "hasNext": true, "nextCursor": "...", "total": 42 }</c>.
/// <list type="bullet">
/// <item><c>nextCursor</c> is returned whenever a next page exists and the sort allows keyset pagination
/// (every page, offset or keyset); pass it back unchanged as <c>afterCursor</c>.</item>
/// <item><c>total</c> is returned only when the request asked for it (<c>includeTotal=true</c>) and the DTO is
/// not marked <c>[NoTotal]</c>.</item>
/// </list>
/// </summary>
/// <typeparam name="T">Item element type.</typeparam>
public record PagedResult<T>
{
    public IReadOnlyList<T> Items { get; init; } = [];

    /// <summary>Whether more rows follow this page.</summary>
    public bool HasNext { get; init; }

    /// <summary>Opaque keyset cursor for the next page (send it back as <c>afterCursor</c>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NextCursor { get; init; }

    /// <summary>Total number of matching rows; only when requested with <c>includeTotal=true</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Total { get; init; }
}
