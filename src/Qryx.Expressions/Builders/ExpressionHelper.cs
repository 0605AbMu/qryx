using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Qryx.Core.Constants;
using Qryx.Core.Exceptions;

namespace Qryx.Expressions.Builders;

/// <summary>
/// Utility methods for building dynamic expression trees.
/// </summary>
public static class ExpressionHelper
{
    private static readonly MethodInfo StringToUpperMethod =
        typeof(string).GetMethod(nameof(string.ToUpper), Type.EmptyTypes)!;

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
    private static readonly Lazy<MethodInfo?> EfILikeWithEscapeMethod = new(FindEfILikeWithEscapeMethod);
    private static readonly ConstantExpression LikeEscapeCharacter = Expression.Constant("\\", typeof(string));
    private static readonly Lazy<NpgsqlFullTextMethods?> NpgsqlFullText = new(FindNpgsqlFullTextMethods);
    private static readonly Lazy<MethodInfo?> NpgsqlJsonContainsMethod = new(FindNpgsqlJsonContainsMethod);

    private static readonly MethodInfo EnumerableContainsDefinition =
        typeof(Enumerable).GetMethods().First(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == 2);

    private static readonly ParameterAccessor StringParameter = ParameterAccessor.For(typeof(string));
    private static readonly ConcurrentDictionary<Type, InCollectionAccessor> InCollectionCache = new();

    private static readonly ConcurrentDictionary<(Type Type, string Path), PropertyInfo[]> PropertyChainCache = new();

    public static Expression BuildMemberAccess(Expression parameter, string propertyPath)
    {
        var chain = PropertyChainCache.GetOrAdd((parameter.Type, propertyPath), static key =>
        {
            var (type, path) = key;
            var parts = path.Split('.');
            var props = new PropertyInfo[parts.Length];
            var currentType = type;

            for (int i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                var prop = currentType.GetProperty(part, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                           ?? throw new InvalidOperationException($"Property '{part}' not found on type '{currentType.FullName}'.");
                props[i] = prop;
                currentType = prop.PropertyType;
            }

            return props;
        });

        Expression current = parameter;
        for (int i = 0; i < chain.Length; i++)
        {
            current = Expression.Property(current, chain[i]);
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

    /// <summary>
    /// Builds a parameterized value expression (<c>holder.Value</c>) of exactly <paramref name="targetType"/>.
    /// Unlike <see cref="BuildConstantExpression"/>, EF Core translates it into a SQL parameter (@p)
    /// instead of an inlined literal, so the compiled query is reused for every value.
    /// Falls back to <see cref="BuildConstantExpression"/> for values that cannot be converted directly.
    /// </summary>
    /// <exception cref="QryxRequestException">The value cannot be converted to <paramref name="targetType"/>.</exception>
    public static Expression BuildParameter(object? value, Type targetType)
    {
        ArgumentNullException.ThrowIfNull(targetType);
        return BuildParameter(value, ParameterAccessor.For(targetType), "value");
    }

    /// <exception cref="QryxRequestException">The value cannot be converted to the accessor's value type.</exception>
    internal static Expression BuildParameter(object? value, ParameterAccessor accessor, string parameterName)
    {
        return TryBuildParameter(value, accessor, out var expression)
            ? expression
            : throw InvalidValue(parameterName, value, accessor.ValueType);
    }

    internal static bool TryBuildParameter(
        object? value,
        ParameterAccessor accessor,
        [NotNullWhen(true)] out Expression? expression)
    {
        if (TryConvertValue(value, accessor.ValueType, out var converted))
        {
            expression = accessor.Build(converted);
            return true;
        }

        // Conversion operators (e.g. DateTime -> DateTimeOffset): parameter of the value's own type + Convert.
        // EF Core evaluates the Convert node client-side, so the result is still a single SQL parameter.
        if (value != null)
        {
            try
            {
                expression = Expression.Convert(ParameterAccessor.For(value.GetType()).Build(value), accessor.ValueType);
                return true;
            }
            catch (InvalidOperationException)
            {
                // No coercion operator -> invalid client value
            }
        }

        expression = null;
        return false;
    }

    private static readonly ConcurrentDictionary<Type, bool> FlagsEnumCache = new();

    private static bool IsFlagsEnum(Type enumType) =>
        FlagsEnumCache.GetOrAdd(enumType, static t => t.IsDefined(typeof(FlagsAttribute), inherit: false));

    /// <summary>Creates a client-error exception describing an unparsable query value.</summary>
    internal static QryxRequestException InvalidValue(string parameterName, object? value, Type targetType)
    {
        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
        var expected = underlying.IsEnum
            ? $"one of: {string.Join(", ", Enum.GetNames(underlying))}"
            : underlying.Name;

        var text = value?.ToString() ?? "null";
        if (text.Length > 64)
            text = string.Concat(text.AsSpan(0, 64), "...");

        return new QryxRequestException(parameterName, $"Value '{text}' is not valid for '{parameterName}'. Expected {expected}.");
    }

    /// <summary>
    /// Converts a raw value to <paramref name="targetType"/> using the same rules as <see cref="BuildConstantExpression"/>.
    /// </summary>
    private static bool TryConvertValue(object? value, Type targetType, out object? converted)
    {
        if (value == null)
        {
            converted = null;
            return !targetType.IsValueType || Nullable.GetUnderlyingType(targetType) != null;
        }

        var underlyingTargetType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        var valueType = value.GetType();

        if (valueType == underlyingTargetType)
        {
            converted = value;
            return true;
        }

        try
        {
            if (underlyingTargetType.IsEnum)
            {
                var isFlags = IsFlagsEnum(underlyingTargetType);

                // "Active,Blocked" would parse as the flags value Active|Blocked = a different single member
                // of a non-[Flags] enum (silently wrong filter), so it is rejected as an invalid client value.
                if (!isFlags && value is string text && text.Contains(','))
                {
                    converted = null;
                    return false;
                }

                var parsed = value is string strValue
                    ? Enum.Parse(underlyingTargetType, strValue, ignoreCase: true)
                    : Enum.ToObject(underlyingTargetType, value);

                // Undefined numeric values (e.g. 7) are client errors for non-[Flags] enums, as in the model binders
                if (!isFlags && !Enum.IsDefined(underlyingTargetType, parsed))
                {
                    converted = null;
                    return false;
                }

                converted = parsed;
                return true;
            }

            if (underlyingTargetType == typeof(Guid) && value is string guidStr)
            {
                converted = Guid.Parse(guidStr);
                return true;
            }

            if (underlyingTargetType.IsAssignableFrom(valueType))
            {
                converted = value;
                return true;
            }

            converted = Convert.ChangeType(value, underlyingTargetType);
            return true;
        }
        catch
        {
            converted = null;
            return false;
        }
    }

    /// <summary>
    /// Detects whether the queryable targets Npgsql (PostgreSQL).
    /// EF Core creates a new IQueryProvider per DbContext, so caching by provider instance misses on every request.
    /// Instead, private field reads use compiled getters (cached per declaring type) and the
    /// "is Npgsql" decision is cached per service type: steady-state cost is a few dictionary lookups, zero reflection.
    /// </summary>
    public static bool IsPostgreSqlProvider(IQueryProvider? provider)
    {
        if (provider == null) return false;

        try
        {
            var compiler = ReadField(provider, "_queryCompiler");
            if (compiler == null) return false;

            var generator = ReadField(compiler, "_compiledQueryCacheKeyGenerator");
            if (generator != null && IsNpgsqlType(generator.GetType()))
                return true;

            var evalFilter = ReadField(compiler, "_evaluatableExpressionFilter");
            if (evalFilter != null && IsNpgsqlType(evalFilter.GetType()))
                return true;

            return IsNpgsqlType(compiler.GetType());
        }
        catch
        {
            return false;
        }
    }

    private static readonly ConcurrentDictionary<(Type Type, string Field), Func<object, object?>?> FieldReaderCache = new();
    private static readonly ConcurrentDictionary<Type, bool> NpgsqlTypeCache = new();

    private static bool IsNpgsqlType(Type type) =>
        NpgsqlTypeCache.GetOrAdd(type, static t => t.FullName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true);

    private static object? ReadField(object instance, string fieldName)
    {
        var reader = FieldReaderCache.GetOrAdd((instance.GetType(), fieldName), static key =>
        {
            var field = FindInstanceField(key.Type, key.Field);
            if (field == null)
                return null;

            var obj = Expression.Parameter(typeof(object), "obj");
            var access = Expression.Field(Expression.Convert(obj, key.Type), field);
            return Expression.Lambda<Func<object, object?>>(Expression.Convert(access, typeof(object)), obj).Compile();
        });

        return reader?.Invoke(instance);
    }

    private static FieldInfo? FindInstanceField(Type type, string fieldName)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        for (var current = type; current != null; current = current.BaseType)
        {
            var field = current.GetField(fieldName, flags);
            if (field != null)
                return field;
        }
        return null;
    }

    public static Expression BuildStartsWith(
        Expression member,
        Expression constant,
        bool isInvariant,
        bool isPostgreSql)
    {
        // 1. Invariant belgilanmagan bo'lsa -> Shunchaki direct LIKE (Starts with)
        if (!isInvariant)
        {
            return Expression.Call(member, StringStartsWithMethod, constant);
        }

        // 2. Invariant belgilangan bo'lsa:
        // A) Faqat PostgreSQL uchun -> Native ILIKE
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

        // B) Boshqa DB provayderlar uchun (SQL Server, SQLite, Oracle) -> UPPER() va ToUpperInvariant()
        var upperMember = Expression.Call(member, StringToUpperMethod);
        var upperConst = constant is ConstantExpression { Value: string s }
            ? Expression.Constant(s.ToUpperInvariant(), typeof(string))
            : (Expression)Expression.Call(constant, StringToUpperMethod);

        return Expression.Call(upperMember, StringStartsWithMethod, upperConst);
    }

    public static Expression BuildEndsWith(
        Expression member,
        Expression constant,
        bool isInvariant,
        bool isPostgreSql)
    {
        // 1. Invariant belgilanmagan bo'lsa -> Shunchaki direct LIKE (Ends with)
        if (!isInvariant)
        {
            return Expression.Call(member, StringEndsWithMethod, constant);
        }

        // 2. Invariant belgilangan bo'lsa:
        // A) Faqat PostgreSQL uchun -> Native ILIKE
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

        // B) Boshqa DB provayderlar uchun (SQL Server, SQLite, Oracle) -> UPPER() va ToUpperInvariant()
        var upperMember = Expression.Call(member, StringToUpperMethod);
        var upperConst = constant is ConstantExpression { Value: string s }
            ? Expression.Constant(s.ToUpperInvariant(), typeof(string))
            : (Expression)Expression.Call(constant, StringToUpperMethod);

        return Expression.Call(upperMember, StringEndsWithMethod, upperConst);
    }

    public static Expression BuildContains(
        Expression member,
        Expression constant,
        bool isInvariant,
        bool isPostgreSql)
    {
        // 1. Invariant belgilanmagan bo'lsa -> Shunchaki direct LIKE (Contains)
        if (!isInvariant)
        {
            return Expression.Call(member, StringContainsMethod, constant);
        }

        // 2. Invariant belgilangan bo'lsa:
        // A) Faqat PostgreSQL uchun -> Native ILIKE
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

        // B) Boshqa DB provayderlar uchun (SQL Server, SQLite, Oracle) -> UPPER() va ToUpperInvariant()
        var upperMember = Expression.Call(member, StringToUpperMethod);
        var upperConst = constant is ConstantExpression { Value: string s }
            ? Expression.Constant(s.ToUpperInvariant(), typeof(string))
            : (Expression)Expression.Call(constant, StringToUpperMethod);

        return Expression.Call(upperMember, StringContainsMethod, upperConst);
    }

    /// <summary>
    /// Parameterized StartsWith: the value is sent as a SQL parameter (see <see cref="BuildParameter(object?, Type)"/>).
    /// </summary>
    public static Expression BuildStartsWith(Expression member, string value, bool isInvariant, bool isPostgreSql)
        => BuildStringMatch(member, value, StringMatchKind.StartsWith, isInvariant, isPostgreSql);

    /// <summary>
    /// Parameterized EndsWith: the value is sent as a SQL parameter (see <see cref="BuildParameter(object?, Type)"/>).
    /// </summary>
    public static Expression BuildEndsWith(Expression member, string value, bool isInvariant, bool isPostgreSql)
        => BuildStringMatch(member, value, StringMatchKind.EndsWith, isInvariant, isPostgreSql);

    /// <summary>
    /// Parameterized Contains: the value is sent as a SQL parameter (see <see cref="BuildParameter(object?, Type)"/>).
    /// </summary>
    public static Expression BuildContains(Expression member, string value, bool isInvariant, bool isPostgreSql)
        => BuildStringMatch(member, value, StringMatchKind.Contains, isInvariant, isPostgreSql);

    private enum StringMatchKind
    {
        StartsWith,
        EndsWith,
        Contains
    }

    private static Expression BuildStringMatch(
        Expression member,
        string value,
        StringMatchKind kind,
        bool isInvariant,
        bool isPostgreSql)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(value);

        var method = kind switch
        {
            StringMatchKind.StartsWith => StringStartsWithMethod,
            StringMatchKind.EndsWith => StringEndsWithMethod,
            _ => StringContainsMethod
        };

        // 1. Invariant belgilanmagan bo'lsa -> direct LIKE (EF Core pattern'ni o'zi escape qiladi)
        if (!isInvariant)
        {
            return Expression.Call(member, method, StringParameter.Build(value));
        }

        // 2. PostgreSQL -> native ILIKE. Pattern klient tomonda tayyorlanadi va parametr sifatida yuboriladi.
        // Foydalanuvchi kiritgan '%', '_' va '\' wildcard sifatida ishlamasligi uchun escape qilinadi.
        if (isPostgreSql)
        {
            var functionsExpr = EfFunctionsExpr.Value;
            var ilikeEscapeMethod = EfILikeWithEscapeMethod.Value;
            var ilikeMethod = EfILikeMethod.Value;

            if (functionsExpr != null && (ilikeEscapeMethod != null || ilikeMethod != null))
            {
                // ILike(match, pattern) overload'i "ESCAPE ''" generatsiya qiladi (escape o'chadi),
                // shuning uchun faqat escape belgisini aniq beradigan overload mavjud bo'lsa escape qilinadi.
                var escaped = ilikeEscapeMethod != null ? EscapeLikePattern(value) : value;
                var pattern = kind switch
                {
                    StringMatchKind.StartsWith => escaped + "%",
                    StringMatchKind.EndsWith => "%" + escaped,
                    _ => "%" + escaped + "%"
                };

                return ilikeEscapeMethod != null
                    ? Expression.Call(ilikeEscapeMethod, functionsExpr, member, StringParameter.Build(pattern), LikeEscapeCharacter)
                    : Expression.Call(ilikeMethod!, functionsExpr, member, StringParameter.Build(pattern));
            }
        }

        // 3. Boshqa DB provayderlar (SQL Server, SQLite, Oracle) -> UPPER(column) LIKE @p (qiymat oldindan ToUpperInvariant)
        var upperMember = Expression.Call(member, StringToUpperMethod);
        return Expression.Call(upperMember, method, StringParameter.Build(value.ToUpperInvariant()));
    }

    /// <summary>
    /// Escapes LIKE/ILIKE wildcards ('%', '_') and the escape character itself with '\'
    /// so user input matches literally. Use together with an explicit ESCAPE '\' clause
    /// (e.g. <c>EF.Functions.ILike(column, pattern, "\\")</c>).
    /// </summary>
    public static string EscapeLikePattern(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.AsSpan().IndexOfAny('\\', '%', '_') < 0)
            return value;

        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            if (c is '\\' or '%' or '_')
                sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
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

    public static Expression BuildFts(
        Expression member,
        Expression constant,
        bool isPostgreSql)
    {
        return BuildContains(member, constant, isInvariant: true, isPostgreSql);
    }

    public static Expression BuildJsonContains(
        Expression member,
        Expression constant,
        bool isPostgreSql)
    {
        return BuildContains(member, constant, isInvariant: false, isPostgreSql);
    }

    /// <summary>
    /// Full-text search. PostgreSQL: <c>column @@ websearch_to_tsquery(@p)</c> for <c>NpgsqlTsVector</c> columns,
    /// or <c>to_tsvector(column) @@ websearch_to_tsquery(@p)</c> for text columns. websearch_to_tsquery accepts any
    /// user input (quotes, OR, -exclusion) without syntax errors.
    /// Other providers: case-insensitive Contains fallback.
    /// </summary>
    public static Expression BuildFts(Expression member, string value, bool isPostgreSql)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(value);

        if (isPostgreSql && NpgsqlFullText.Value is { } fts && EfFunctionsExpr.Value is { } functions)
        {
            Expression? vector = member.Type == fts.TsVectorType
                ? member
                : member.Type == typeof(string)
                    ? Expression.Call(fts.ToTsVector, functions, member)
                    : null;

            if (vector != null)
            {
                var query = Expression.Call(fts.WebSearchToTsQuery, functions, StringParameter.Build(value));
                return Expression.Call(fts.Matches, vector, query);
            }
        }

        return BuildContains(member, value, isInvariant: true, isPostgreSql);
    }

    /// <summary>
    /// JSON search. PostgreSQL (jsonb column): a JSON object/array value (e.g. <c>{"tag":"x"}</c>) becomes containment
    /// <c>column @&gt; @p</c>; a plain text value becomes top-level key existence <c>column ? @key</c>. Both are GIN-index friendly.
    /// Other providers: LIKE Contains fallback.
    /// </summary>
    public static Expression BuildJsonContains(Expression member, string value, bool isPostgreSql)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(value);

        if (isPostgreSql && TryParseJsonContainer(value, out var json) &&
            NpgsqlJsonContainsMethod.Value is { } jsonContains && EfFunctionsExpr.Value is { } functions)
        {
            // JsonElement parametri Npgsql'da jsonb sifatida yuboriladi (string -> text bo'lib "jsonb @> text" xatosi beradi)
            return Expression.Call(
                jsonContains,
                functions,
                Expression.Convert(member, typeof(object)),
                Expression.Convert(JsonElementParameter.Build(json), typeof(object)));
        }

        // PostgreSQL + oddiy matn -> yuqori darajadagi kalit mavjudligi: column ? @key (jsonb ustunida LIKE ishlamaydi)
        if (isPostgreSql && NpgsqlJsonExistsMethod.Value is { } jsonExists && EfFunctionsExpr.Value is { } fn)
        {
            return Expression.Call(
                jsonExists,
                fn,
                Expression.Convert(member, typeof(object)),
                StringParameter.Build(value));
        }

        return BuildContains(member, value, isInvariant: false, isPostgreSql);
    }

    private static readonly Lazy<MethodInfo?> NpgsqlJsonExistsMethod = new(() =>
        NpgsqlJsonContainsMethod.Value?.DeclaringType?
            .GetMethods()
            .FirstOrDefault(m => m.Name == "JsonExists" && m.GetParameters().Length == 3));

    private static readonly ParameterAccessor JsonElementParameter = ParameterAccessor.For(typeof(System.Text.Json.JsonElement));

    private static bool TryParseJsonContainer(string value, out System.Text.Json.JsonElement element)
    {
        element = default;
        var trimmed = value.AsSpan().TrimStart();
        if (trimmed.IsEmpty || (trimmed[0] != '{' && trimmed[0] != '['))
            return false;

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(value);
            element = document.RootElement.Clone(); // independent of the pooled document buffer
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private sealed record NpgsqlFullTextMethods(Type TsVectorType, MethodInfo ToTsVector, MethodInfo WebSearchToTsQuery, MethodInfo Matches);

    private static NpgsqlFullTextMethods? FindNpgsqlFullTextMethods()
    {
        var assembly = EfILikeMethod.Value?.DeclaringType?.Assembly;
        var functionsType = assembly?.GetType("Microsoft.EntityFrameworkCore.NpgsqlFullTextSearchDbFunctionsExtensions");
        var linqType = assembly?.GetType("Microsoft.EntityFrameworkCore.NpgsqlFullTextSearchLinqExtensions");
        if (functionsType == null || linqType == null)
            return null;

        var toTsVector = functionsType.GetMethods().FirstOrDefault(m => m.Name == "ToTsVector" && m.GetParameters().Length == 2);
        var webSearch = functionsType.GetMethods().FirstOrDefault(m => m.Name == "WebSearchToTsQuery" && m.GetParameters().Length == 2);
        if (toTsVector == null || webSearch == null)
            return null;

        var matches = linqType.GetMethods().FirstOrDefault(m =>
            m.Name == "Matches" &&
            m.GetParameters() is { Length: 2 } p &&
            p[1].ParameterType == webSearch.ReturnType);

        return matches == null
            ? null
            : new NpgsqlFullTextMethods(matches.GetParameters()[0].ParameterType, toTsVector, webSearch, matches);
    }

    private static MethodInfo? FindNpgsqlJsonContainsMethod()
    {
        return EfILikeMethod.Value?.DeclaringType?.Assembly
            .GetType("Microsoft.EntityFrameworkCore.NpgsqlJsonDbFunctionsExtensions")?
            .GetMethods()
            .FirstOrDefault(m => m.Name == "JsonContains" && m.GetParameters().Length == 3);
    }

    /// <summary>
    /// Builds a parameterized <c>list.Contains(member)</c> expression.
    /// The whole list is sent as one collection parameter, so the SQL text does not depend on the values.
    /// </summary>
    private static readonly ConcurrentDictionary<Type, MethodInfo> EnumerableContainsCache = new();

    /// <summary>Closed <c>Enumerable.Contains&lt;T&gt;(IEnumerable&lt;T&gt;, T)</c> (cached; resolve once, e.g. in a static field).</summary>
    public static MethodInfo GetEnumerableContainsMethod(Type elementType)
    {
        ArgumentNullException.ThrowIfNull(elementType);
        return EnumerableContainsCache.GetOrAdd(elementType, static t => EnumerableContainsDefinition.MakeGenericMethod(t));
    }

    /// <summary>
    /// Builds a binary comparison. Relational operators on enums compare the underlying numeric type
    /// (<see cref="Expression.GreaterThan(Expression, Expression)"/> is not defined for enums); EF Core translates it unchanged.
    /// </summary>
    public static Expression BuildComparison(ExpressionType comparison, Expression member, Expression value)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(value);

        if (comparison is ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual
            or ExpressionType.LessThan or ExpressionType.LessThanOrEqual)
        {
            var type = Nullable.GetUnderlyingType(member.Type) ?? member.Type;
            if (type.IsEnum)
            {
                var underlying = Enum.GetUnderlyingType(type);
                var target = member.Type == type ? underlying : typeof(Nullable<>).MakeGenericType(underlying);
                member = Expression.Convert(member, target);
                value = Expression.Convert(value, target);
            }
        }

        return Expression.MakeBinary(comparison, member, value);
    }

    /// <exception cref="QryxRequestException">
    /// The collection has more than <see cref="QryxConstants.DefaultMaxInCollectionSize"/> items or an item is invalid.
    /// </exception>
    public static Expression BuildInExpression(Expression member, object collection)
        => BuildInExpression(member, collection, "collection");

    internal static Expression BuildInExpression(Expression member, object collection, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(collection);

        // DoS guard: katta ro'yxat jimgina kesilmaydi (noto'g'ri natija), balki rad etiladi
        if (collection is ICollection { Count: > QryxConstants.DefaultMaxInCollectionSize })
            throw TooManyValues(parameterName);

        var memberType = member.Type;
        var underlyingMemberType = Nullable.GetUnderlyingType(memberType);
        var targetType = underlyingMemberType ?? memberType;

        var accessor = InCollectionCache.GetOrAdd(memberType, static t => new InCollectionAccessor(t));
        var enumerable = (IEnumerable)collection;
        var list = accessor.ListFactory();

        int count = 0;
        foreach (var item in enumerable)
        {
            if (++count > QryxConstants.DefaultMaxInCollectionSize)
                throw TooManyValues(parameterName);

            if (item == null)
            {
                if (underlyingMemberType != null || !memberType.IsValueType)
                    list.Add(null);
                continue;
            }

            if (!TryConvertValue(item, targetType, out var convertedItem))
                throw InvalidValue(parameterName, item, targetType);

            list.Add(convertedItem);
        }

        return Expression.Call(accessor.ContainsMethod, accessor.Parameter.Build(list), member);
    }

    private static QryxRequestException TooManyValues(string parameterName) =>
        new(parameterName, $"'{parameterName}' accepts at most {QryxConstants.DefaultMaxInCollectionSize} values.");

    /// <summary>
    /// Pre-compiled per-member-type pieces for <see cref="BuildInExpression"/>: list factory,
    /// List&lt;T&gt; parameter holder and closed Enumerable.Contains&lt;T&gt;.
    /// </summary>
    private sealed class InCollectionAccessor
    {
        public Func<IList> ListFactory { get; }
        public ParameterAccessor Parameter { get; }
        public MethodInfo ContainsMethod { get; }

        public InCollectionAccessor(Type memberType)
        {
            var listType = typeof(List<>).MakeGenericType(memberType);
            ListFactory = Expression.Lambda<Func<IList>>(Expression.New(listType)).Compile();
            Parameter = ParameterAccessor.For(listType);
            ContainsMethod = EnumerableContainsDefinition.MakeGenericMethod(memberType);
        }
    }

    public static MethodInfo? FindEfILikeMethod()
    {
        // Direct type lookup instead of scanning every loaded assembly's types (slow startup, loader exceptions)
        return FindType("Microsoft.EntityFrameworkCore.NpgsqlDbFunctionsExtensions, Npgsql.EntityFrameworkCore.PostgreSQL")?
            .GetMethods()
            .FirstOrDefault(m => m.Name == "ILike" && m.GetParameters().Length == 3);
    }

    private static Type? FindType(string assemblyQualifiedName)
    {
        try
        {
            return Type.GetType(assemblyQualifiedName, throwOnError: false);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Finds NpgsqlDbFunctionsExtensions.ILike(DbFunctions, string matchExpression, string pattern, string escapeCharacter).
    /// </summary>
    public static MethodInfo? FindEfILikeWithEscapeMethod()
    {
        return EfILikeMethod.Value?.DeclaringType?
            .GetMethods()
            .FirstOrDefault(m => m.Name == "ILike" && m.GetParameters().Length == 4);
    }

    public static Expression? FindEfFunctionsInstance()
    {
        var efType = FindType("Microsoft.EntityFrameworkCore.EF, Microsoft.EntityFrameworkCore");
        var prop = efType?.GetProperty("Functions", BindingFlags.Public | BindingFlags.Static);
        return prop != null ? Expression.Property(null, prop) : null;
    }
}
