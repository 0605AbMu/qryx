using System.Collections.Concurrent;
using System.Reflection;
using Qryx.Core.Attributes;

namespace Qryx.Expressions.Metadata;

/// <summary>
/// Cached inspection metadata for an Entity type.
/// </summary>
public sealed class EntityMetadata
{
    private readonly ConcurrentDictionary<string, PropertyInfo> _propertyCache = new(StringComparer.OrdinalIgnoreCase);

    public Type EntityType { get; }
    public IReadOnlyDictionary<string, PropertyInfo> Properties { get; }
    public PropertyInfo? PrimaryKeyProperty { get; }
    public IReadOnlySet<string> SortableProperties { get; }

    public EntityMetadata(Type entityType)
    {
        EntityType = entityType;

        var dict = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);
        var sortableSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var prop in entityType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.CanRead)
            {
                dict[prop.Name] = prop;

                if (prop.IsDefined(typeof(SortableAttribute), inherit: true))
                {
                    sortableSet.Add(prop.Name);
                }
            }
        }
        Properties = dict;
        SortableProperties = sortableSet;

        // Resolve Primary Key:
        // 1. Property with [Key] attribute
        PropertyInfo? keyProp = null;
        foreach (var p in dict.Values)
        {
            if (p.GetCustomAttributes().Any(a => a.GetType().Name == "KeyAttribute"))
            {
                keyProp = p;
                break;
            }
        }

        if (keyProp != null)
        {
            PrimaryKeyProperty = keyProp;
        }
        else if (dict.TryGetValue("Id", out var idProp))
        {
            PrimaryKeyProperty = idProp;
        }
        else if (dict.TryGetValue($"{entityType.Name}Id", out var namedIdProp))
        {
            PrimaryKeyProperty = namedIdProp;
        }
        else
        {
            // Fallback: any property ending with "Id" or first primitive/scalar property
            var fallbackId = dict.Values.FirstOrDefault(p => p.Name.EndsWith("Id", StringComparison.OrdinalIgnoreCase))
                             ?? dict.Values.FirstOrDefault(p => p.PropertyType.IsPrimitive || p.PropertyType == typeof(string) || p.PropertyType == typeof(Guid) || p.PropertyType == typeof(DateTime) || p.PropertyType == typeof(DateTimeOffset));
            PrimaryKeyProperty = fallbackId;
        }
    }

    /// <summary>
    /// Resolves a single or nested property info (e.g. "Department.Company.Id").
    /// </summary>
    public PropertyInfo? ResolveProperty(string propertyPath)
    {
        if (string.IsNullOrWhiteSpace(propertyPath))
            return null;

        if (_propertyCache.TryGetValue(propertyPath, out var cached))
            return cached;

        var parts = propertyPath.Split('.');
        Type currentType = EntityType;
        PropertyInfo? lastProp = null;

        foreach (var part in parts)
        {
            var prop = currentType.GetProperty(part, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop == null || !prop.CanRead)
                return null; // Topilmagan yo'llar keshlanmaydi -> klient satrlari keshni cheksiz o'stira olmaydi

            lastProp = prop;
            currentType = prop.PropertyType;
        }

        if (lastProp != null)
            _propertyCache.TryAdd(propertyPath, lastProp);

        return lastProp;
    }

    /// <summary>Number of cached property paths (diagnostics / tests).</summary>
    internal int CachedPropertyCount => _propertyCache.Count;

    public bool HasProperty(string propertyPath) => ResolveProperty(propertyPath) != null;

    private readonly ConcurrentDictionary<string, Func<object, object?>?> _getterCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets a pre-compiled, zero-reflection delegate for extracting property values from entity instances.
    /// </summary>
    public Func<object, object?>? GetFastPropertyGetter(string propertyPath)
    {
        if (string.IsNullOrWhiteSpace(propertyPath))
            return null;

        if (_getterCache.TryGetValue(propertyPath, out var cached))
            return cached;

        if (ResolveProperty(propertyPath) == null)
            return null; // Topilmagan yo'llar keshlanmaydi

        return _getterCache.GetOrAdd(propertyPath, path =>
        {
            // Nested paths (e.g. "Department.Name") are supported via the shared member-access builder
            var param = System.Linq.Expressions.Expression.Parameter(typeof(object), "obj");
            var cast = System.Linq.Expressions.Expression.Convert(param, EntityType);
            var member = Builders.ExpressionHelper.BuildMemberAccess(cast, path);
            var box = System.Linq.Expressions.Expression.Convert(member, typeof(object));
            return System.Linq.Expressions.Expression.Lambda<Func<object, object?>>(box, param).Compile();
        });
    }
}
