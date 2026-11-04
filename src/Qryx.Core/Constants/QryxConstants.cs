namespace Qryx.Core.Constants;

public static class QryxConstants
{
    public const int DefaultPage = 1;
    public const int DefaultPageSize = 20;
    public const int DefaultMaxPageSize = 100;
    public const int DefaultMaxInCollectionSize = 1000;
    /// <summary>Largest allowed OFFSET for offset pagination (same default window as Elasticsearch max_result_window).</summary>
    public const int DefaultMaxOffset = 10_000;
    public const int MaxSortByLength = 256;
    public const int MaxSortClauses = 5;
}
