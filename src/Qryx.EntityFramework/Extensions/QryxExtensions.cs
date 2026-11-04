using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Qryx.Core.Contracts;
using Qryx.Core.Exceptions;
using Qryx.EntityFramework.Internal;
using Qryx.EntityFramework.Models;
using Qryx.Expressions.Builders;
using Qryx.Expressions.Metadata;
using Qryx.Expressions.Options;

namespace Qryx.EntityFramework.Extensions;

/// <summary>
/// Entity Framework Core extension methods for the Qryx query pipeline.
/// </summary>
/// <remarks>
/// One request shape, no configuration:
/// <list type="bullet">
/// <item><c>?page=N</c> — offset page (limited by <see cref="QryxOptions.MaxOffset"/>).</item>
/// <item><c>?afterCursor=...</c> — keyset page continuing after the previous page's <c>nextCursor</c>.</item>
/// <item>Every page reads <c>pageSize + 1</c> rows in ONE query: <c>hasNext</c> without counting, and
/// <c>nextCursor</c> whenever a next page exists (so a client can start with offset and continue with keyset).</item>
/// <item><c>?includeTotal=true</c> — also return <c>total</c> (skipped when the offset page is the last one,
/// ignored for DTOs marked <c>[NoTotal]</c>).</item>
/// </list>
/// Ordering = whitelisted <c>sortBy</c> + primary-key tie-breaker (<see cref="SortBuilder.ResolveSort{TEntity}"/>).
/// </remarks>
public static class QryxExtensions
{
    private const string CursorParameterName = "afterCursor";

    /// <summary>
    /// Applies declarative filters and sorting from the request to the EF Core queryable.
    /// Sorting is always deterministic (primary-key tie-breaker); an invalid <c>SortBy</c> throws
    /// <see cref="QryxRequestException"/> only when <see cref="QryxOptions.ThrowOnInvalidSortProperty"/> is enabled.
    /// </summary>
    public static IQueryable<TEntity> ApplyQryx<TEntity>(
        this IQueryable<TEntity> source,
        IQryxRequest<TEntity> request)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);

        source = FilterBuilder.Apply(source, request);
        source = SortBuilder.Apply(source, request, QryxOptions.Global.ThrowOnInvalidSortProperty);
        return Tag(source, request);
    }

    /// <summary>
    /// Filters, orders and pages the query in one database roundtrip (plus a COUNT only when
    /// <c>includeTotal=true</c> needs it). See the class remarks for the request shape.
    /// </summary>
    /// <exception cref="QryxRequestException">Invalid client input (filter value, cursor, too deep page, ...).</exception>
    public static Task<PagedResult<TEntity>> ToPagedResultAsync<TEntity>(
        this IQueryable<TEntity> source,
        IQryxRequest<TEntity> request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);

        return ToPagedResultCoreAsync<TEntity, TEntity>(source, request, selector: null, ct);
    }

    /// <summary>
    /// Same as <see cref="ToPagedResultAsync{TEntity}(IQueryable{TEntity}, IQryxRequest{TEntity}, CancellationToken)"/>
    /// with a projection pushed into SQL. The sort-key columns are selected alongside the projection (same statement),
    /// so <c>nextCursor</c> is available even when the projection does not contain them.
    /// </summary>
    /// <exception cref="QryxRequestException">Invalid client input (filter value, cursor, too deep page, ...).</exception>
    public static Task<PagedResult<TResult>> ToPagedResultAsync<TEntity, TResult>(
        this IQueryable<TEntity> source,
        IQryxRequest<TEntity> request,
        Expression<Func<TEntity, TResult>> selector,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(selector);

        return ToPagedResultCoreAsync(source, request, selector, ct);
    }

    /// <summary>
    /// Streams one page as <see cref="IAsyncEnumerable{T}"/> without buffering it (offset or keyset, no COUNT).
    /// </summary>
    /// <exception cref="QryxRequestException">Invalid client input (thrown when this method is called).</exception>
    public static IAsyncEnumerable<TEntity> ToPagedAsyncEnumerable<TEntity>(
        this IQueryable<TEntity> source,
        IQryxRequest<TEntity> request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);

        return ToPagedAsyncEnumerableCore<TEntity, TEntity>(source, request, selector: null, ct);
    }

    /// <summary>
    /// Streams one projected page as <see cref="IAsyncEnumerable{T}"/> without buffering it (offset or keyset, no COUNT).
    /// </summary>
    /// <exception cref="QryxRequestException">Invalid client input (thrown when this method is called).</exception>
    public static IAsyncEnumerable<TResult> ToPagedAsyncEnumerable<TEntity, TResult>(
        this IQueryable<TEntity> source,
        IQryxRequest<TEntity> request,
        Expression<Func<TEntity, TResult>> selector,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(selector);

        return ToPagedAsyncEnumerableCore(source, request, selector, ct);
    }

    private static async Task<PagedResult<TResult>> ToPagedResultCoreAsync<TEntity, TResult>(
        IQueryable<TEntity> source,
        IQryxRequest<TEntity> request,
        Expression<Func<TEntity, TResult>>? selector,
        CancellationToken ct)
    {
        var pageSize = QryxOptions.Global.ClampPageSize(request.PageSize);
        var page = BuildPage(source, request, GetProbeSize(pageSize));
        var cursorPlan = page.Plan != null && PaginationBuilder.SupportsKeyset<TEntity>(page.Plan) ? page.Plan : null;

        List<TResult> items;
        bool hasNext;
        string? nextCursor = null;

        if (selector == null)
        {
            var entities = await page.Query.ToListAsync(ct).ConfigureAwait(false);
            hasNext = TrimProbe(entities, pageSize);
            if (hasNext && cursorPlan != null)
                nextCursor = PaginationBuilder.CreateCursor(entities[^1]!, cursorPlan);

            items = (List<TResult>)(object)entities;
        }
        else if (cursorPlan != null && KeysetProjection.CanProject(cursorPlan))
        {
            // Projection + sort-key columns in the same SELECT -> nextCursor without loading the entity
            var rows = await KeysetProjection.Select(page.Query, selector, cursorPlan).ToListAsync(ct).ConfigureAwait(false);
            hasNext = TrimProbe(rows, pageSize);

            items = new List<TResult>(rows.Count);
            foreach (var row in rows)
                items.Add(row.Item);

            if (hasNext)
                nextCursor = PaginationBuilder.CreateCursorFromValues<TEntity>(cursorPlan, rows[^1].GetKeys(cursorPlan.Keys.Count));
        }
        else
        {
            items = await page.Query.Select(selector).ToListAsync(ct).ConfigureAwait(false);
            hasNext = TrimProbe(items, pageSize);
        }

        int? total = null;
        if (request.IncludeTotal && MetadataCache.GetDtoMetadata(request.GetType()).AllowTotal)
        {
            // Last offset page: the exact total is known without COUNT(*)
            total = !page.IsKeyset && !hasNext && (items.Count > 0 || page.Skip == 0)
                ? page.Skip + items.Count
                : await page.Filtered.CountAsync(ct).ConfigureAwait(false);
        }

        return new PagedResult<TResult>
        {
            Items = items,
            HasNext = hasNext,
            NextCursor = nextCursor,
            Total = total
        };
    }

    private static IAsyncEnumerable<TResult> ToPagedAsyncEnumerableCore<TEntity, TResult>(
        IQueryable<TEntity> source,
        IQryxRequest<TEntity> request,
        Expression<Func<TEntity, TResult>>? selector,
        CancellationToken ct)
    {
        var pageSize = QryxOptions.Global.ClampPageSize(request.PageSize);
        var page = BuildPage(source, request, pageSize);

        var stream = Project(page.Query, selector).AsAsyncEnumerable();
        return ct.CanBeCanceled ? WithCancellation(stream, ct) : stream;
    }

    /// <summary>
    /// Filters + ordering + (keyset seek | offset) + Take(<paramref name="take"/>). The request is validated eagerly.
    /// </summary>
    internal static PageQuery<TEntity> BuildPage<TEntity>(IQueryable<TEntity> source, IQryxRequest<TEntity> request, int take)
    {
        var filtered = Tag(FilterBuilder.Apply(source, request), request);

        // A developer OrderBy on the source is kept when the client sends no (valid) sortBy; such an ordering is
        // unknown to Qryx, so it cannot produce or continue keyset cursors.
        QryxSortPlan? plan = null;
        if (!(string.IsNullOrWhiteSpace(request.SortBy) && SortBuilder.HasOrdering(source)))
        {
            plan = SortBuilder.ResolveSort<TEntity>(request.SortBy, request.IsDescending, request.GetType(), QryxOptions.Global.ThrowOnInvalidSortProperty);
            if ((!plan.HasClientKeys && SortBuilder.HasOrdering(source)) || plan.IsEmpty)
                plan = null;
        }

        if (request.AfterCursor is { } cursor)
        {
            if (plan == null)
            {
                throw new QryxRequestException(
                    CursorParameterName,
                    "Keyset pagination needs an ordering Qryx knows (sortBy or the primary key); this query has a custom OrderBy.");
            }

            var keyset = PaginationBuilder.ApplyKeyset(filtered, cursor, plan, take, maxPageSize: take);
            return new PageQuery<TEntity>(keyset, filtered, plan, Skip: 0, IsKeyset: true);
        }

        var ordered = plan != null ? SortBuilder.ApplySort(filtered, plan) : SortBuilder.EnsureOrdering(filtered, request.IsDescending);
        var skip = PaginationBuilder.GetSkip(request.Page, QryxOptions.Global.ClampPageSize(request.PageSize));
        return new PageQuery<TEntity>(ordered.Skip(skip).Take(take), filtered, plan, skip, IsKeyset: false);
    }

    /// <summary>The page query plus what the response needs: unpaged filter (for COUNT), sort plan and offset.</summary>
    internal readonly record struct PageQuery<TEntity>(
        IQueryable<TEntity> Query,
        IQueryable<TEntity> Filtered,
        QryxSortPlan? Plan,
        int Skip,
        bool IsKeyset);

    private static IQueryable<TEntity> Tag<TEntity>(IQueryable<TEntity> source, IQryxRequest<TEntity> request)
    {
        return QryxOptions.EnableQueryTagging
            ? source.TagWith($"Qryx: {request.GetType().Name}")
            : source;
    }

    private static IQueryable<TResult> Project<TEntity, TResult>(
        IQueryable<TEntity> source,
        Expression<Func<TEntity, TResult>>? selector)
    {
        return selector != null ? source.Select(selector) : (IQueryable<TResult>)source;
    }

    /// <summary>pageSize + 1 (one extra row tells whether a next page exists), overflow-safe.</summary>
    private static int GetProbeSize(int pageSize) => pageSize < int.MaxValue ? pageSize + 1 : pageSize;

    /// <summary>Removes the extra probe row; returns true when it was present (i.e. a next page exists).</summary>
    private static bool TrimProbe<T>(List<T> items, int pageSize)
    {
        if (items.Count <= pageSize)
            return false;

        items.RemoveRange(pageSize, items.Count - pageSize);
        return true;
    }

    private static async IAsyncEnumerable<T> WithCancellation<T>(
        IAsyncEnumerable<T> source,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var item in source.WithCancellation(ct).ConfigureAwait(false))
        {
            yield return item;
        }
    }
}
