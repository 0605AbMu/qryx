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
}
