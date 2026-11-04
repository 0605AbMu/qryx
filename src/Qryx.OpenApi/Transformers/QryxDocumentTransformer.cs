using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Qryx.ApiDocs.Internal;

namespace Qryx.OpenApi.Transformers;

/// <summary>
/// Removes component schemas that only the parameters rewritten by <see cref="QryxOperationTransformer"/>
/// referenced (e.g. an enum used solely as a query parameter, now documented inline as a string enum).
/// </summary>
internal sealed class QryxDocumentTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        QryxUnusedSchemas.RemoveUnreferenced(document, document);
        return Task.CompletedTask;
    }
}
