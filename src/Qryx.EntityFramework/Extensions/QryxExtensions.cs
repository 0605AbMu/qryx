using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Qryx.Core.Constants;
using Qryx.Core.Contracts;
using Qryx.EntityFramework.Models;
using Qryx.Expressions.Builders;
using Qryx.Expressions.Metadata;

namespace Qryx.EntityFramework.Extensions;

/// <summary>
/// Entity Framework Core extension methods for Qryx query pipeline.
/// </summary>
public static class QryxExtensions
{
    /// <summary>
    /// Applies declarative filters and sorting from the request to the EF Core queryable.
    /// </summary>
    public static IQueryable<TEntity> ApplyQryx<TEntity>(
        this IQueryable<TEntity> source,
        IQryxRequest<TEntity> request)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);

        source = FilterBuilder.Apply(source, request);
        source = SortBuilder.Apply(source, request);
        return source;
    }

    /// <summary>
    /// Applies filters, sorting, DoS guard clamping, and executes pagination asynchronously.
    /// Automatically optimizes between:
    /// 1. Keyset / Cursor Pagination (when AfterCursor is provided - 0 count queries, O(log N) seek)
    /// 2. Single-Query Pagination (when IncludeTotalCount = false - 1 roundtrip, pageSize + 1 items)
    /// 3. Standard Total-Count Pagination (when IncludeTotalCount = true - 2 roundtrips)
    /// </summary>
    public static async Task<PagedResult<TEntity>> ToPagedResultAsync<TEntity>(
        this IQueryable<TEntity> source,
        IQryxRequest<TEntity> request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);

        var pageSize = PaginationBuilder.ClampPageSize(request.PageSize, QryxConstants.DefaultMaxPageSize);

        // 1. Keyset (Cursor-based) Pagination: 0 COUNT queries, direct index seek
        if (request.AfterCursor != null)
        {
            var filtered = FilterBuilder.Apply(source, request);
            var query = PaginationBuilder.ApplyKeyset(
                filtered,
                request.AfterCursor,
                pageSize + 1,
                request.CursorProperty,
                request.IsDescending);

            var items = await query.ToListAsync(ct).ConfigureAwait(false);
            var hasNext = items.Count > pageSize;
            if (hasNext)
            {
                items.RemoveAt(pageSize);
            }

            object? nextCursor = null;
            if (items.Count > 0)
            {
                var cursorProp = request.CursorProperty
                                 ?? MetadataCache.GetEntityMetadata<TEntity>().PrimaryKeyProperty?.Name
                                 ?? "Id";
                var propInfo = MetadataCache.GetEntityMetadata<TEntity>().ResolveProperty(cursorProp);
                nextCursor = propInfo?.GetValue(items[^1]);
            }

            return new PagedResult<TEntity>
            {
                Items = items,
                Total = null,
                Page = 1,
                PageSize = pageSize,
                HasNext = hasNext,
                NextCursor = nextCursor
            };
        }

        // 2. Single-Query Pagination (Skip COUNT(*) for 2x faster execution)
        var page = PaginationBuilder.ClampPage(request.Page);
        var filteredQuery = source.ApplyQryx(request);

        if (!request.IncludeTotalCount)
        {
            var items = await filteredQuery
                .Skip((page - 1) * pageSize)
                .Take(pageSize + 1)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            var hasNext = items.Count > pageSize;
            if (hasNext)
            {
                items.RemoveAt(pageSize);
            }

            return new PagedResult<TEntity>
            {
                Items = items,
                Total = null,
                Page = page,
                PageSize = pageSize,
                HasNext = hasNext
            };
        }

        // 3. Standard Pagination with Total Count
        var totalCount = await filteredQuery.CountAsync(ct).ConfigureAwait(false);
        var pagedItems = await filteredQuery
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new PagedResult<TEntity>
        {
            Items = pagedItems,
            Total = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    /// <summary>
    /// Applies filters, sorting, projection mapping, and executes pagination asynchronously.
    /// Pushes projection (selector) into SQL query to transfer only selected columns over network.
    /// </summary>
    public static async Task<PagedResult<TResult>> ToPagedResultAsync<TEntity, TResult>(
        this IQueryable<TEntity> source,
        IQryxRequest<TEntity> request,
        Expression<Func<TEntity, TResult>> selector,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(selector);

        var pageSize = PaginationBuilder.ClampPageSize(request.PageSize, QryxConstants.DefaultMaxPageSize);

        // 1. Keyset (Cursor-based) Pagination
        if (request.AfterCursor != null)
        {
            var filtered = FilterBuilder.Apply(source, request);
            var query = PaginationBuilder.ApplyKeyset(
                filtered,
                request.AfterCursor,
                pageSize + 1,
                request.CursorProperty,
                request.IsDescending);

            var items = await query.Select(selector).ToListAsync(ct).ConfigureAwait(false);
            var hasNext = items.Count > pageSize;
            if (hasNext)
            {
                items.RemoveAt(pageSize);
            }

            return new PagedResult<TResult>
            {
                Items = items,
                Total = null,
                Page = 1,
                PageSize = pageSize,
                HasNext = hasNext
            };
        }

        // 2. Single-Query Pagination
        var page = PaginationBuilder.ClampPage(request.Page);
        var filteredQuery = source.ApplyQryx(request);

        if (!request.IncludeTotalCount)
        {
            var items = await filteredQuery
                .Skip((page - 1) * pageSize)
                .Take(pageSize + 1)
                .Select(selector)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            var hasNext = items.Count > pageSize;
            if (hasNext)
            {
                items.RemoveAt(pageSize);
            }

            return new PagedResult<TResult>
            {
                Items = items,
                Total = null,
                Page = page,
                PageSize = pageSize,
                HasNext = hasNext
            };
        }

        // 3. Standard Pagination with Total Count
        var totalCount = await filteredQuery.CountAsync(ct).ConfigureAwait(false);
        var pagedItems = await filteredQuery
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(selector)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new PagedResult<TResult>
        {
            Items = pagedItems,
            Total = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }
}
