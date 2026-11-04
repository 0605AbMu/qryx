using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Qryx.ApiDocs.Internal;

namespace Qryx.OpenApi.Transformers;

/// <summary>
/// Microsoft.AspNetCore.OpenApi operation transformer that documents Qryx query DTOs (<c>IQryxRequest&lt;TEntity&gt;</c>)
/// bound with <c>[AsParameters]</c> (Minimal APIs) or <c>[FromQuery]</c> (MVC): camelCase keys, every parameter
/// optional, correct schemas for <c>Range&lt;T&gt;</c>, <c>QryxList&lt;T&gt;</c>, collections and enums, the filter
/// operator of each <c>[Filter]</c> property and the sortable fields of <c>sortBy</c>.
/// </summary>
public sealed class QryxOperationTransformer : IOpenApiOperationTransformer
{
    /// <inheritdoc />
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);

        var replacedSchemaIds = context.Document != null ? QryxUnusedSchemas.For(context.Document) : null;
        QryxOperationDocumenter.Apply(operation, context.Description, replacedSchemaIds);
        return Task.CompletedTask;
    }
}
