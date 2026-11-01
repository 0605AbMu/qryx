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

    private static readonly MethodInfo StringConcatMethod =
        typeof(string).GetMethod(nameof(string.Concat), [typeof(string), typeof(string)])!;

    private static readonly MethodInfo StringConcat3Method =
        typeof(string).GetMethod(nameof(string.Concat), [typeof(string), typeof(string), typeof(string)])!;

    private static readonly Lazy<MethodInfo?> EfILikeMethod = new(FindEfILikeMethod);
    private static readonly Lazy<Expression?> EfFunctionsExpr = new(FindEfFunctionsInstance);

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

    public static bool IsPostgreSqlProvider(IQueryProvider? provider)
    {
        if (provider == null) return false;

        try
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var compiler = provider.GetType().GetField("_queryCompiler", flags)?.GetValue(provider);
            if (compiler != null)
            {
                var generator = compiler.GetType().GetField("_compiledQueryCacheKeyGenerator", flags)?.GetValue(compiler);
                if (generator != null && generator.GetType().FullName?.Contains("Npgsql") == true)
                {
                    return true;
                }
            }
        }
        catch { }

        return false;
    }

    public static Expression BuildStartsWith(
        Expression member,
        Expression constant,
        bool isInvariant,
        bool isPostgreSql)
    {
        var nullGuard = Expression.NotEqual(member, Expression.Constant(null, typeof(string)));

        if (!isInvariant)
        {
            var call = Expression.Call(member, StringStartsWithMethod, constant);
            return Expression.AndAlso(nullGuard, call);
        }

        if (isPostgreSql)
        {
            var ilikeMethod = EfILikeMethod.Value;
            var functionsExpr = EfFunctionsExpr.Value;
            if (ilikeMethod != null && functionsExpr != null)
            {
                var patternExpr = BuildPatternConcat(constant, prefix: null, suffix: "%");
                return Expression.Call(ilikeMethod, functionsExpr, member, patternExpr);
            }
        }

        var lowerMember = Expression.Call(member, StringToLowerMethod);
        var lowerConst = constant is ConstantExpression { Value: string s }
            ? Expression.Constant(s.ToLowerInvariant(), typeof(string))
            : (Expression)Expression.Call(constant, StringToLowerMethod);

        var lowerCall = Expression.Call(lowerMember, StringStartsWithMethod, lowerConst);
        return Expression.AndAlso(nullGuard, lowerCall);
    }

    public static Expression BuildEndsWith(
        Expression member,
        Expression constant,
        bool isInvariant,
        bool isPostgreSql)
    {
        var nullGuard = Expression.NotEqual(member, Expression.Constant(null, typeof(string)));

        if (!isInvariant)
        {
            var call = Expression.Call(member, StringEndsWithMethod, constant);
            return Expression.AndAlso(nullGuard, call);
        }

        if (isPostgreSql)
        {
            var ilikeMethod = EfILikeMethod.Value;
            var functionsExpr = EfFunctionsExpr.Value;
            if (ilikeMethod != null && functionsExpr != null)
            {
                var patternExpr = BuildPatternConcat(constant, prefix: "%", suffix: null);
                return Expression.Call(ilikeMethod, functionsExpr, member, patternExpr);
            }
        }

        var lowerMember = Expression.Call(member, StringToLowerMethod);
        var lowerConst = constant is ConstantExpression { Value: string s }
            ? Expression.Constant(s.ToLowerInvariant(), typeof(string))
            : (Expression)Expression.Call(constant, StringToLowerMethod);

        var lowerCall = Expression.Call(lowerMember, StringEndsWithMethod, lowerConst);
        return Expression.AndAlso(nullGuard, lowerCall);
    }

    public static Expression BuildContains(
        Expression member,
        Expression constant,
        bool isInvariant,
        bool isPostgreSql)
    {
        var nullGuard = Expression.NotEqual(member, Expression.Constant(null, typeof(string)));

        if (!isInvariant)
        {
            var call = Expression.Call(member, StringContainsMethod, constant);
            return Expression.AndAlso(nullGuard, call);
        }

        if (isPostgreSql)
        {
            var ilikeMethod = EfILikeMethod.Value;
            var functionsExpr = EfFunctionsExpr.Value;
            if (ilikeMethod != null && functionsExpr != null)
            {
                var patternExpr = BuildPatternConcat(constant, prefix: "%", suffix: "%");
                return Expression.Call(ilikeMethod, functionsExpr, member, patternExpr);
            }
        }

        var lowerMember = Expression.Call(member, StringToLowerMethod);
        var lowerConst = constant is ConstantExpression { Value: string s }
            ? Expression.Constant(s.ToLowerInvariant(), typeof(string))
            : (Expression)Expression.Call(constant, StringToLowerMethod);

        var lowerCall = Expression.Call(lowerMember, StringContainsMethod, lowerConst);
        return Expression.AndAlso(nullGuard, lowerCall);
    }

    private static Expression BuildPatternConcat(Expression valueExpr, string? prefix, string? suffix)
    {
        if (valueExpr is ConstantExpression { Value: string strVal })
        {
            var combined = $"{prefix ?? string.Empty}{strVal}{suffix ?? string.Empty}";
            return Expression.Constant(combined, typeof(string));
        }

        if (prefix != null && suffix != null)
        {
            return Expression.Call(StringConcat3Method, Expression.Constant(prefix), valueExpr, Expression.Constant(suffix));
        }

        if (prefix != null)
        {
            return Expression.Call(StringConcatMethod, Expression.Constant(prefix), valueExpr);
        }

        if (suffix != null)
        {
            return Expression.Call(StringConcatMethod, valueExpr, Expression.Constant(suffix));
        }

        return valueExpr;
    }

    public static Expression BuildInExpression(Expression member, object collection)
    {
        var memberType = member.Type;
        var underlyingMemberType = Nullable.GetUnderlyingType(memberType);
        var targetType = underlyingMemberType ?? memberType;

        var elementType = GetCollectionElementType(collection.GetType());
        var enumerable = (IEnumerable)collection;

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

    private static MethodInfo? FindEfILikeMethod()
    {
        try { Assembly.Load("Npgsql.EntityFrameworkCore.PostgreSQL"); } catch { }
        return AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
            .FirstOrDefault(t => t.Name == "NpgsqlDbFunctionsExtensions")
            ?.GetMethods()
            .FirstOrDefault(m => m.Name == "ILike" && m.GetParameters().Length == 3);
    }

    private static Expression? FindEfFunctionsInstance()
    {
        try { Assembly.Load("Microsoft.EntityFrameworkCore"); } catch { }
        var efType = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
            .FirstOrDefault(t => t.FullName == "Microsoft.EntityFrameworkCore.EF");

        var prop = efType?.GetProperty("Functions", BindingFlags.Public | BindingFlags.Static);
        return prop != null ? Expression.Property(null, prop) : null;
    }
}
