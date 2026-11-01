using Microsoft.AspNetCore.Mvc.ModelBinding;
using Qryx.Core.Contracts;

namespace Qryx.AspNetCore.ModelBinding;

/// <summary>
/// Model binder provider for IQryxRequest&lt;T&gt; DTOs.
/// </summary>
public sealed class QryxDtoModelBinderProvider : IModelBinderProvider
{
    public IModelBinder? GetBinder(ModelBinderProviderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var modelType = context.Metadata.ModelType;
        if (IsQryxRequestType(modelType))
        {
            return new QryxDtoModelBinder(modelType);
        }

        return null;
    }

    private static bool IsQryxRequestType(Type type)
    {
        if (type.IsInterface || type.IsAbstract)
            return false;

        foreach (var iface in type.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IQryxRequest<>))
                return true;
        }

        return false;
    }
}
