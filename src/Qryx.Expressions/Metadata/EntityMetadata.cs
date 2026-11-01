using System.Collections.Concurrent;
using System.Reflection;
using Qryx.Core.Attributes;

namespace Qryx.Expressions.Metadata;

/// <summary>
/// Cached inspection metadata for an Entity type.
/// </summary>
public sealed class EntityMetadata
{
    private readonly ConcurrentDictionary<string, PropertyInfo?> _propertyCache = new(StringComparer.OrdinalIgnoreCase);

    public Type EntityType { get; }
    public IReadOnlyDictionary<string, PropertyInfo> Properties { get; }
    public PropertyInfo? PrimaryKeyProperty { get; }

    public EntityMetadata(Type entityType)
    {
        EntityType = entityType;

        var dict = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in entityType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.CanRead && !prop.IsDefined(typeof(NotSortableAttribute), inherit: true))
            {
                dict[prop.Name] = prop;
            }
        }
        Properties = dict;

        // Try find standard Id or EntityId
        if (dict.TryGetValue("Id", out var idProp))
        {
            PrimaryKeyProperty = idProp;
        }
        else if (dict.TryGetValue($"{entityType.Name}Id", out var namedIdProp))
        {
            PrimaryKeyProperty = namedIdProp;
        }
    }

    /// <summary>
    /// Resolves a single or nested property info (e.g. "Department.Company.Id").
    /// </summary>
    public PropertyInfo? ResolveProperty(string propertyPath)
    {
        if (string.IsNullOrWhiteSpace(propertyPath))
            return null;

        return _propertyCache.GetOrAdd(propertyPath, path =>
        {
            var parts = path.Split('.');
            Type currentType = EntityType;
            PropertyInfo? lastProp = null;

            foreach (var part in parts)
            {
                var prop = currentType.GetProperty(part, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (prop == null || !prop.CanRead || prop.IsDefined(typeof(NotSortableAttribute), inherit: true))
                    return null;

                lastProp = prop;
                currentType = prop.PropertyType;
            }

            return lastProp;
        });
    }

    public bool HasProperty(string propertyPath) => ResolveProperty(propertyPath) != null;
}
