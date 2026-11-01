using Qryx.Core.Constants;

namespace Qryx.Expressions.Options;

/// <summary>
/// Configuration options for Qryx query engine execution.
/// </summary>
public sealed class QryxOptions
{
    public int MaxPageSize { get; set; } = QryxConstants.DefaultMaxPageSize;
    public int DefaultPageSize { get; set; } = QryxConstants.DefaultPageSize;
    public bool ThrowOnInvalidSortProperty { get; set; } = false;

    /// <summary>
    /// When true, attaches EF Core query tags (.TagWith($"Qryx: {Dto}")) for SQL tracing.
    /// Default is false to eliminate string allocations and SQL comment parsing overhead in high-throughput hot paths.
    /// </summary>
    public static bool EnableQueryTagging { get; set; } = false;
}
