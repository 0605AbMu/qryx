using System.Reflection;
using Microsoft.AspNetCore.Http;
using Qryx.Core.Constants;
using Qryx.Core.Contracts;

namespace Qryx.AspNetCore.Filters;

/// <summary>
/// Endpoint filter for Minimal APIs that enforces Qryx pagination DoS guards.
/// </summary>
public sealed class QryxEndpointFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        foreach (var argument in context.Arguments)
        {
            if (argument == null)
                continue;

            if (IsQryxRequest(argument.GetType()))
            {
                EnforceDosGuards(argument);
            }
        }

        return await next(context);
    }

    private static bool IsQryxRequest(Type type)
    {
        foreach (var iface in type.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IQryxRequest<>))
                return true;
        }
        return false;
    }

    private static void EnforceDosGuards(object dto)
    {
        var type = dto.GetType();

        var pageProp = type.GetProperty(nameof(IQryxRequest<object>.Page), BindingFlags.Public | BindingFlags.Instance);
        if (pageProp != null && pageProp.CanWrite)
        {
            var page = (int?)(pageProp.GetValue(dto)) ?? 1;
            if (page < 1)
                pageProp.SetValue(dto, 1);
        }

        var pageSizeProp = type.GetProperty(nameof(IQryxRequest<object>.PageSize), BindingFlags.Public | BindingFlags.Instance);
        if (pageSizeProp != null && pageSizeProp.CanWrite)
        {
            var pageSize = (int?)(pageSizeProp.GetValue(dto)) ?? QryxConstants.DefaultPageSize;
            if (pageSize <= 0)
                pageSize = QryxConstants.DefaultPageSize;
            else if (pageSize > QryxConstants.DefaultMaxPageSize)
                pageSize = QryxConstants.DefaultMaxPageSize;

            pageSizeProp.SetValue(dto, pageSize);
        }
    }
}
