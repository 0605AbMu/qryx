using Microsoft.AspNetCore.Http;
using Qryx.Core.Constants;
using Qryx.Core.Contracts;
using Qryx.Expressions.Metadata;

namespace Qryx.AspNetCore.Filters;

/// <summary>
/// Endpoint filter for Minimal APIs that enforces Qryx pagination DoS guards with 0.00 ns reflection.
/// </summary>
public sealed class QryxEndpointFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var arguments = context.Arguments;
        for (int i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            if (argument is IQryxRequest qryxRequest)
            {
                EnforceDosGuards(argument, qryxRequest);
            }
        }

        return await next(context);
    }

    private static void EnforceDosGuards(object dto, IQryxRequest request)
    {
        var metadata = MetadataCache.GetDtoMetadata(dto.GetType());
        var setters = metadata.StandardSetters;

        if (request.Page < 1 && setters.TryGetValue(nameof(IQryxRequest.Page), out var pageSetter))
        {
            pageSetter(dto, 1);
        }

        var pageSize = request.PageSize;
        if (pageSize <= 0)
        {
            if (setters.TryGetValue(nameof(IQryxRequest.PageSize), out var sizeSetter))
            {
                sizeSetter(dto, QryxConstants.DefaultPageSize);
            }
        }
        else if (pageSize > QryxConstants.DefaultMaxPageSize)
        {
            if (setters.TryGetValue(nameof(IQryxRequest.PageSize), out var sizeSetter))
            {
                sizeSetter(dto, QryxConstants.DefaultMaxPageSize);
            }
        }
    }
}
