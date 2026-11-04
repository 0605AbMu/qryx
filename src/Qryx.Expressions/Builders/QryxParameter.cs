using System.Collections.Concurrent;
using System.ComponentModel;
using System.Linq.Expressions;
using System.Reflection;

namespace Qryx.Expressions.Builders;

/// <summary>
/// Strongly-typed value holder used to inject filter values into expression trees.
/// EF Core treats member access on a holder instance like a closure-captured variable,
/// so the value is sent as a SQL parameter (@p) instead of being inlined as a literal.
/// This keeps the EF Core compiled query cache and the database plan cache stable
/// across requests with different filter values.
/// </summary>
/// <typeparam name="T">Exact CLR type of the target entity member.</typeparam>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class QryxParameter<T>(T value)
{
    internal static readonly FieldInfo ValueField = typeof(QryxParameter<T>).GetField(nameof(Value))!;

    public readonly T Value = value;
}

/// <summary>
/// Zero-lookup factory for parameterized values when the value type is known at compile time
/// (used by Qryx.SourceGenerator output): no dictionary lookup, no boxing.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class QryxParameters
{
    /// <summary>
    /// Builds <c>holder.Value</c> (a SQL parameter for EF Core) typed as <paramref name="targetType"/>.
    /// When <typeparamref name="T"/> differs (e.g. <c>int</c> value for an <c>int?</c> column) a Convert node is added;
    /// EF Core evaluates it client-side, so the result is still a single parameter.
    /// </summary>
    public static Expression Create<T>(T value, Type targetType)
    {
        Expression access = Expression.Field(
            Expression.Constant(new QryxParameter<T>(value), typeof(QryxParameter<T>)),
            QryxParameter<T>.ValueField);

        return access.Type == targetType ? access : Expression.Convert(access, targetType);
    }

    /// <summary>Builds <c>holder.Value</c> (a SQL parameter for EF Core) typed exactly as <typeparamref name="T"/>.</summary>
    public static Expression Create<T>(T value) =>
        Expression.Field(
            Expression.Constant(new QryxParameter<T>(value), typeof(QryxParameter<T>)),
            QryxParameter<T>.ValueField);

    /// <summary>
    /// Extracts the member access of <paramref name="selector"/> (e.g. <c>x =&gt; x.Department.Name</c>) rebound to the
    /// shared entity parameter of <see cref="FilterBuilder.EntityParameter{TEntity}"/>. Intended for one-time
    /// static initialization of generated query plans: the C# compiler validates the member path, including
    /// non-public members visible to the generated code.
    /// </summary>
    public static Expression Member<TEntity, TProperty>(Expression<Func<TEntity, TProperty>> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        var target = FilterBuilder.EntityParameter<TEntity>();
        return new ParameterRebinder(selector.Parameters[0], target).Visit(selector.Body)!;
    }

    private sealed class ParameterRebinder(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }
}

/// <summary>
/// Pre-compiled, zero-reflection factory that wraps a value into <see cref="QryxParameter{T}"/>
/// and produces the parameterized member access expression.
/// Resolve once per target type (e.g. while building a filter plan) and reuse on every request.
/// </summary>
internal sealed class ParameterAccessor
{
    private static readonly ConcurrentDictionary<Type, ParameterAccessor> Cache = new();

    private readonly Func<object?, object> _factory;
    private readonly Type _holderType;
    private readonly FieldInfo _valueField;

    public Type ValueType { get; }

    private ParameterAccessor(Type valueType)
    {
        ValueType = valueType;
        _holderType = typeof(QryxParameter<>).MakeGenericType(valueType);
        _valueField = _holderType.GetField(nameof(QryxParameter<object>.Value))!;

        var input = Expression.Parameter(typeof(object), "value");
        var ctor = _holderType.GetConstructor([valueType])!;
        var body = Expression.New(ctor, Expression.Convert(input, valueType));
        _factory = Expression.Lambda<Func<object?, object>>(body, input).Compile();
    }

    public static ParameterAccessor For(Type valueType) =>
        Cache.GetOrAdd(valueType, static t => new ParameterAccessor(t));

    /// <summary>
    /// Builds <c>holder.Value</c> member access. The value must already be assignable to <see cref="ValueType"/>.
    /// </summary>
    public Expression Build(object? value) =>
        Expression.Field(Expression.Constant(_factory(value), _holderType), _valueField);
}
