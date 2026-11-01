using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Qryx.AspNetCore.Filters;

namespace Qryx.AspNetCore.Extensions;

public static class QryxEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Attaches the QryxEndpointFilter to the endpoint for query binding enhancement and DoS guarding.
    /// </summary>
    public static RouteHandlerBuilder WithQryx(this RouteHandlerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddEndpointFilter<QryxEndpointFilter>();
    }

    /// <summary>
    /// Attaches the QryxEndpointFilter to the endpoint route group.
    /// </summary>
    public static RouteGroupBuilder WithQryx(this RouteGroupBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddEndpointFilter<QryxEndpointFilter>();
    }
}
