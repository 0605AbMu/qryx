using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using Qryx.Core.Constants;
using Qryx.Core.Contracts;
using Qryx.Core.Exceptions;
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

    /// <summary>
    /// Applies the request's ordering: whitelisted <c>SortBy</c> clauses plus the primary key as tie-breaker
    /// (deterministic pages, and the same ordering keyset cursors continue). Without <c>SortBy</c> an ordering the
    /// developer already applied to <paramref name="source"/> is kept; otherwise rows are ordered by primary key.
    /// </summary>
    public static IQueryable<TEntity> Apply<TEntity>(
        IQueryable<TEntity> source,
        IQryxRequest<TEntity> request,
        bool throwOnInvalidProperty = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.SortBy) && HasOrdering(source))
            return source;

        var plan = ResolveSort<TEntity>(request.SortBy, request.IsDescending, request.GetType(), throwOnInvalidProperty);

        // Every clause was rejected: keep an ordering the developer already applied, else primary key
        if (!plan.HasClientKeys && HasOrdering(source))
            return source;

        return ApplySort(source, plan);
    }

    /// <summary>
    /// Applies whitelisted <paramref name="sortBy"/> clauses (plus primary-key tie-breaker). Returns
    /// <paramref name="source"/> unchanged when no clause is valid.
    /// </summary>
    public static IQueryable<TEntity> Apply<TEntity>(
        IQueryable<TEntity> source,
        string? sortBy,
        bool isDescending = false,
        bool throwOnInvalidProperty = false,
        Type? dtoType = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        var plan = ResolveSort<TEntity>(sortBy, isDescending, dtoType, throwOnInvalidProperty);
        return plan.HasClientKeys ? ApplySort(source, plan) : source;
    }

    /// <summary>
    /// Resolves <paramref name="sortBy"/> (e.g. <c>"createdAt desc, name"</c>) into a validated
    /// <see cref="QryxSortPlan"/>: only [Sortable] properties (DTO or entity), canonical names, duplicates removed,
    /// at most <see cref="QryxConstants.MaxSortClauses"/> clauses; then the primary key is appended as tie-breaker
    /// (direction of the last clause, or <paramref name="isDescending"/> when there is no valid clause).
    /// Invalid clauses are skipped, or throw <see cref="QryxRequestException"/> when <paramref name="throwOnInvalidProperty"/>.
    /// </summary>
    public static QryxSortPlan ResolveSort<TEntity>(
        string? sortBy,
        bool isDescending = false,
        Type? dtoType = null,
        bool throwOnInvalidProperty = false)
    {
        var entityMetadata = MetadataCache.GetEntityMetadata<TEntity>();
        var keys = new List<QryxSortKey>(4);

        if (!string.IsNullOrWhiteSpace(sortBy))
        {
            // DoS guard: haddan tashqari uzun sortBy keshga ham, parserga ham tushmaydi
            if (sortBy.Length > QryxConstants.MaxSortByLength)
            {
                if (throwOnInvalidProperty)
                {
                    throw new QryxRequestException(
                        nameof(IQryxRequest.SortBy),
                        $"Sort expression must not exceed {QryxConstants.MaxSortByLength} characters.");
                }
            }
            else
            {
                if (ClauseCache.Count > 5000)
                {
                    ClauseCache.Clear();
                }

                var clauses = ClauseCache.GetOrAdd((sortBy, isDescending), static k => ParseSortClauses(k.SortBy, k.Descending));
                if (clauses.Length > QryxConstants.MaxSortClauses && throwOnInvalidProperty)
                {
                    throw new QryxRequestException(
                        nameof(IQryxRequest.SortBy),
                        $"At most {QryxConstants.MaxSortClauses} sort fields are allowed.");
                }

                var dtoMetadata = dtoType != null ? MetadataCache.GetDtoMetadata(dtoType) : null;
                var count = Math.Min(clauses.Length, QryxConstants.MaxSortClauses);
                for (int i = 0; i < count; i++)
                {
                    var clause = clauses[i];
                    if (!TryGetSortablePropertyName(clause.PropertyName, dtoMetadata, entityMetadata, out var canonical))
                    {
                        if (throwOnInvalidProperty)
                        {
                            throw new QryxRequestException(
                                nameof(IQryxRequest.SortBy),
                                $"Sort property '{clause.PropertyName}' is not allowed or does not exist on entity '{typeof(TEntity).Name}'.");
                        }

                        continue; // invalid clause is ignored
                    }

                    if (!keys.Exists(k => string.Equals(k.Path, canonical, StringComparison.OrdinalIgnoreCase)))
                        keys.Add(new QryxSortKey(canonical, clause.IsDescending));
                }
            }
        }

        var hasClientKeys = keys.Count > 0;

        // Primary key tie-breaker: rows with equal sort values get a stable order (offset pages never overlap and
        // keyset cursors are unique). Kalit nomi kanonik, shuning uchun kesh chegaralangan.
        var primaryKey = entityMetadata.PrimaryKeyProperty?.Name;
        if (primaryKey != null && entityMetadata.HasProperty(primaryKey) &&
            !keys.Exists(k => string.Equals(k.Path, primaryKey, StringComparison.OrdinalIgnoreCase)))
        {
            keys.Add(new QryxSortKey(primaryKey, hasClientKeys ? keys[^1].Descending : isDescending));
        }

        return new QryxSortPlan([.. keys], hasClientKeys);
    }

    /// <summary>Applies a resolved plan as OrderBy/ThenBy (cached, pre-built steps). Empty plan: source unchanged.</summary>
    public static IQueryable<TEntity> ApplySort<TEntity>(IQueryable<TEntity> source, QryxSortPlan plan)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(plan);

        IQueryable current = source;
        var keys = plan.KeyArray;
        for (int i = 0; i < keys.Length; i++)
        {
            var key = (typeof(TEntity), keys[i].Path, keys[i].Descending, i == 0);
            var step = SortStepCache.GetOrAdd(key, static k => CreateSortStep(k.EntityType, k.PropertyName, k.IsDescending, k.IsFirst));
            current = step(current);
        }

        return (IQueryable<TEntity>)current;
    }

    public static bool IsPropertySortable<TEntity>(string propertyName, Type? dtoType, EntityMetadata entityMetadata)
    {
        var dtoMetadata = dtoType != null ? MetadataCache.GetDtoMetadata(dtoType) : null;
        return IsPropertySortable(propertyName, dtoMetadata, entityMetadata);
    }

    public static bool IsPropertySortable(string propertyName, DtoMetadata? dtoMetadata, EntityMetadata entityMetadata)
        => TryGetSortablePropertyName(propertyName, dtoMetadata, entityMetadata, out _);

    /// <summary>
    /// Resolves a client-supplied property name against the [Sortable] whitelist.
    /// The whitelist is checked BEFORE any entity lookup, so arbitrary client strings never reach
    /// metadata caches; the returned canonical name (as declared) is safe to use as a cache key.
    /// </summary>
    public static bool TryGetSortablePropertyName(
        string propertyName,
        DtoMetadata? dtoMetadata,
        EntityMetadata entityMetadata,
        out string canonicalName)
    {
        ArgumentNullException.ThrowIfNull(entityMetadata);

        // Sortable is the single source of truth:
        // 1. DTO properties with [Sortable], 2. Entity properties with [Sortable]
        if ((dtoMetadata != null && TryGetFromSet(dtoMetadata.SortableProperties, propertyName, out canonicalName)) ||
            TryGetFromSet(entityMetadata.SortableProperties, propertyName, out canonicalName))
        {
            return entityMetadata.HasProperty(canonicalName);
        }

        canonicalName = string.Empty;
        return false;
    }

    internal static bool TryGetFromSet(IReadOnlySet<string> set, string value, out string canonical)
    {
        if (set is HashSet<string> hashSet)
        {
            if (hashSet.TryGetValue(value, out var actual))
            {
                canonical = actual;
                return true;
            }
        }
        else
        {
            foreach (var item in set)
            {
                if (string.Equals(item, value, StringComparison.OrdinalIgnoreCase))
                {
                    canonical = item;
                    return true;
                }
            }
        }

        canonical = string.Empty;
        return false;
    }

    /// <summary>Number of cached sort steps for an entity type (diagnostics / tests).</summary>
    internal static int CountCachedSortSteps(Type entityType) => SortStepCache.Keys.Count(k => k.EntityType == entityType);

    /// <summary>
    /// Applies sorting directly by property name, bypassing external user input whitelist validation.
    /// Used internally for cursor-based keyset pagination and deterministic primary key fallbacks.
    /// </summary>
    public static IQueryable<TEntity> ApplyDirect<TEntity>(
        IQueryable<TEntity> source,
        string propertyName,
        bool isDescending = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(propertyName))
            return source;

        var key = (typeof(TEntity), propertyName, isDescending, true);
        var step = SortStepCache.GetOrAdd(key, static k => CreateSortStep(k.EntityType, k.PropertyName, k.IsDescending, k.IsFirst));
        return (IQueryable<TEntity>)step(source);
    }

    public static bool HasOrdering(IQueryable query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var expr = query.Expression;
        while (expr != null)
        {
            if (expr is MethodCallExpression methodCall)
            {
                var name = methodCall.Method.Name;
                if (name is nameof(Queryable.OrderBy) or nameof(Queryable.OrderByDescending)
                    or nameof(Queryable.ThenBy) or nameof(Queryable.ThenByDescending))
                {
                    return true;
                }

                if (methodCall.Arguments.Count == 0)
                    break;

                expr = methodCall.Arguments[0];
            }
            else if (expr is UnaryExpression unary)
            {
                expr = unary.Operand;
            }
            else
            {
                break;
            }
        }
        return false;
    }

    public static IQueryable<TEntity> EnsureOrdering<TEntity>(
        IQueryable<TEntity> source,
        bool isDescending = false)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (HasOrdering(source))
            return source;

        var entityMetadata = MetadataCache.GetEntityMetadata<TEntity>();
        var defaultProp = entityMetadata.PrimaryKeyProperty?.Name;

        if (defaultProp != null && entityMetadata.HasProperty(defaultProp))
        {
            return ApplyDirect(source, defaultProp, isDescending);
        }

        if (entityMetadata.HasProperty("Id"))
        {
            return ApplyDirect(source, "Id", isDescending);
        }

        return source;
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
