using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Qryx.OpenApi.Transformers;

namespace Qryx.OpenApi.Extensions;

public static class QryxOpenApiExtensions
{
    /// <summary>
    /// Registers Qryx operation transformer with Microsoft.AspNetCore.OpenApi.
    /// </summary>
    public static OpenApiOptions AddQryxTransformers(this OpenApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.AddOperationTransformer<QryxOperationTransformer>();

        return options;
    }
}
