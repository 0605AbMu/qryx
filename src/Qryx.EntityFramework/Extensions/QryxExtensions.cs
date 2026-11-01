using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Qryx.Core.Constants;
using Qryx.Core.Contracts;
using Qryx.EntityFramework.Models;
using Qryx.Expressions.Builders;

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
    /// </summary>
    public static async Task<PagedResult<TEntity>> ToPagedResultAsync<TEntity>(
        this IQueryable<TEntity> source,
        IQryxRequest<TEntity> request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);

        // DoS Guard: clamp pageSize and page
        var page = PaginationBuilder.ClampPage(request.Page);
        var pageSize = PaginationBuilder.ClampPageSize(request.PageSize, QryxConstants.DefaultMaxPageSize);

        // 1. Filtr va saralashni qo'llash
        var filteredQuery = source.ApplyQryx(request);

        // 2. Jami qatorlar sonini olish
        var totalCount = await filteredQuery.CountAsync(ct).ConfigureAwait(false);

        // 3. Sahifalashni qo'llash va ma'lumotni yuklash
        var items = await filteredQuery
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new PagedResult<TEntity>
        {
            Items = items,
            Total = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    /// <summary>
    /// Applies filters, sorting, projection mapping, and executes pagination asynchronously.
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

        var page = PaginationBuilder.ClampPage(request.Page);
        var pageSize = PaginationBuilder.ClampPageSize(request.PageSize, QryxConstants.DefaultMaxPageSize);

        var filteredQuery = source.ApplyQryx(request);
        var totalCount = await filteredQuery.CountAsync(ct).ConfigureAwait(false);

        var items = await filteredQuery
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(selector)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new PagedResult<TResult>
        {
            Items = items,
            Total = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }
}
