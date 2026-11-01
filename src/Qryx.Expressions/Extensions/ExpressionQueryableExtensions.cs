using Qryx.Core.Constants;
using Qryx.Core.Contracts;
using Qryx.Expressions.Builders;

namespace Qryx.Expressions.Extensions;

/// <summary>
/// Core IQueryable extension methods for filter, sort, and pagination.
/// </summary>
public static class ExpressionQueryableExtensions
{
    public static IQueryable<TEntity> ApplyFilters<TEntity>(
        this IQueryable<TEntity> source,
        object request)
    {
        return FilterBuilder.Apply(source, request);
    }

    public static IQueryable<TEntity> ApplySorting<TEntity>(
        this IQueryable<TEntity> source,
        IQryxRequest<TEntity> request,
        bool throwOnInvalidProperty = false)
    {
        return SortBuilder.Apply(source, request, throwOnInvalidProperty);
    }

    public static IQueryable<TEntity> ApplyOffsetPagination<TEntity>(
        this IQueryable<TEntity> source,
        IQryxRequest<TEntity> request,
        int maxPageSize = QryxConstants.DefaultMaxPageSize)
    {
        return PaginationBuilder.ApplyOffset(source, request, maxPageSize);
    }
}
