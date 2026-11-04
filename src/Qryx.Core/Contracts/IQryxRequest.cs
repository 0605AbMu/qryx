namespace Qryx.Core.Contracts;

/// <summary>
/// Non-generic base contract for query requests.
/// Allows zero-reflection polymorphic binding, inspection, and filter execution.
/// </summary>
public interface IQryxRequest
{
    /// <summary>1-based page number for offset pagination (ignored when <see cref="AfterCursor"/> is set).</summary>
    int Page { get; }

    /// <summary>Requested page size (clamped to <c>QryxOptions.MaxPageSize</c>).</summary>
    int PageSize { get; }

    /// <summary>Opaque keyset cursor from a previous response's <c>nextCursor</c>; switches to keyset pagination.</summary>
    object? AfterCursor { get; }

    /// <summary>Comma-separated sort clauses, e.g. <c>"createdAt desc, name"</c> (only [Sortable] properties).</summary>
    string? SortBy { get; }

    /// <summary>Default direction for sort clauses without an explicit <c>asc</c>/<c>desc</c>.</summary>
    bool IsDescending { get; }

    /// <summary>
    /// When true, the response also carries the total number of matching rows (<c>total</c>).
    /// Off by default: counting is the most expensive part of paging. Ignored for DTOs marked with <c>[NoTotal]</c>.
    /// </summary>
    bool IncludeTotal => false;
}

/// <summary>
/// Contract for query requests targeting an entity type.
/// Encapsulates pagination, cursor, and sorting parameters.
/// </summary>
/// <typeparam name="TEntity">Target entity type.</typeparam>
public interface IQryxRequest<TEntity> : IQryxRequest
{
    new int Page { get; init; }
    new int PageSize { get; init; }
    new object? AfterCursor { get; init; }
    new string? SortBy { get; init; }
    new bool IsDescending { get; init; }
    new bool IncludeTotal => false;

    int IQryxRequest.Page => Page;
    int IQryxRequest.PageSize => PageSize;
    object? IQryxRequest.AfterCursor => AfterCursor;
    string? IQryxRequest.SortBy => SortBy;
    bool IQryxRequest.IsDescending => IsDescending;
    bool IQryxRequest.IncludeTotal => IncludeTotal;
}

/// <summary>
/// Base record for Qryx query requests.
/// Properties are nullable so every query string parameter is optional in Minimal APIs and OpenAPI
/// while defaulting to standard values (Page=1, PageSize=20, IsDescending=false, IncludeTotal=false).
/// </summary>
/// <typeparam name="TEntity">Target entity type.</typeparam>
public abstract record QryxRequest<TEntity> : IQryxRequest<TEntity>
{
    /// <summary>1-based page number for offset pagination. Default: 1.</summary>
    public int? Page { get; init; } = 1;

    /// <summary>Items per page. Default: 20, clamped to <c>QryxOptions.MaxPageSize</c>.</summary>
    public int? PageSize { get; init; } = 20;

    /// <summary>Opaque cursor from the previous response's <c>nextCursor</c> (keyset pagination).</summary>
    public string? AfterCursor { get; init; }

    /// <summary>Sort clauses, e.g. <c>"createdAt desc, name"</c>. Only [Sortable] properties are allowed.</summary>
    public string? SortBy { get; init; }

    /// <summary>Default sort direction for clauses without <c>asc</c>/<c>desc</c>. Default: false.</summary>
    public bool? IsDescending { get; init; } = false;

    /// <summary>Also return the total number of matching rows. Default: false.</summary>
    public bool? IncludeTotal { get; init; }

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
        // Bo'sh "?afterCursor=" keyset rejimini yoqmaydi (offset sahifalash ishlaydi)
        get => string.IsNullOrWhiteSpace(AfterCursor) ? null : AfterCursor;
        init => AfterCursor = value?.ToString();
    }

    bool IQryxRequest<TEntity>.IsDescending
    {
        get => IsDescending ?? false;
        init => IsDescending = value;
    }

    bool IQryxRequest<TEntity>.IncludeTotal => IncludeTotal ?? false;
}
