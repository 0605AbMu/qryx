using System.Collections;
using System.Globalization;
using System.Reflection;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Qryx.Core.Constants;
using Qryx.Core.Contracts;
using Qryx.Expressions.Metadata;

namespace Qryx.AspNetCore.ModelBinding;

/// <summary>
/// Model binder for QryxRequest DTOs in MVC Controllers.
/// </summary>
public sealed class QryxDtoModelBinder(Type dtoType) : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext bindingContext)
    {
        ArgumentNullException.ThrowIfNull(bindingContext);

        var dto = Activator.CreateInstance(dtoType);
        if (dto == null)
        {
            bindingContext.Result = ModelBindingResult.Failed();
            return Task.CompletedTask;
        }

        var valueProvider = bindingContext.ValueProvider;
        var dtoMetadata = MetadataCache.GetDtoMetadata(dtoType);

        // Bind standard request properties
        BindStandardProperties(dto, dtoMetadata, valueProvider);

        // Bind filter properties
        foreach (var filter in dtoMetadata.Filters)
        {
            var propName = filter.DtoPropertyName;

            if (filter.IsCollection && filter.CollectionElementType != null)
            {
                var colVal = BindCollection(propName, filter.CollectionElementType, valueProvider);
                if (colVal != null)
                {
                    filter.Setter?.Invoke(dto, colVal);
                }
            }
            else
            {
                var rawResult = valueProvider.GetValue(propName);
                if (rawResult != ValueProviderResult.None && !string.IsNullOrWhiteSpace(rawResult.FirstValue))
                {
                    var converted = ConvertSingleValue(rawResult.FirstValue, filter.DtoPropertyType);
                    if (converted != null)
                    {
                        filter.Setter?.Invoke(dto, converted);
                    }
                }
            }
        }

        bindingContext.Result = ModelBindingResult.Success(dto);
        return Task.CompletedTask;
    }

    private static void BindStandardProperties(object dto, DtoMetadata metadata, IValueProvider valueProvider)
    {
        var pageResult = valueProvider.GetValue("page");
        if (pageResult != ValueProviderResult.None && int.TryParse(pageResult.FirstValue, out var page))
        {
            SetFast(metadata, dto, nameof(IQryxRequest<object>.Page), Math.Max(page, 1));
        }

        var pageSizeResult = valueProvider.GetValue("pageSize");
        if (pageSizeResult != ValueProviderResult.None && int.TryParse(pageSizeResult.FirstValue, out var pageSize))
        {
            SetFast(metadata, dto, nameof(IQryxRequest<object>.PageSize), Math.Clamp(pageSize <= 0 ? QryxConstants.DefaultPageSize : pageSize, 1, QryxConstants.DefaultMaxPageSize));
        }

        var sortByResult = valueProvider.GetValue("sortBy");
        if (sortByResult != ValueProviderResult.None && !string.IsNullOrWhiteSpace(sortByResult.FirstValue))
        {
            SetFast(metadata, dto, nameof(IQryxRequest<object>.SortBy), sortByResult.FirstValue);
        }

        var isDescResult = valueProvider.GetValue("isDescending");
        if (isDescResult != ValueProviderResult.None && bool.TryParse(isDescResult.FirstValue, out var isDesc))
        {
            SetFast(metadata, dto, nameof(IQryxRequest<object>.IsDescending), isDesc);
        }

        var cursorResult = valueProvider.GetValue("afterCursor");
        if (cursorResult != ValueProviderResult.None && !string.IsNullOrWhiteSpace(cursorResult.FirstValue))
        {
            SetFast(metadata, dto, nameof(IQryxRequest<object>.AfterCursor), cursorResult.FirstValue);
        }
    }

    private static void SetFast(DtoMetadata metadata, object target, string propName, object? value)
    {
        if (metadata.StandardSetters.TryGetValue(propName, out var setter))
        {
            setter(target, value);
        }
    }

    private static object? BindCollection(string propName, Type elementType, IValueProvider valueProvider)
    {
        var rawResult = valueProvider.GetValue(propName);
        if (rawResult == ValueProviderResult.None || rawResult.Values.Count == 0)
            return null;

        var listType = typeof(List<>).MakeGenericType(elementType);
        var list = (IList)Activator.CreateInstance(listType)!;

        foreach (var val in rawResult.Values)
        {
            if (string.IsNullOrWhiteSpace(val))
                continue;

            var converted = ConvertSingleValue(val, elementType);
            if (converted != null)
                list.Add(converted);
        }

        return list.Count > 0 ? list : null;
    }

    private static object? ConvertSingleValue(string str, Type targetType)
    {
        try
        {
            var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

            if (underlying.IsEnum)
                return Enum.Parse(underlying, str, ignoreCase: true);

            if (underlying == typeof(Guid))
                return Guid.Parse(str);

            return Convert.ChangeType(str, underlying, CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    private static void SetNamedProperty(object target, string propName, object? value)
    {
        var prop = target.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (prop != null && prop.CanWrite)
        {
            prop.SetValue(target, value);
        }
    }

    private static void SetPropertyValue(PropertyInfo prop, object target, object? value)
    {
        if (prop.CanWrite)
        {
            prop.SetValue(target, value);
        }
    }
}
