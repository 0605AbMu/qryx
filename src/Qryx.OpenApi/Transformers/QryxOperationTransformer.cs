using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Qryx.Core.Constants;
using Qryx.Core.Contracts;

namespace Qryx.OpenApi.Transformers;

/// <summary>
/// OpenAPI operation transformer that documents Qryx standard parameters (pagination, sorting).
/// </summary>
public sealed class QryxOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        if (operation.Parameters == null)
            return Task.CompletedTask;

        Type? qryxDtoType = null;

        foreach (var param in context.Description.ParameterDescriptions)
        {
            if (param.Type != null && IsQryxRequest(param.Type))
            {
                qryxDtoType = param.Type;
                break;
            }

            if (param.ModelMetadata?.ContainerType != null && IsQryxRequest(param.ModelMetadata.ContainerType))
            {
                qryxDtoType = param.ModelMetadata.ContainerType;
                break;
            }
        }

        if (qryxDtoType == null)
            return Task.CompletedTask;

        foreach (var param in operation.Parameters)
        {
            if (param is not OpenApiParameter openApiParam)
                continue;

            if (string.Equals(openApiParam.Name, "page", StringComparison.OrdinalIgnoreCase))
            {
                openApiParam.Description = "1-based page number for offset pagination. Minimum: 1.";
            }
            else if (string.Equals(openApiParam.Name, "pageSize", StringComparison.OrdinalIgnoreCase))
            {
                openApiParam.Description = $"Number of items per page. Clamped to maximum {QryxConstants.DefaultMaxPageSize}.";
            }
            else if (string.Equals(openApiParam.Name, "sortBy", StringComparison.OrdinalIgnoreCase))
            {
                openApiParam.Description = "Entity property name to sort by (e.g. 'CreatedAt' or 'Department.Name').";
            }
            else if (string.Equals(openApiParam.Name, "isDescending", StringComparison.OrdinalIgnoreCase))
            {
                openApiParam.Description = "Whether sort direction is descending. Default: false.";
            }
            else if (string.Equals(openApiParam.Name, "afterCursor", StringComparison.OrdinalIgnoreCase))
            {
                openApiParam.Description = "Cursor value for keyset pagination (e.g. last seen Id or timestamp).";
            }
        }

        return Task.CompletedTask;
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
}
