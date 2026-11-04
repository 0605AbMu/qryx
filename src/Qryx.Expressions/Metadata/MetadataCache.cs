using System.Collections;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using Qryx.Core.Attributes;
using Qryx.Core.Enums;

namespace Qryx.Expressions.Metadata;

/// <summary>
/// Static metadata inspector and cache for DTO and Entity types.
/// All reflection is performed on the first encounter and permanently cached.
/// </summary>
public static class MetadataCache
{
    private static readonly ConcurrentDictionary<Type, DtoMetadata> DtoCache = new();
    private static readonly ConcurrentDictionary<Type, EntityMetadata> EntityCache = new();
    private static readonly string[] RangePrefixes = ["Min", "Max", "From", "To"];

    public static DtoMetadata GetDtoMetadata<TDto>() => GetDtoMetadata(typeof(TDto));

    public static DtoMetadata GetDtoMetadata(Type dtoType)
    {
        return DtoCache.GetOrAdd(dtoType, static type => InspectDto(type));
    }

    public static EntityMetadata GetEntityMetadata<TEntity>() => GetEntityMetadata(typeof(TEntity));

    public static EntityMetadata GetEntityMetadata(Type entityType)
    {
        return EntityCache.GetOrAdd(entityType, static type => new EntityMetadata(type));
    }

    private static DtoMetadata InspectDto(Type dtoType)
    {
        var filterProps = new List<FilterPropertyMetadata>();

        var properties = dtoType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        foreach (var prop in properties)
        {
            if (!prop.CanRead)
                continue;

            var filterAttr = prop.GetCustomAttribute<FilterAttribute>(inherit: true);
            if (filterAttr == null)
                continue;

            var (isCollection, collectionElementType) = AnalyzeCollectionType(prop.PropertyType);
            var getter = CompileGetter(dtoType, prop);
            var setter = CompileSetter(dtoType, prop);

            Func<IList>? colFactory = null;
            if (isCollection && collectionElementType != null)
            {
                var listType = typeof(List<>).MakeGenericType(collectionElementType);
                var listCtor = listType.GetConstructor(Type.EmptyTypes);
                if (listCtor != null)
                {
                    colFactory = Expression.Lambda<Func<IList>>(Expression.New(listCtor)).Compile();
                }
            }

            IReadOnlyList<string> targetPaths;
            if (filterAttr.TargetProperties != null && filterAttr.TargetProperties.Length > 0)
            {
                targetPaths = filterAttr.TargetProperties;
            }
            else if (!string.IsNullOrWhiteSpace(filterAttr.TargetProperty))
            {
                targetPaths = [filterAttr.TargetProperty];
            }
            else
            {
                targetPaths = [ResolvePrefixProperty(prop.Name)];
            }

            filterProps.Add(new FilterPropertyMetadata
            {
                DtoProperty = prop,
                DtoPropertyName = prop.Name,
                DtoPropertyType = prop.PropertyType,
                TargetPropertyPaths = targetPaths,
                Operator = filterAttr.Operator,
                Invariant = filterAttr.Invariant,
                Getter = getter,
                Setter = setter,
                IsCollection = isCollection,
                CollectionElementType = collectionElementType,
                CollectionFactory = colFactory
            });
        }

        // Optimize short-circuit evaluation order: cheap scalar filters first, expensive text/compound filters last
        filterProps.Sort(static (a, b) => GetFilterWeight(a).CompareTo(GetFilterWeight(b)));

        var standardSetters = new Dictionary<string, Action<object, object?>>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in properties)
        {
            if (prop.CanWrite)
            {
                var s = CompileSetter(dtoType, prop);
                if (s != null)
                {
                    standardSetters[prop.Name] = s;
                }
            }
        }

        var sortableSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in properties)
        {
            if (prop.IsDefined(typeof(SortableAttribute), inherit: true))
            {
                var sortAttr = prop.GetCustomAttribute<SortableAttribute>(inherit: true);
                if (!string.IsNullOrWhiteSpace(sortAttr?.TargetProperty))
                {
                    sortableSet.Add(sortAttr.TargetProperty);
                }

                var filterAttr = prop.GetCustomAttribute<FilterAttribute>(inherit: true);
                if (!string.IsNullOrWhiteSpace(filterAttr?.TargetProperty))
                {
                    sortableSet.Add(filterAttr.TargetProperty);
                }
                else if (filterAttr?.TargetProperties != null && filterAttr.TargetProperties.Length > 0)
                {
                    foreach (var tp in filterAttr.TargetProperties)
                        sortableSet.Add(tp);
                }
                else
                {
                    sortableSet.Add(ResolvePrefixProperty(prop.Name));
                    sortableSet.Add(prop.Name);
                }
            }
        }

        var ctor = dtoType.GetConstructor(Type.EmptyTypes);
        Func<object> factory = ctor != null
            ? Expression.Lambda<Func<object>>(Expression.New(ctor)).Compile()
            : () => Activator.CreateInstance(dtoType)!;

        return new DtoMetadata
        {
            DtoType = dtoType,
            Factory = factory,
            Filters = filterProps,
            SortableProperties = sortableSet,
            StandardSetters = standardSetters,
            AllowTotal = !dtoType.IsDefined(typeof(NoTotalAttribute), inherit: true)
        };
    }

    private static int GetFilterWeight(FilterPropertyMetadata meta)
    {
        if (meta.TargetPropertyPaths.Count > 1)
            return 100;

        return meta.Operator switch
        {
            Op.Equals => meta.DtoPropertyType == typeof(string) ? 2 : 1,
            Op.NotEquals => 3,
            Op.GreaterThan or Op.GreaterThanOrEqual or Op.LessThan or Op.LessThanOrEqual => 4,
            Op.Between => 5,
            Op.In => 10,
            Op.NotIn => 11,
            Op.StartsWith => 20,
            Op.EndsWith => 21,
            Op.Contains => 22,
            _ => 50
        };
    }

    private static (bool IsCollection, Type? ElementType) AnalyzeCollectionType(Type type)
    {
        if (type == typeof(string))
            return (false, null);

        if (typeof(IEnumerable).IsAssignableFrom(type))
        {
            if (type.IsGenericType)
            {
                var args = type.GetGenericArguments();
                if (args.Length == 1)
                    return (true, args[0]);
            }

            foreach (var iface in type.GetInterfaces())
            {
                if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                {
                    return (true, iface.GetGenericArguments()[0]);
                }
            }

            return (true, typeof(object));
        }

        return (false, null);
    }

    private static Func<object, object?> CompileGetter(Type dtoType, PropertyInfo propInfo)
    {
        var param = Expression.Parameter(typeof(object), "dto");
        var cast = Expression.Convert(param, dtoType);
        var propAccess = Expression.Property(cast, propInfo);
        var boxed = Expression.Convert(propAccess, typeof(object));
        return Expression.Lambda<Func<object, object?>>(boxed, param).Compile();
    }

    private static Action<object, object?>? CompileSetter(Type dtoType, PropertyInfo propInfo)
    {
        if (!propInfo.CanWrite)
            return null;

        try
        {
            var targetParam = Expression.Parameter(typeof(object), "target");
            var valueParam = Expression.Parameter(typeof(object), "value");

            var castTarget = Expression.Convert(targetParam, dtoType);
            var castValue = Expression.Convert(valueParam, propInfo.PropertyType);

            var assign = Expression.Assign(Expression.Property(castTarget, propInfo), castValue);
            return Expression.Lambda<Action<object, object?>>(assign, targetParam, valueParam).Compile();
        }
        catch
        {
            return (target, val) => propInfo.SetValue(target, val);
        }
    }

    private static string ResolvePrefixProperty(string propName)
    {
        foreach (var prefix in RangePrefixes)
        {
            if (propName.StartsWith(prefix, StringComparison.Ordinal) &&
                propName.Length > prefix.Length &&
                char.IsUpper(propName[prefix.Length]))
            {
                return propName[prefix.Length..];
            }
        }
        return propName;
    }
}
