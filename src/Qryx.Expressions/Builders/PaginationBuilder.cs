using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using Qryx.Core.Constants;
using Qryx.Core.Contracts;
using Qryx.Core.Exceptions;
using Qryx.Expressions.Metadata;
using Qryx.Expressions.Options;

namespace Qryx.Expressions.Builders;

/// <summary>
/// Offset and keyset pagination with DoS guards.
/// <para>
/// Keyset cursors are derived from the request's <see cref="QryxSortPlan"/> (sortBy clauses + primary-key
/// tie-breaker): <c>nextCursor</c> = opaque token with the sort fingerprint and the last row's key values. It can be
/// taken from ANY page (offset or keyset) and only continues the ordering it was created for.
/// </para>
/// </summary>
public static class PaginationBuilder
{
    private const string CursorParameterName = "afterCursor";
    private const int MaxCursorLength = 4096;
    private const int MaxCachedKeysetPlans = 2048;

    private static readonly ConcurrentDictionary<(Type EntityType, string Signature), KeysetPlan> KeysetPlanCache = new();

    private static readonly MethodInfo StringCompareMethod =
        typeof(string).GetMethod(nameof(string.Compare), [typeof(string), typeof(string)])!;

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

        var ordered = SortBuilder.EnsureOrdering(source);
        return ordered.Skip(GetSkip(clampedPage, clampedPageSize)).Take(clampedPageSize);
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
    /// Overflow-safe offset <c>(page - 1) * pageSize</c>, limited by <see cref="QryxOptions.MaxOffset"/> of
    /// <see cref="QryxOptions.Global"/>.
    /// </summary>
    /// <exception cref="QryxRequestException">The offset exceeds <see cref="QryxOptions.MaxOffset"/>.</exception>
    public static int GetSkip(int page, int pageSize) => GetSkip(page, pageSize, QryxOptions.Global.MaxOffset);

    /// <summary>
    /// Overflow-safe offset <c>(page - 1) * pageSize</c>. Every skipped row is still read and discarded by the
    /// database, so deep offsets are a DoS vector; offsets above <paramref name="maxOffset"/> are rejected.
    /// </summary>
    /// <param name="maxOffset">Largest allowed offset; <c>null</c> = unlimited.</param>
    /// <exception cref="QryxRequestException">The offset exceeds <paramref name="maxOffset"/>.</exception>
    public static int GetSkip(int page, int pageSize, int? maxOffset)
    {
        long skipLong = ((long)ClampPage(page) - 1) * pageSize;

        if (maxOffset is { } limit && skipLong > limit)
        {
            var maxPage = pageSize > 0 ? limit / pageSize + 1 : 1;
            throw new QryxRequestException(
                nameof(IQryxRequest.Page),
                $"Page {page} is too deep for offset pagination (offset {skipLong} exceeds {limit}). " +
                $"With pageSize {pageSize} the last allowed page is {maxPage}; use keyset pagination (afterCursor) for deeper results.");
        }

        return skipLong > int.MaxValue ? int.MaxValue : (int)skipLong;
    }

    /// <summary>
    /// Keyset pagination for a request's sort: resolves <paramref name="sortBy"/> exactly like
    /// <see cref="SortBuilder.Apply{TEntity}(IQueryable{TEntity}, IQryxRequest{TEntity}, bool)"/> and continues after
    /// <paramref name="afterCursor"/>.
    /// </summary>
    /// <exception cref="QryxRequestException">The cursor is invalid or was created for a different sort.</exception>
    public static IQueryable<TEntity> ApplyKeyset<TEntity>(
        IQueryable<TEntity> source,
        object afterCursor,
        int pageSize,
        string? sortBy,
        bool isDescending,
        Type? dtoType,
        int maxPageSize = QryxConstants.DefaultMaxPageSize)
    {
        var plan = SortBuilder.ResolveSort<TEntity>(sortBy, isDescending, dtoType, QryxOptions.Global.ThrowOnInvalidSortProperty);
        return ApplyKeyset(source, afterCursor, plan, pageSize, maxPageSize);
    }

    /// <summary>
    /// Keyset (seek) pagination: <c>WHERE (k1, k2, ..., pk) &gt; (cursor values)</c> expanded per key direction
    /// (<c>k1 &gt; @v1 OR (k1 = @v1 AND k2 &lt; @v2) OR ...</c>), then ordering by <paramref name="plan"/> and
    /// <c>Take(pageSize)</c>. Cost does not depend on how deep the page is.
    /// </summary>
    /// <exception cref="QryxRequestException">
    /// The cursor is invalid, was created for a different sort, or the sort has nullable keys.
    /// </exception>
    public static IQueryable<TEntity> ApplyKeyset<TEntity>(
        IQueryable<TEntity> source,
        object afterCursor,
        QryxSortPlan plan,
        int pageSize,
        int maxPageSize = QryxConstants.DefaultMaxPageSize)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(afterCursor);
        ArgumentNullException.ThrowIfNull(plan);

        var keyset = GetKeysetPlan<TEntity>(plan);
        if (keyset.UnsupportedReason is { } reason)
            throw new QryxRequestException(CursorParameterName, reason);

        var values = keyset.Decode(afterCursor);
        var seek = keyset.BuildSeekPredicate<TEntity>(values);

        return SortBuilder.ApplySort(source.Where(seek), plan).Take(ClampPageSize(pageSize, maxPageSize));
    }

    /// <summary>True when cursors can be created for <paramref name="plan"/> (every sort key is non-nullable).</summary>
    public static bool SupportsKeyset<TEntity>(QryxSortPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return !plan.IsEmpty && GetKeysetPlan<TEntity>(plan).UnsupportedReason is null;
    }

    /// <summary>
    /// Builds the cursor that continues after <paramref name="lastItem"/> in <paramref name="plan"/>'s ordering,
    /// or null when keyset pagination is not possible for this sort (nullable keys, or a null key value).
    /// </summary>
    public static string? CreateCursor<TEntity>(TEntity lastItem, QryxSortPlan plan)
    {
        ArgumentNullException.ThrowIfNull(lastItem);
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.IsEmpty)
            return null;

        var keyset = GetKeysetPlan<TEntity>(plan);
        if (keyset.UnsupportedReason != null)
            return null;

        var values = new object?[keyset.Getters.Length];
        for (int i = 0; i < values.Length; i++)
            values[i] = keyset.Getters[i](lastItem);

        return keyset.Encode(values);
    }

    /// <summary>
    /// Builds a cursor from already extracted key values (one per <see cref="QryxSortPlan.Keys"/> entry, same order),
    /// e.g. selected next to a projection. Null when keyset pagination is not possible.
    /// </summary>
    public static string? CreateCursorFromValues<TEntity>(QryxSortPlan plan, IReadOnlyList<object?> values)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(values);

        if (plan.IsEmpty || values.Count != plan.KeyArray.Length)
            return null;

        var keyset = GetKeysetPlan<TEntity>(plan);
        return keyset.UnsupportedReason == null ? keyset.Encode(values) : null;
    }

    private static KeysetPlan GetKeysetPlan<TEntity>(QryxSortPlan plan)
    {
        // Keys are canonical whitelisted names, so the key space is bounded; the cap is a safety net only.
        if (KeysetPlanCache.Count > MaxCachedKeysetPlans)
            KeysetPlanCache.Clear();

        return KeysetPlanCache.GetOrAdd((typeof(TEntity), plan.Signature), static (key, p) => new KeysetPlan(key.EntityType, p), plan);
    }

    /// <summary>
    /// Cached keyset machinery for one (entity, sort plan): member nodes, typed parameter accessors,
    /// fast getters and value types for cursor encoding/decoding.
    /// </summary>
    private sealed class KeysetPlan
    {
        private readonly string _fingerprint;
        private readonly QryxSortKey[] _keys;
        private readonly Expression[] _members;
        private readonly ParameterAccessor[] _accessors;
        private readonly Type[] _types;
        private readonly ParameterExpression _parameter;

        public KeysetPlan(Type entityType, QryxSortPlan plan)
        {
            var metadata = MetadataCache.GetEntityMetadata(entityType);
            _fingerprint = plan.Fingerprint;
            _keys = plan.KeyArray;
            _parameter = Expression.Parameter(entityType, "x");
            _members = new Expression[_keys.Length];
            _accessors = new ParameterAccessor[_keys.Length];
            _types = new Type[_keys.Length];
            Getters = new Func<object, object?>[_keys.Length];

            for (int i = 0; i < _keys.Length; i++)
            {
                var path = _keys[i].Path;
                _members[i] = ExpressionHelper.BuildMemberAccess(_parameter, path);
                _types[i] = _members[i].Type;
                _accessors[i] = ParameterAccessor.For(_types[i]);
                Getters[i] = metadata.GetFastPropertyGetter(path)!;

                if (UnsupportedReason == null && IsNullable(metadata.ResolveProperty(path), _types[i]))
                {
                    // NULL breaks "k > @v" seeks (and NULL ordering differs per database)
                    UnsupportedReason = $"Keyset pagination needs non-nullable sort columns, but '{path}' is nullable. Use offset pagination or sort by a non-nullable column.";
                }
            }
        }

        public Func<object, object?>[] Getters { get; }

        public string? UnsupportedReason { get; }

        /// <summary>Null when a key value is NULL (unexpected NULL in a column assumed non-nullable): no cursor.</summary>
        public string? Encode(IReadOnlyList<object?> values)
        {
            var payload = new object?[values.Count + 1];
            payload[0] = _fingerprint;
            for (int i = 0; i < values.Count; i++)
            {
                if (values[i] is null)
                    return null;
                payload[i + 1] = values[i];
            }

            return Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(payload));
        }

        public object?[] Decode(object cursor)
        {
            if (cursor is not string token || token.Length == 0 || token.Length > MaxCursorLength || !Base64Url.IsValid(token))
                throw InvalidCursor();

            try
            {
                using var document = JsonDocument.Parse(Base64Url.DecodeFromChars(token));
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0 || root[0].ValueKind != JsonValueKind.String)
                    throw InvalidCursor();

                // Fingerprint first: a cursor of another sort usually also has another number of keys
                if (root[0].GetString() != _fingerprint)
                {
                    throw new QryxRequestException(
                        CursorParameterName,
                        "The cursor was created for a different sortBy. Request the first page again with the current sortBy.");
                }

                if (root.GetArrayLength() != _keys.Length + 1)
                    throw InvalidCursor();

                var values = new object?[_keys.Length];
                for (int i = 0; i < values.Length; i++)
                {
                    values[i] = root[i + 1].Deserialize(_types[i]) ?? throw InvalidCursor();
                }

                return values;
            }
            catch (Exception ex) when (ex is JsonException or FormatException or NotSupportedException or InvalidOperationException)
            {
                throw InvalidCursor();
            }
        }

        public Expression<Func<TEntity, bool>> BuildSeekPredicate<TEntity>(object?[] values)
        {
            var parameters = new Expression[values.Length];
            for (int i = 0; i < values.Length; i++)
                parameters[i] = ExpressionHelper.BuildParameter(values[i], _accessors[i], CursorParameterName);

            // OR_i ( k_0 = v_0 AND ... AND k_(i-1) = v_(i-1) AND k_i (> | <) v_i )
            Expression? body = null;
            for (int i = 0; i < values.Length; i++)
            {
                Expression branch = Compare(_members[i], parameters[i], _keys[i].Descending);
                for (int j = i - 1; j >= 0; j--)
                    branch = Expression.AndAlso(Expression.Equal(_members[j], parameters[j]), branch);

                body = body == null ? branch : Expression.OrElse(body, branch);
            }

            return Expression.Lambda<Func<TEntity, bool>>(body!, _parameter);
        }

        private static QryxRequestException InvalidCursor() =>
            new(CursorParameterName, "The cursor is invalid. Pass the 'nextCursor' value of the previous response unchanged.");

        private static bool IsNullable(PropertyInfo? property, Type type)
        {
            if (Nullable.GetUnderlyingType(type) != null)
                return true;

            if (type.IsValueType || property == null)
                return false;

            try
            {
                // Reference types: honour nullable annotations (string? -> nullable); unannotated -> assumed non-null.
                // NullabilityInfoContext is NOT thread-safe: one instance per call (plans are built once and cached).
                return new NullabilityInfoContext().Create(property).ReadState == NullabilityState.Nullable;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>
        /// "member &gt; value" (or &lt; when descending) for types without comparison operators too:
        /// strings via string.Compare, enums via their underlying type, Guid/others via CompareTo.
        /// All forms are translated by EF Core into plain SQL comparisons.
        /// </summary>
        private static Expression Compare(Expression member, Expression value, bool isDescending)
        {
            var type = Nullable.GetUnderlyingType(member.Type) ?? member.Type;

            if (type == typeof(string))
            {
                var compare = Expression.Call(StringCompareMethod, member, value);
                var zero = Expression.Constant(0);
                return isDescending ? Expression.LessThan(compare, zero) : Expression.GreaterThan(compare, zero);
            }

            if (type.IsEnum)
            {
                var underlying = Enum.GetUnderlyingType(type);
                var targetType = member.Type == type ? underlying : typeof(Nullable<>).MakeGenericType(underlying);
                member = Expression.Convert(member, targetType);
                value = Expression.Convert(value, targetType);
            }

            try
            {
                return isDescending ? Expression.LessThan(member, value) : Expression.GreaterThan(member, value);
            }
            catch (InvalidOperationException)
            {
                var compareTo = member.Type.GetMethod(nameof(IComparable.CompareTo), [member.Type])
                    ?? throw new InvalidOperationException($"Sort key type '{member.Type.Name}' is not comparable.");
                var call = Expression.Call(member, compareTo, value);
                var zero = Expression.Constant(0);
                return isDescending ? Expression.LessThan(call, zero) : Expression.GreaterThan(call, zero);
            }
        }
    }
}
