using System.Linq.Expressions;
using System.Reflection;
using Qryx.Expressions.Builders;

namespace Qryx.EntityFramework.Internal;

/// <summary>
/// A projected row: the caller's selector result plus the sort-key values of the row (boxed, in
/// <see cref="QryxSortPlan.Keys"/> order), so a projection can still produce <c>nextCursor</c>.
/// Supports up to <see cref="MaxKeys"/> keys (5 sortBy clauses + primary-key tie-breaker).
/// </summary>
internal sealed class KeysetRow<TResult>
{
    public const int MaxKeys = 6;

    public TResult Item { get; set; } = default!;

    public object? K0 { get; set; }
    public object? K1 { get; set; }
    public object? K2 { get; set; }
    public object? K3 { get; set; }
    public object? K4 { get; set; }
    public object? K5 { get; set; }

    public object?[] GetKeys(int count)
    {
        var keys = new object?[count];
        for (int i = 0; i < count; i++)
        {
            keys[i] = i switch
            {
                0 => K0,
                1 => K1,
                2 => K2,
                3 => K3,
                4 => K4,
                _ => K5
            };
        }

        return keys;
    }
}

/// <summary>
/// Wraps a projection selector so a single SQL statement returns the projected columns AND the sort-key columns:
/// <c>x =&gt; new KeysetRow { Item = selector(x), K0 = (object)x.CreatedAt, K1 = (object)x.Id }</c>.
/// The selector body is inlined (same parameter), so EF Core translates the whole shape into one SELECT.
/// </summary>
internal static class KeysetProjection
{
    private static readonly MethodInfo SelectMethod = typeof(Queryable).GetMethods()
        .First(m => m.Name == nameof(Queryable.Select)
                    && m.GetParameters()[1].ParameterType.GetGenericArguments()[0].GetGenericArguments().Length == 2);

    public static bool CanProject(QryxSortPlan plan) => !plan.IsEmpty && plan.Keys.Count <= KeysetRow<object>.MaxKeys;

    public static IQueryable<KeysetRow<TResult>> Select<TEntity, TResult>(
        IQueryable<TEntity> source,
        Expression<Func<TEntity, TResult>> selector,
        QryxSortPlan plan)
    {
        var parameter = selector.Parameters[0];
        var rowType = typeof(KeysetRow<TResult>);

        var bindings = new List<MemberBinding>(plan.Keys.Count + 1)
        {
            Expression.Bind(rowType.GetProperty(nameof(KeysetRow<TResult>.Item))!, selector.Body)
        };

        for (int i = 0; i < plan.Keys.Count; i++)
        {
            var member = ExpressionHelper.BuildMemberAccess(parameter, plan.Keys[i].Path);
            bindings.Add(Expression.Bind(rowType.GetProperty("K" + i)!, Expression.Convert(member, typeof(object))));
        }

        var lambda = Expression.Lambda(Expression.MemberInit(Expression.New(rowType), bindings), parameter);
        var call = Expression.Call(
            SelectMethod.MakeGenericMethod(typeof(TEntity), rowType),
            source.Expression,
            Expression.Quote(lambda));

        return (IQueryable<KeysetRow<TResult>>)source.Provider.CreateQuery(call);
    }
}
