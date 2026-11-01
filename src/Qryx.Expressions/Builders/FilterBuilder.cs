using System.Collections;
using System.Linq.Expressions;
using Qryx.Core.Enums;
using Qryx.Expressions.Metadata;

namespace Qryx.Expressions.Builders;

/// <summary>
/// Builds Expression Trees from DTO metadata and applies them to IQueryable.
/// </summary>
public static class FilterBuilder
{
    /// <summary>
    /// Applies declarative filter attributes from the DTO instance to the queryable.
    /// Automatically detects provider (e.g. PostgreSQL) for optimal native translation.
    /// </summary>
    public static IQueryable<TEntity> Apply<TEntity>(IQueryable<TEntity> source, object request)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);

        var isPostgreSql = ExpressionHelper.IsPostgreSqlProvider(source.Provider);
        var predicate = BuildPredicate<TEntity>(request, isPostgreSql);
        return predicate != null ? source.Where(predicate) : source;
    }

    /// <summary>
    /// Builds a combined Expression&lt;Func&lt;TEntity, bool&gt;&gt; predicate from DTO filter metadata.
    /// Returns null if no active filters are provided.
    /// </summary>
    public static Expression<Func<TEntity, bool>>? BuildPredicate<TEntity>(
        object request,
        bool isPostgreSql = false)
    {
        ArgumentNullException.ThrowIfNull(request);

        var dtoMetadata = MetadataCache.GetDtoMetadata(request.GetType());
        if (dtoMetadata.Filters.Count == 0)
            return null;

        var parameter = Expression.Parameter(typeof(TEntity), "x");
        Expression? combined = null;
        var filters = dtoMetadata.Filters;

        for (int fIndex = 0; fIndex < filters.Count; fIndex++)
        {
            var filter = filters[fIndex];
            var val = filter.Getter(request);
            if (val == null)
                continue;

            if (val is string str && string.IsNullOrEmpty(str))
                continue;

            if (filter.IsCollection && val is IEnumerable enumerable)
            {
                var enumerator = enumerable.GetEnumerator();
                using var disposable = enumerator as IDisposable;
                if (!enumerator.MoveNext())
                    continue;
            }

            Expression? filterCombinedExpr = null;
            var paths = filter.TargetPropertyPaths;
            for (int i = 0; i < paths.Count; i++)
            {
                var pathExpr = BuildSingleValueExpressionForPath(parameter, paths[i], filter, val, isPostgreSql);
                if (pathExpr == null)
                    continue;

                filterCombinedExpr = filterCombinedExpr == null
                    ? pathExpr
                    : Expression.OrElse(filterCombinedExpr, pathExpr);
            }

            if (filterCombinedExpr != null)
            {
                combined = combined == null
                    ? filterCombinedExpr
                    : Expression.AndAlso(combined, filterCombinedExpr);
            }
        }

        if (combined == null)
            return null;

        return Expression.Lambda<Func<TEntity, bool>>(combined, parameter);
    }

    private static Expression? BuildSingleValueExpressionForPath(
        Expression parameter,
        string targetPath,
        FilterPropertyMetadata filter,
        object val,
        bool isPostgreSql)
    {
        var targetMember = ExpressionHelper.BuildMemberAccess(parameter, targetPath);

        switch (filter.Operator)
        {
            case Op.Equals:
            {
                var constant = ExpressionHelper.BuildConstantExpression(val, targetMember.Type);
                return Expression.Equal(targetMember, constant);
            }
            case Op.NotEquals:
            {
                var constant = ExpressionHelper.BuildConstantExpression(val, targetMember.Type);
                return Expression.NotEqual(targetMember, constant);
            }
            case Op.GreaterThan:
            {
                var constant = ExpressionHelper.BuildConstantExpression(val, targetMember.Type);
                return Expression.GreaterThan(targetMember, constant);
            }
            case Op.GreaterThanOrEqual:
            {
                var constant = ExpressionHelper.BuildConstantExpression(val, targetMember.Type);
                return Expression.GreaterThanOrEqual(targetMember, constant);
            }
            case Op.LessThan:
            {
                var constant = ExpressionHelper.BuildConstantExpression(val, targetMember.Type);
                return Expression.LessThan(targetMember, constant);
            }
            case Op.LessThanOrEqual:
            {
                var constant = ExpressionHelper.BuildConstantExpression(val, targetMember.Type);
                return Expression.LessThanOrEqual(targetMember, constant);
            }
            case Op.StartsWith:
            {
                var constant = Expression.Constant(val.ToString(), typeof(string));
                return ExpressionHelper.BuildStartsWith(targetMember, constant, filter.Invariant, isPostgreSql);
            }
            case Op.EndsWith:
            {
                var constant = Expression.Constant(val.ToString(), typeof(string));
                return ExpressionHelper.BuildEndsWith(targetMember, constant, filter.Invariant, isPostgreSql);
            }
            case Op.Contains:
            {
                var constant = Expression.Constant(val.ToString(), typeof(string));
                return ExpressionHelper.BuildContains(targetMember, constant, filter.Invariant, isPostgreSql);
            }
            case Op.In:
            {
                return ExpressionHelper.BuildInExpression(targetMember, val);
            }
            case Op.NotIn:
            {
                return Expression.Not(ExpressionHelper.BuildInExpression(targetMember, val));
            }
            case Op.Between:
            {
                var constant = ExpressionHelper.BuildConstantExpression(val, targetMember.Type);
                return Expression.Equal(targetMember, constant);
            }
            case Op.IsNull:
            {
                if (val is bool b)
                {
                    return b
                        ? Expression.Equal(targetMember, Expression.Constant(null, targetMember.Type))
                        : Expression.NotEqual(targetMember, Expression.Constant(null, targetMember.Type));
                }
                return Expression.Equal(targetMember, Expression.Constant(null, targetMember.Type));
            }
            case Op.IsNotNull:
            {
                if (val is bool b)
                {
                    return b
                        ? Expression.NotEqual(targetMember, Expression.Constant(null, targetMember.Type))
                        : Expression.Equal(targetMember, Expression.Constant(null, targetMember.Type));
                }
                return Expression.NotEqual(targetMember, Expression.Constant(null, targetMember.Type));
            }
            case Op.Fts:
            {
                var constant = Expression.Constant(val.ToString(), typeof(string));
                return ExpressionHelper.BuildFts(targetMember, constant, isPostgreSql);
            }
            case Op.JsonContains:
            {
                var constant = Expression.Constant(val.ToString(), typeof(string));
                return ExpressionHelper.BuildJsonContains(targetMember, constant, isPostgreSql);
            }
            default:
                throw new NotSupportedException($"Operator '{filter.Operator}' is not supported.");
        }
    }
}
