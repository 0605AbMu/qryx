namespace Qryx.Core.Contracts;

/// <summary>
/// Contract for query requests targeting an entity type.
/// Encapsulates pagination, cursor, and sorting parameters.
/// </summary>
/// <typeparam name="TEntity">Target entity type.</typeparam>
public interface IQryxRequest<TEntity>
{
    int Page { get; init; }
    int PageSize { get; init; }
    object? AfterCursor { get; init; }
    string? SortBy { get; init; }
    bool IsDescending { get; init; }
}

/// <summary>
/// Base record for Qryx query requests.
/// Properties are nullable to support optional query string binding in Minimal APIs
/// while defaulting to standard values (Page=1, PageSize=20, IsDescending=false).
/// </summary>
/// <typeparam name="TEntity">Target entity type.</typeparam>
public abstract record QryxRequest<TEntity> : IQryxRequest<TEntity>
{
    public int? Page { get; init; } = 1;
    public int? PageSize { get; init; } = 20;
    public string? AfterCursor { get; init; }
    public string? SortBy { get; init; }
    public bool? IsDescending { get; init; } = false;

    int IQryxRequest<TEntity>.Page
    {
        get => Page ?? 1;
        init => Page = value;
    }

    int IQryxRequest<TEntity>.PageSize
    {
        get => PageSize ?? 20;
        init => PageSize = value;
    }

    object? IQryxRequest<TEntity>.AfterCursor
    {
        get => AfterCursor;
        init => AfterCursor = value?.ToString();
    }

    bool IQryxRequest<TEntity>.IsDescending
    {
        get => IsDescending ?? false;
        init => IsDescending = value;
    }
}
