using System.Collections;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using Qryx.Core.Enums;
using Qryx.Core.Types;
using Qryx.Expressions.Metadata;

namespace Qryx.Expressions.Builders;

/// <summary>
/// Builds Expression Trees from DTO metadata and applies them to IQueryable.
/// All filter values are emitted as SQL parameters (never inlined literals), so EF Core reuses
/// the compiled query and the database reuses its execution plan for every value combination.
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

        var plan = FilterPlanCache<TEntity>.Get(request.GetType());
        var filters = plan.Filters;
        if (filters.Length == 0)
            return null;

        var parameter = FilterPlanCache<TEntity>.Parameter;
        Expression? combined = null;

        for (int fIndex = 0; fIndex < filters.Length; fIndex++)
        {
            var filterExpr = BuildFilterExpression(parameter, filters[fIndex], request, isPostgreSql);
            if (filterExpr != null)
            {
                combined = combined == null
                    ? filterExpr
                    : Expression.AndAlso(combined, filterExpr);
            }
        }

        if (combined == null)
            return null;

        return Expression.Lambda<Func<TEntity, bool>>(combined, parameter);
    }

    /// <summary>
    /// The shared lambda parameter (<c>x</c>) used by every predicate this builder creates for <typeparamref name="TEntity"/>.
    /// Bodies built against it (including <see cref="BuildPropertyPredicate{TEntity}"/> results) can be combined directly.
    /// </summary>
    public static ParameterExpression EntityParameter<TEntity>() => FilterPlanCache<TEntity>.Parameter;

    /// <summary>
    /// Builds the predicate of a single [Filter] property of the DTO (null when its value is empty).
    /// Used by the source generator for operators it delegates to the runtime engine (e.g. Fts, JsonContains).
    /// </summary>
    /// <exception cref="ArgumentException">The DTO has no [Filter] property with this name.</exception>
    public static Expression<Func<TEntity, bool>>? BuildPropertyPredicate<TEntity>(
        object request,
        string dtoPropertyName,
        bool isPostgreSql = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(dtoPropertyName);

        var filters = FilterPlanCache<TEntity>.Get(request.GetType()).Filters;
        for (int i = 0; i < filters.Length; i++)
        {
            if (string.Equals(filters[i].Metadata.DtoPropertyName, dtoPropertyName, StringComparison.Ordinal))
            {
                var parameter = FilterPlanCache<TEntity>.Parameter;
                var body = BuildFilterExpression(parameter, filters[i], request, isPostgreSql);
                return body != null ? Expression.Lambda<Func<TEntity, bool>>(body, parameter) : null;
            }
        }

        throw new ArgumentException(
            $"'{request.GetType().Name}' has no [Filter] property named '{dtoPropertyName}'.",
            nameof(dtoPropertyName));
    }

    private static Expression? BuildFilterExpression(
        ParameterExpression parameter,
        FilterPlanEntry entry,
        object request,
        bool isPostgreSql)
    {
        var filter = entry.Metadata;
        var val = filter.Getter(request);
        if (val == null)
            return null;

        if (val is string str && string.IsNullOrEmpty(str))
            return null;

        if (val is IRange { HasValue: false })
            return null;

        if (filter.IsCollection && val is IEnumerable enumerable)
        {
            var enumerator = enumerable.GetEnumerator();
            using var disposable = enumerator as IDisposable;
            if (!enumerator.MoveNext())
                return null;
        }

        Expression? filterCombinedExpr = null;
        var paths = entry.Paths;
        for (int i = 0; i < paths.Length; i++)
        {
            var pathExpr = BuildPathExpression(parameter, paths[i], filter, val, isPostgreSql);
            if (pathExpr == null)
                continue;

            filterCombinedExpr = filterCombinedExpr == null
                ? pathExpr
                : Expression.OrElse(filterCombinedExpr, pathExpr);
        }

        return filterCombinedExpr;
    }

    private static Expression? BuildPathExpression(
        ParameterExpression parameter,
        PathPlan path,
        FilterPropertyMetadata filter,
        object val,
        bool isPostgreSql)
    {
        // Path could not be resolved while building the plan -> rebuild to surface the original error.
        var targetMember = path.Member ?? ExpressionHelper.BuildMemberAccess(parameter, path.Path);
        var valueParameter = path.ValueParameter!;
        var name = filter.DtoPropertyName;

        switch (filter.Operator)
        {
            case Op.Equals:
                return Expression.Equal(targetMember, ExpressionHelper.BuildParameter(val, valueParameter, name));
            case Op.NotEquals:
                return Expression.NotEqual(targetMember, ExpressionHelper.BuildParameter(val, valueParameter, name));
            case Op.GreaterThan:
                return Expression.GreaterThan(targetMember, ExpressionHelper.BuildParameter(val, valueParameter, name));
            case Op.GreaterThanOrEqual:
                return Expression.GreaterThanOrEqual(targetMember, ExpressionHelper.BuildParameter(val, valueParameter, name));
            case Op.LessThan:
                return Expression.LessThan(targetMember, ExpressionHelper.BuildParameter(val, valueParameter, name));
            case Op.LessThanOrEqual:
                return Expression.LessThanOrEqual(targetMember, ExpressionHelper.BuildParameter(val, valueParameter, name));
            case Op.StartsWith:
                return ExpressionHelper.BuildStartsWith(targetMember, val.ToString() ?? string.Empty, filter.Invariant, isPostgreSql);
            case Op.EndsWith:
                return ExpressionHelper.BuildEndsWith(targetMember, val.ToString() ?? string.Empty, filter.Invariant, isPostgreSql);
            case Op.Contains:
                return ExpressionHelper.BuildContains(targetMember, val.ToString() ?? string.Empty, filter.Invariant, isPostgreSql);
            case Op.In:
                return ExpressionHelper.BuildInExpression(targetMember, val, name);
            case Op.NotIn:
                return Expression.Not(ExpressionHelper.BuildInExpression(targetMember, val, name));
            case Op.Between:
            {
                // Range<T>: index-friendly "column >= @from AND column <= @to" (each bound optional)
                if (val is IRange range)
                {
                    Expression? lower = range.From != null
                        ? Expression.GreaterThanOrEqual(targetMember, ExpressionHelper.BuildParameter(range.From, valueParameter, name))
                        : null;
                    Expression? upper = range.To != null
                        ? Expression.LessThanOrEqual(targetMember, ExpressionHelper.BuildParameter(range.To, valueParameter, name))
                        : null;

                    return lower != null && upper != null ? Expression.AndAlso(lower, upper) : lower ?? upper;
                }

                // Backward compatibility: a scalar value with Op.Between behaves like Equals
                return Expression.Equal(targetMember, ExpressionHelper.BuildParameter(val, valueParameter, name));
            }
            case Op.IsNull:
            {
                var isNull = val is not bool b || b;
                var nullConstant = Expression.Constant(null, targetMember.Type);
                return isNull
                    ? Expression.Equal(targetMember, nullConstant)
                    : Expression.NotEqual(targetMember, nullConstant);
            }
            case Op.IsNotNull:
            {
                var isNotNull = val is not bool b || b;
                var nullConstant = Expression.Constant(null, targetMember.Type);
                return isNotNull
                    ? Expression.NotEqual(targetMember, nullConstant)
                    : Expression.Equal(targetMember, nullConstant);
            }
            case Op.Fts:
                return ExpressionHelper.BuildFts(targetMember, val.ToString() ?? string.Empty, isPostgreSql);
            case Op.JsonContains:
                return ExpressionHelper.BuildJsonContains(targetMember, val.ToString() ?? string.Empty, isPostgreSql);
            default:
                throw new NotSupportedException($"Operator '{filter.Operator}' is not supported.");
        }
    }

    /// <summary>
    /// Per (TEntity, DTO) filter plan: member access expressions and parameter accessors are resolved once,
    /// so a request only reads DTO values and allocates the final expression nodes.
    /// </summary>
    private static class FilterPlanCache<TEntity>
    {
        public static readonly ParameterExpression Parameter = Expression.Parameter(typeof(TEntity), "x");

        private static readonly ConcurrentDictionary<Type, FilterPlan> Plans = new();

        public static FilterPlan Get(Type dtoType) =>
            Plans.GetOrAdd(dtoType, static t => FilterPlan.Create(Parameter, MetadataCache.GetDtoMetadata(t)));
    }

    private sealed class FilterPlan
    {
        public required FilterPlanEntry[] Filters { get; init; }

        public static FilterPlan Create(ParameterExpression parameter, DtoMetadata dtoMetadata)
        {
            var source = dtoMetadata.Filters;
            var filters = new FilterPlanEntry[source.Count];

            for (int i = 0; i < source.Count; i++)
            {
                var filter = source[i];
                var targetPaths = filter.TargetPropertyPaths;
                var paths = new PathPlan[targetPaths.Count];

                for (int p = 0; p < targetPaths.Count; p++)
                {
                    paths[p] = PathPlan.Create(parameter, targetPaths[p]);
                }

                filters[i] = new FilterPlanEntry { Metadata = filter, Paths = paths };
            }

            return new FilterPlan { Filters = filters };
        }
    }

    private sealed class FilterPlanEntry
    {
        public required FilterPropertyMetadata Metadata { get; init; }
        public required PathPlan[] Paths { get; init; }
    }

    private sealed class PathPlan
    {
        public required string Path { get; init; }

        /// <summary>Shared, immutable member access (e.g. x.Department.Name). Null if the path is invalid.</summary>
        public Expression? Member { get; init; }

        /// <summary>Parameter holder accessor for exactly the member type (no Expression.Convert needed).</summary>
        public ParameterAccessor? ValueParameter { get; init; }

        public static PathPlan Create(ParameterExpression parameter, string path)
        {
            try
            {
                var member = ExpressionHelper.BuildMemberAccess(parameter, path);
                return new PathPlan
                {
                    Path = path,
                    Member = member,
                    ValueParameter = ParameterAccessor.For(member.Type)
                };
            }
            catch (InvalidOperationException)
            {
                // Keep previous behavior: an invalid path only fails when its filter is actually used.
                return new PathPlan { Path = path };
            }
        }
    }
}
