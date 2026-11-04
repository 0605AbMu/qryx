using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Qryx.OpenApi.Transformers;

namespace Qryx.OpenApi.Extensions;

public static class QryxOpenApiExtensions
{
    /// <summary>
    /// Registers the Qryx transformers with Microsoft.AspNetCore.OpenApi: <see cref="QryxOperationTransformer"/>
    /// documents the query parameters of Qryx DTOs (Minimal APIs and MVC), and a document transformer removes the
    /// component schemas that only the replaced parameter schemas used.
    /// </summary>
    public static OpenApiOptions AddQryxTransformers(this OpenApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.AddOperationTransformer<QryxOperationTransformer>();
        options.AddDocumentTransformer<QryxDocumentTransformer>();

        return options;
    }
}
