using Qryx.Core.Constants;

namespace Qryx.Expressions.Options;

/// <summary>
/// Configuration options for Qryx query engine execution.
/// </summary>
public sealed class QryxOptions
{
    private int _maxPageSize = QryxConstants.DefaultMaxPageSize;
    private int _defaultPageSize = QryxConstants.DefaultPageSize;

    /// <summary>
    /// Process-wide options used by the static builders and EF Core extensions when no explicit value is passed.
    /// <c>services.AddQryx(o => ...)</c> (Qryx.AspNetCore) replaces it with the configured instance.
    /// </summary>
    public static QryxOptions Global { get; set; } = new();

    /// <summary>Upper bound for PageSize (DoS guard). Must be at least 1.</summary>
    public int MaxPageSize
    {
        get => _maxPageSize;
        set => _maxPageSize = value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), "MaxPageSize must be at least 1.");
    }

    /// <summary>PageSize used when the request omits it or sends a non-positive value. Must be at least 1.</summary>
    public int DefaultPageSize
    {
        get => _defaultPageSize;
        set => _defaultPageSize = value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), "DefaultPageSize must be at least 1.");
    }

    private int? _maxOffset = QryxConstants.DefaultMaxOffset;

    /// <summary>
    /// DoS guard for offset pagination: the largest allowed OFFSET, i.e. <c>(Page - 1) * PageSize</c>.
    /// A database must walk (and discard) every skipped row, so <c>?page=2000000</c> costs a full index/table walk
    /// on every request. Deeper pages throw <c>QryxRequestException</c> (HTTP 400) that points the client to
    /// keyset pagination (<c>afterCursor</c>). Default: 10,000. <c>null</c> disables the limit.
    /// </summary>
    public int? MaxOffset
    {
        get => _maxOffset;
        set => _maxOffset = value is null or >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), "MaxOffset must be null or non-negative.");
    }

    /// <summary>
    /// When true, an unknown or non-[Sortable] SortBy field results in a <c>QryxRequestException</c> (HTTP 400)
    /// instead of being silently ignored.
    /// </summary>
    public bool ThrowOnInvalidSortProperty { get; set; } = false;

    /// <summary>
    /// When true, attaches EF Core query tags (.TagWith($"Qryx: {Dto}")) for SQL tracing.
    /// Default is false to eliminate string allocations and SQL comment parsing overhead in high-throughput hot paths.
    /// </summary>
    public static bool EnableQueryTagging { get; set; } = false;

    /// <summary>Clamps a requested page size to [1, MaxPageSize], using DefaultPageSize for non-positive values.</summary>
    public int ClampPageSize(int pageSize) => pageSize <= 0 ? Math.Min(DefaultPageSize, MaxPageSize) : Math.Min(pageSize, MaxPageSize);
}
