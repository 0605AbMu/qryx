using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using Qryx.Core.Contracts;
using Qryx.Expressions.Metadata;

namespace Qryx.Expressions.Builders;

/// <summary>
/// Validates SortBy against Entity properties (whitelist) and applies dynamic sorting to IQueryable.
/// Uses pre-compiled generic invocation delegates and clause caches for zero-allocation performance.
/// </summary>
public static class SortBuilder
{
    private static readonly MethodInfo OrderByMethod =
        typeof(Queryable).GetMethods().First(m => m.Name == nameof(Queryable.OrderBy) && m.GetParameters().Length == 2);

    private static readonly MethodInfo OrderByDescendingMethod =
        typeof(Queryable).GetMethods().First(m => m.Name == nameof(Queryable.OrderByDescending) && m.GetParameters().Length == 2);

    private static readonly MethodInfo ThenByMethod =
        typeof(Queryable).GetMethods().First(m => m.Name == nameof(Queryable.ThenBy) && m.GetParameters().Length == 2);

    private static readonly MethodInfo ThenByDescendingMethod =
        typeof(Queryable).GetMethods().First(m => m.Name == nameof(Queryable.ThenByDescending) && m.GetParameters().Length == 2);

    private static readonly ConcurrentDictionary<(string SortBy, bool Descending), (string PropertyName, bool IsDescending)[]> ClauseCache = new();
    private static readonly ConcurrentDictionary<(Type EntityType, string PropertyName, bool IsDescending, bool IsFirst), Func<IQueryable, IQueryable>> SortStepCache = new();

    public static IQueryable<TEntity> Apply<TEntity>(
        IQueryable<TEntity> source,
        IQryxRequest<TEntity> request,
        bool throwOnInvalidProperty = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);

        return Apply(source, request.SortBy, request.IsDescending, throwOnInvalidProperty);
    }

    public static IQueryable<TEntity> Apply<TEntity>(
        IQueryable<TEntity> source,
        string? sortBy,
        bool isDescending = false,
        bool throwOnInvalidProperty = false)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (string.IsNullOrWhiteSpace(sortBy))
            return source;

        var entityMetadata = MetadataCache.GetEntityMetadata<TEntity>();

        // Support multiple comma-separated fields: e.g. "CreatedAt desc, Name asc" or single field
        var sortClauses = ClauseCache.GetOrAdd((sortBy, isDescending), static k => ParseSortClauses(k.SortBy, k.Descending));

        IOrderedQueryable<TEntity>? orderedQuery = null;
        bool isFirst = true;

        foreach (var clause in sortClauses)
        {
            if (!entityMetadata.HasProperty(clause.PropertyName))
            {
                if (throwOnInvalidProperty)
                {
                    throw new ArgumentException(
                        $"Sort property '{clause.PropertyName}' does not exist on entity '{typeof(TEntity).Name}' or is not allowed.",
                        nameof(sortBy));
                }

                // If invalid and not throwing, ignore this clause
                continue;
            }

            var key = (typeof(TEntity), clause.PropertyName, clause.IsDescending, isFirst);
            var step = SortStepCache.GetOrAdd(key, static k => CreateSortStep(k.EntityType, k.PropertyName, k.IsDescending, k.IsFirst));

            var currentQuery = (IQueryable)(orderedQuery ?? source);
            orderedQuery = (IOrderedQueryable<TEntity>)step(currentQuery);
            isFirst = false;
        }

        return orderedQuery ?? source;
    }

    private static Func<IQueryable, IQueryable> CreateSortStep(Type entityType, string propertyName, bool isDescending, bool isFirst)
    {
        var parameter = Expression.Parameter(entityType, "x");
        var member = ExpressionHelper.BuildMemberAccess(parameter, propertyName);
        var lambda = Expression.Lambda(member, parameter);

        var methodToCall = isFirst
            ? (isDescending ? OrderByDescendingMethod : OrderByMethod)
            : (isDescending ? ThenByDescendingMethod : ThenByMethod);

        var genericMethod = methodToCall.MakeGenericMethod(entityType, member.Type);
        var quotedLambda = Expression.Quote(lambda);

        return query =>
        {
            var resultExpr = Expression.Call(null, genericMethod, query.Expression, quotedLambda);
            return query.Provider.CreateQuery(resultExpr);
        };
    }

    private static (string PropertyName, bool IsDescending)[] ParseSortClauses(string sortBy, bool defaultIsDescending)
    {
        var result = new List<(string PropertyName, bool IsDescending)>();
        var parts = sortBy.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var part in parts)
        {
            var tokens = part.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length == 0)
                continue;

            var propName = tokens[0];
            bool desc = defaultIsDescending;

            if (tokens.Length > 1)
            {
                if (string.Equals(tokens[1], "desc", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tokens[1], "descending", StringComparison.OrdinalIgnoreCase))
                {
                    desc = true;
                }
                else if (string.Equals(tokens[1], "asc", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(tokens[1], "ascending", StringComparison.OrdinalIgnoreCase))
                {
                    desc = false;
                }
            }

            result.Add((propName, desc));
        }

        return result.ToArray();
    }
}
