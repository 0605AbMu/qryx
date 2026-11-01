using System.Linq.Expressions;
using Qryx.Core.Constants;
using Qryx.Core.Contracts;
using Qryx.Expressions.Metadata;

namespace Qryx.Expressions.Builders;

/// <summary>
/// Handles offset and keyset pagination with DoS guards.
/// </summary>
public static class PaginationBuilder
{
    public static int ClampPageSize(int pageSize, int maxPageSize = QryxConstants.DefaultMaxPageSize)
    {
        if (pageSize <= 0)
            return QryxConstants.DefaultPageSize;

        return Math.Min(pageSize, maxPageSize);
    }

    public static int ClampPage(int page)
    {
        return page < 1 ? QryxConstants.DefaultPage : page;
    }

    /// <summary>
    /// Applies offset pagination (Skip and Take) with DoS guard.
    /// </summary>
    public static IQueryable<TEntity> ApplyOffset<TEntity>(
        IQueryable<TEntity> source,
        int page,
        int pageSize,
        int maxPageSize = QryxConstants.DefaultMaxPageSize)
    {
        ArgumentNullException.ThrowIfNull(source);

        var clampedPage = ClampPage(page);
        var clampedPageSize = ClampPageSize(pageSize, maxPageSize);

        return source.Skip((clampedPage - 1) * clampedPageSize).Take(clampedPageSize);
    }

    /// <summary>
    /// Applies offset pagination from request.
    /// </summary>
    public static IQueryable<TEntity> ApplyOffset<TEntity>(
        IQueryable<TEntity> source,
        IQryxRequest<TEntity> request,
        int maxPageSize = QryxConstants.DefaultMaxPageSize)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ApplyOffset(source, request.Page, request.PageSize, maxPageSize);
    }

    /// <summary>
    /// Applies keyset (cursor-based) pagination.
    /// Example: Where(x => x.Id > AfterCursor).OrderBy(x => x.Id).Take(PageSize)
    /// </summary>
    public static IQueryable<TEntity> ApplyKeyset<TEntity>(
        IQueryable<TEntity> source,
        object afterCursor,
        int pageSize,
        string? cursorProperty = null,
        bool isDescending = false,
        int maxPageSize = QryxConstants.DefaultMaxPageSize)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(afterCursor);

        var entityMetadata = MetadataCache.GetEntityMetadata<TEntity>();
        var propName = cursorProperty
                       ?? entityMetadata.PrimaryKeyProperty?.Name
                       ?? "Id";

        var propInfo = entityMetadata.ResolveProperty(propName)
                       ?? throw new InvalidOperationException($"Cursor property '{propName}' was not found on '{typeof(TEntity).Name}'.");

        var parameter = Expression.Parameter(typeof(TEntity), "x");
        var member = ExpressionHelper.BuildMemberAccess(parameter, propName);
        var constant = ExpressionHelper.BuildConstantExpression(afterCursor, member.Type);

        var comparison = isDescending
            ? Expression.LessThan(member, constant)
            : Expression.GreaterThan(member, constant);

        var filterLambda = Expression.Lambda<Func<TEntity, bool>>(comparison, parameter);
        var filtered = source.Where(filterLambda);

        // Apply ordering by cursor
        var ordered = SortBuilder.Apply(filtered, propName, isDescending);

        var clampedPageSize = ClampPageSize(pageSize, maxPageSize);
        return ordered.Take(clampedPageSize);
    }
}
