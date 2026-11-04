using System.Collections;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Primitives;
using Qryx.Core.Constants;
using Qryx.Core.Contracts;
using Qryx.Expressions.Metadata;
using Qryx.Expressions.Options;

namespace Qryx.AspNetCore.Internal;

/// <summary>A single client-facing binding error: camelCase query key + message.</summary>
internal readonly record struct QryxBindingError(string Key, string Message);

/// <summary>Abstraction over the raw request values (MVC value providers or the Minimal API query collection).</summary>
internal interface IQryxValueSource
{
    StringValues GetValues(string key);
}

internal readonly struct ValueProviderSource(IValueProvider provider) : IQryxValueSource
{
    public StringValues GetValues(string key) => provider.GetValue(key).Values;
}

internal readonly struct QueryCollectionSource(IQueryCollection query) : IQryxValueSource
{
    public StringValues GetValues(string key) => query[key];
}

/// <summary>
/// Pre-computed, cached binding plan for a Qryx DTO type: query keys, compiled setters/getters and parsers
/// for the standard request properties and every [Filter] property. Built once per DTO type.
/// </summary>
internal sealed class QryxBindingPlan
{
    private static readonly ConcurrentDictionary<Type, QryxBindingPlan> Cache = new();

    private readonly Slot? _page;
    private readonly Slot? _pageSize;
    private readonly Slot? _isDescending;
    private readonly Slot? _afterCursor;
    private readonly Slot? _includeTotal;
    private readonly Action<object, object?>? _sortBySetter;
    private readonly Func<object, object?>? _pageGetter;
    private readonly Func<object, object?>? _pageSizeGetter;
    private readonly Slot[] _scalarFilters;
    private readonly CollectionSlot[] _collectionFilters;

    private QryxBindingPlan(Type dtoType)
    {
        Metadata = MetadataCache.GetDtoMetadata(dtoType);

        _page = CreateStandardSlot(dtoType, nameof(IQryxRequest.Page));
        _pageSize = CreateStandardSlot(dtoType, nameof(IQryxRequest.PageSize));
        _isDescending = CreateStandardSlot(dtoType, nameof(IQryxRequest.IsDescending));
        _afterCursor = CreateStandardSlot(dtoType, nameof(IQryxRequest.AfterCursor));
        _includeTotal = CreateStandardSlot(dtoType, nameof(IQryxRequest.IncludeTotal));
        Metadata.StandardSetters.TryGetValue(nameof(IQryxRequest.SortBy), out _sortBySetter);

        _pageGetter = _page != null ? CompileGetter(dtoType, _page.Property) : null;
        _pageSizeGetter = _pageSize != null ? CompileGetter(dtoType, _pageSize.Property) : null;

        var scalars = new List<Slot>();
        var collections = new List<CollectionSlot>();
        var enumSlots = new List<EnumSlot>();
        foreach (var filter in Metadata.Filters)
        {
            if (filter.Setter == null)
                continue;

            var key = QryxProblemDetails.GetErrorKey(filter.DtoPropertyName);
            if (filter.IsCollection && filter.CollectionElementType != null)
            {
                var elementType = filter.CollectionElementType;
                var parser = QryxValueParser.GetParser(elementType);
                var converter = BuildCollectionConverter(filter.DtoPropertyType, elementType);
                if (parser == null || converter == null)
                    continue; // Unsupported element/collection type: left unbound (as before)

                var factory = filter.CollectionFactory
                              ?? (() => (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType))!);

                // QryxList<T> is the comma-separated list type ("?status=Active,Pending"), so its values are split
                var splitCommas = IsQryxList(filter.DtoPropertyType);
                collections.Add(new CollectionSlot(key, elementType, filter.Setter, parser, factory, converter, splitCommas));

                if (!splitCommas && GetNonFlagsEnum(elementType) is { } enumType)
                    enumSlots.Add(new EnumSlot(key, enumType, IsCollection: true));
            }
            else
            {
                var parser = QryxValueParser.GetParser(filter.DtoPropertyType);
                if (parser == null)
                    continue;

                scalars.Add(new Slot(key, filter.DtoProperty, filter.DtoPropertyType, filter.Setter, parser));

                if (GetNonFlagsEnum(filter.DtoPropertyType) is { } enumType)
                    enumSlots.Add(new EnumSlot(key, enumType, IsCollection: false));
            }
        }

        _scalarFilters = [.. scalars];
        _collectionFilters = [.. collections];
        _enumSlots = [.. enumSlots];
    }

    private readonly EnumSlot[] _enumSlots;

    /// <summary>A filter property bound by the Minimal API binder itself whose (element) type is a non-[Flags] enum.</summary>
    private sealed record EnumSlot(string Key, Type EnumType, bool IsCollection);

    /// <summary>
    /// Minimal APIs bind enum properties with <c>Enum.TryParse</c>, outside Qryx's parser: names are case-sensitive and
    /// "Active,Blocked" silently becomes the flags value Active|Blocked (another single member of a non-[Flags] enum).
    /// This validates the raw query values of such properties. Comma-separated values are always reported;
    /// with <paramref name="checkCase"/> (after a binding failure) a name that only matches case-insensitively is
    /// reported with a hint instead of an empty 400.
    /// </summary>
    public List<QryxBindingError>? ValidateMinimalApiEnums(IQueryCollection query, bool checkCase)
    {
        if (_enumSlots.Length == 0)
            return null;

        List<QryxBindingError>? errors = null;
        foreach (var slot in _enumSlots)
        {
            foreach (var raw in query[slot.Key])
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                var expected = string.Join(", ", Enum.GetNames(slot.EnumType));
                if (raw.Contains(','))
                {
                    var hint = slot.IsCollection
                        ? $"Repeat the key for several values (?{slot.Key}=A&{slot.Key}=B) or declare the property as QryxList<T>."
                        : "Only one value is allowed.";
                    (errors ??= []).Add(new QryxBindingError(slot.Key,
                        $"Comma-separated value '{Shorten(raw)}' is not supported for '{slot.Key}'. {hint} Expected one of: {expected}."));
                    break;
                }

                if (checkCase &&
                    !Enum.TryParse(slot.EnumType, raw.Trim(), ignoreCase: false, out _) &&
                    Enum.TryParse(slot.EnumType, raw.Trim(), ignoreCase: true, out _))
                {
                    (errors ??= []).Add(new QryxBindingError(slot.Key,
                        $"Value '{Shorten(raw)}' is not valid for '{slot.Key}'. Enum names are case-sensitive on Minimal APIs; expected one of: {expected} (or the numeric value)."));
                    break;
                }
            }
        }

        return errors;
    }

    private static string Shorten(string value) => value.Length > 64 ? string.Concat(value.AsSpan(0, 64), "...") : value;

    private static bool IsQryxList(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Qryx.Core.Types.QryxList<>);

    private static Type? GetNonFlagsEnum(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying.IsEnum && !underlying.IsDefined(typeof(FlagsAttribute), inherit: false) ? underlying : null;
    }

    public DtoMetadata Metadata { get; }

    public static QryxBindingPlan For(Type dtoType) => Cache.GetOrAdd(dtoType, static type => new QryxBindingPlan(type));

    /// <summary>
    /// Parses every known query key into <paramref name="target"/>. When <paramref name="target"/> is null only
    /// validation is performed. Invalid values are reported as errors and leave the property unchanged.
    /// An omitted <c>pageSize</c> is set to <see cref="QryxOptions.DefaultPageSize"/>; page/pageSize are clamped
    /// afterwards by <see cref="ApplyGuards"/>.
    /// </summary>
    public List<QryxBindingError>? Bind<TSource>(object? target, TSource source, QryxOptions options)
        where TSource : IQryxValueSource
    {
        List<QryxBindingError>? errors = null;

        BindScalar(_page, target, source, ref errors);

        if (_pageSize != null && !BindScalar(_pageSize, target, source, ref errors) && target != null &&
            IsMissing(source.GetValues(_pageSize.Key)))
        {
            _pageSize.Setter(target, options.ClampPageSize(0));
        }

        if (_sortBySetter != null)
        {
            var sortValues = source.GetValues("sortBy");
            if (sortValues.Count > 0)
            {
                var combined = sortValues.Count == 1
                    ? sortValues[0]
                    : string.Join(",", sortValues.Where(static v => !string.IsNullOrWhiteSpace(v)));

                if (!string.IsNullOrWhiteSpace(combined) && target != null)
                    _sortBySetter(target, combined);
            }
        }

        BindScalar(_isDescending, target, source, ref errors);
        BindScalar(_afterCursor, target, source, ref errors);
        BindScalar(_includeTotal, target, source, ref errors);

        foreach (var slot in _scalarFilters)
        {
            BindScalar(slot, target, source, ref errors);
        }

        foreach (var slot in _collectionFilters)
        {
            BindCollection(slot, target, source, ref errors);
        }

        if (target is IQryxRequest request)
            ApplyGuards(target, request, options);

        return errors;
    }

    /// <summary>
    /// DoS guards: Page &lt; 1 becomes 1; PageSize is clamped to [1, MaxPageSize] and a missing (null) or
    /// non-positive PageSize becomes <see cref="QryxOptions.DefaultPageSize"/>.
    /// </summary>
    public void ApplyGuards(object dto, IQryxRequest request, QryxOptions options)
    {
        if (_page != null)
        {
            var rawPage = _pageGetter != null ? _pageGetter(dto) : request.Page;
            if (rawPage is int page && page < 1)
                _page.Setter(dto, QryxConstants.DefaultPage);
        }

        if (_pageSize != null)
        {
            var rawPageSize = _pageSizeGetter != null ? _pageSizeGetter(dto) : request.PageSize;
            if (rawPageSize is int pageSize)
            {
                var clamped = options.ClampPageSize(pageSize);
                if (clamped != pageSize)
                    _pageSize.Setter(dto, clamped);
            }
            else if (rawPageSize is null)
            {
                _pageSize.Setter(dto, options.ClampPageSize(0));
            }
        }
    }

    /// <returns>True when a value was present and parsed successfully.</returns>
    private static bool BindScalar<TSource>(Slot? slot, object? target, TSource source, ref List<QryxBindingError>? errors)
        where TSource : IQryxValueSource
    {
        if (slot == null)
            return false;

        var raw = FirstNonEmpty(source.GetValues(slot.Key));
        if (raw == null)
            return false;

        if (!slot.Parser(raw, out var value))
        {
            (errors ??= []).Add(new QryxBindingError(slot.Key, QryxValueParser.FormatInvalidValue(slot.Key, raw, slot.Type)));
            return false;
        }

        if (target != null)
            slot.Setter(target, value);

        return true;
    }

    private static void BindCollection<TSource>(CollectionSlot slot, object? target, TSource source, ref List<QryxBindingError>? errors)
        where TSource : IQryxValueSource
    {
        var values = source.GetValues(slot.Key);
        if (values.Count == 0)
            return;

        // DoS guard: reject before parsing/allocating anything for oversized collections
        if (values.Count > QryxConstants.DefaultMaxInCollectionSize)
        {
            (errors ??= []).Add(new QryxBindingError(
                slot.Key,
                $"'{slot.Key}' accepts at most {QryxConstants.DefaultMaxInCollectionSize} values."));
            return;
        }

        IList? list = null;
        foreach (var raw in values)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            if (slot.SplitCommas)
            {
                // QryxList<T>: "?status=Active,Pending" (and repeated keys) -> one item per comma-separated part
                foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (!AddItem(slot, part, ref list, ref errors))
                        return;
                }
            }
            else if (!AddItem(slot, raw, ref list, ref errors))
            {
                return;
            }
        }

        if (list != null && target != null)
            slot.Setter(target, slot.Converter(list));
    }

    private static bool AddItem(CollectionSlot slot, string raw, ref IList? list, ref List<QryxBindingError>? errors)
    {
        if (!slot.Parser(raw, out var value))
        {
            (errors ??= []).Add(new QryxBindingError(slot.Key, QryxValueParser.FormatInvalidValue(slot.Key, raw, slot.ElementType)));
            return false;
        }

        list ??= slot.Factory();
        if (list.Count >= QryxConstants.DefaultMaxInCollectionSize)
        {
            (errors ??= []).Add(new QryxBindingError(
                slot.Key,
                $"'{slot.Key}' accepts at most {QryxConstants.DefaultMaxInCollectionSize} values."));
            return false;
        }

        list.Add(value);
        return true;
    }

    private static string? FirstNonEmpty(StringValues values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static bool IsMissing(StringValues values) => FirstNonEmpty(values) == null;

    private Slot? CreateStandardSlot(Type dtoType, string propertyName)
    {
        if (!Metadata.StandardSetters.TryGetValue(propertyName, out var setter))
            return null;

        var property = FindProperty(dtoType, propertyName);
        if (property == null)
            return null;

        var parser = QryxValueParser.GetParser(property.PropertyType);
        return parser == null
            ? null
            : new Slot(QryxProblemDetails.GetErrorKey(propertyName), property, property.PropertyType, setter, parser);
    }

    private static PropertyInfo? FindProperty(Type dtoType, string propertyName)
    {
        // Most-derived match wins ('new' re-declarations in derived records would make GetProperty ambiguous)
        PropertyInfo? best = null;
        foreach (var property in dtoType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase) || !property.CanWrite)
                continue;

            if (best == null || (property.DeclaringType != null && best.DeclaringType != null &&
                                 property.DeclaringType.IsSubclassOf(best.DeclaringType)))
            {
                best = property;
            }
        }

        return best;
    }

    private static Func<object, object?>? CompileGetter(Type dtoType, PropertyInfo property)
    {
        if (property.GetMethod is not { IsPublic: true } || property.DeclaringType == null)
            return null;

        var param = Expression.Parameter(typeof(object), "dto");
        var access = Expression.Property(Expression.Convert(param, property.DeclaringType), property);
        var boxed = Expression.Convert(access, typeof(object));
        return Expression.Lambda<Func<object, object?>>(boxed, param).Compile();
    }

    private static Func<IList, object>? BuildCollectionConverter(Type propertyType, Type elementType)
    {
        var listType = typeof(List<>).MakeGenericType(elementType);

        if (propertyType.IsArray)
        {
            return list =>
            {
                var array = Array.CreateInstance(elementType, list.Count);
                list.CopyTo(array, 0);
                return array;
            };
        }

        if (propertyType.IsAssignableFrom(listType))
            return static list => list;

        if (!propertyType.IsAbstract && !propertyType.IsInterface)
        {
            var enumerableType = typeof(IEnumerable<>).MakeGenericType(elementType);
            var ctor = propertyType.GetConstructor([enumerableType]);
            if (ctor != null)
            {
                var param = Expression.Parameter(typeof(IList), "list");
                var create = Expression.New(ctor, Expression.Convert(param, enumerableType));
                return Expression.Lambda<Func<IList, object>>(Expression.Convert(create, typeof(object)), param).Compile();
            }
        }

        return null;
    }

    private sealed class Slot(string key, PropertyInfo property, Type type, Action<object, object?> setter, QryxValueParserDelegate parser)
    {
        public string Key { get; } = key;
        public PropertyInfo Property { get; } = property;
        public Type Type { get; } = type;
        public Action<object, object?> Setter { get; } = setter;
        public QryxValueParserDelegate Parser { get; } = parser;
    }

    private sealed class CollectionSlot(
        string key,
        Type elementType,
        Action<object, object?> setter,
        QryxValueParserDelegate parser,
        Func<IList> factory,
        Func<IList, object> converter,
        bool splitCommas)
    {
        public string Key { get; } = key;
        public Type ElementType { get; } = elementType;
        public Action<object, object?> Setter { get; } = setter;
        public QryxValueParserDelegate Parser { get; } = parser;
        public Func<IList> Factory { get; } = factory;
        public Func<IList, object> Converter { get; } = converter;
        public bool SplitCommas { get; } = splitCommas;
    }
}
