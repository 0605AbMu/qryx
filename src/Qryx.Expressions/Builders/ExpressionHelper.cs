using System.Collections;
using System.Linq.Expressions;
using System.Reflection;

namespace Qryx.Expressions.Builders;

/// <summary>
/// Utility methods for building dynamic expression trees.
/// </summary>
public static class ExpressionHelper
{
    private static readonly MethodInfo StringToLowerMethod =
        typeof(string).GetMethod(nameof(string.ToLower), Type.EmptyTypes)!;

    private static readonly MethodInfo StringStartsWithMethod =
        typeof(string).GetMethod(nameof(string.StartsWith), [typeof(string)])!;

    private static readonly MethodInfo StringEndsWithMethod =
        typeof(string).GetMethod(nameof(string.EndsWith), [typeof(string)])!;

    private static readonly MethodInfo StringContainsMethod =
        typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!;

    public static Expression BuildMemberAccess(Expression parameter, string propertyPath)
    {
        Expression current = parameter;
        var parts = propertyPath.Split('.');

        foreach (var part in parts)
        {
            var prop = current.Type.GetProperty(part, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop == null)
            {
                throw new InvalidOperationException($"Property '{part}' not found on type '{current.Type.FullName}'.");
            }
            current = Expression.Property(current, prop);
        }

        return current;
    }

    public static Expression BuildConstantExpression(object? value, Type targetType)
    {
        if (value == null)
        {
            return Expression.Constant(null, targetType);
        }

        var underlyingTargetType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        var valueType = value.GetType();

        // Convert enum if value is string or int
        if (underlyingTargetType.IsEnum)
        {
            if (value is string strValue)
            {
                var enumValue = Enum.Parse(underlyingTargetType, strValue, ignoreCase: true);
                var constExpr = Expression.Constant(enumValue, underlyingTargetType);
                return targetType != underlyingTargetType ? Expression.Convert(constExpr, targetType) : constExpr;
            }
            else
            {
                var enumValue = Enum.ToObject(underlyingTargetType, value);
                var constExpr = Expression.Constant(enumValue, underlyingTargetType);
                return targetType != underlyingTargetType ? Expression.Convert(constExpr, targetType) : constExpr;
            }
        }

        // Convert Guid if string
        if (underlyingTargetType == typeof(Guid) && value is string guidStr)
        {
            var guid = Guid.Parse(guidStr);
            var constExpr = Expression.Constant(guid, typeof(Guid));
            return targetType != underlyingTargetType ? Expression.Convert(constExpr, targetType) : constExpr;
        }

        // Convert numeric or standard types
        if (valueType != underlyingTargetType)
        {
            try
            {
                var converted = Convert.ChangeType(value, underlyingTargetType);
                var constExpr = Expression.Constant(converted, underlyingTargetType);
                return targetType != underlyingTargetType ? Expression.Convert(constExpr, targetType) : constExpr;
            }
            catch
            {
                // Fallback to direct conversion expression
            }
        }

        var rawConstant = Expression.Constant(value, valueType);
        if (valueType != targetType)
        {
            return Expression.Convert(rawConstant, targetType);
        }

        return rawConstant;
    }

    public static Expression BuildStringComparison(
        Expression member,
        Expression constant,
        MethodInfo method,
        bool caseSensitive)
    {
        var nullGuard = Expression.NotEqual(member, Expression.Constant(null, typeof(string)));

        Expression targetMember = member;
        Expression targetConst = constant;

        if (!caseSensitive)
        {
            targetMember = Expression.Call(member, StringToLowerMethod);
            if (constant is ConstantExpression { Value: string constStr })
            {
                targetConst = Expression.Constant(constStr.ToLower(), typeof(string));
            }
            else
            {
                targetConst = Expression.Call(constant, StringToLowerMethod);
            }
        }

        var call = Expression.Call(targetMember, method, targetConst);
        return Expression.AndAlso(nullGuard, call);
    }

    public static Expression BuildStartsWith(Expression member, Expression constant, bool caseSensitive) =>
        BuildStringComparison(member, constant, StringStartsWithMethod, caseSensitive);

    public static Expression BuildEndsWith(Expression member, Expression constant, bool caseSensitive) =>
        BuildStringComparison(member, constant, StringEndsWithMethod, caseSensitive);

    public static Expression BuildContains(Expression member, Expression constant, bool caseSensitive) =>
        BuildStringComparison(member, constant, StringContainsMethod, caseSensitive);

    public static Expression BuildInExpression(Expression member, object collection)
    {
        var memberType = member.Type;
        var underlyingMemberType = Nullable.GetUnderlyingType(memberType);
        var targetType = underlyingMemberType ?? memberType;

        var elementType = GetCollectionElementType(collection.GetType());
        var enumerable = (IEnumerable)collection;

        // Create a typed list matching memberType for exact compatibility
        var listType = typeof(List<>).MakeGenericType(memberType);
        var list = (IList)Activator.CreateInstance(listType)!;

        foreach (var item in enumerable)
        {
            if (item == null)
            {
                if (underlyingMemberType != null || !memberType.IsValueType)
                    list.Add(null);
                continue;
            }

            var itemType = item.GetType();
            object? convertedItem = item;

            if (targetType.IsEnum)
            {
                convertedItem = item is string s
                    ? Enum.Parse(targetType, s, ignoreCase: true)
                    : Enum.ToObject(targetType, item);
            }
            else if (itemType != targetType)
            {
                convertedItem = Convert.ChangeType(item, targetType);
            }

            list.Add(convertedItem);
        }

        var containsMethod = typeof(Enumerable).GetMethods()
            .First(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == 2)
            .MakeGenericMethod(memberType);

        var listConstant = Expression.Constant(list, listType);
        return Expression.Call(containsMethod, listConstant, member);
    }

    private static Type GetCollectionElementType(Type collectionType)
    {
        if (collectionType.IsArray)
            return collectionType.GetElementType()!;

        if (collectionType.IsGenericType && collectionType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            return collectionType.GetGenericArguments()[0];

        foreach (var iface in collectionType.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return iface.GetGenericArguments()[0];
        }

        return typeof(object);
    }
}
