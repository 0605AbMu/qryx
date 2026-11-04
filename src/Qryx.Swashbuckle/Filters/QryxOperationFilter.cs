using Microsoft.OpenApi;
using Qryx.ApiDocs.Internal;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Qryx.Swashbuckle.Filters;

/// <summary>
/// Swashbuckle operation filter that documents Qryx query DTOs (<c>IQryxRequest&lt;TEntity&gt;</c>) bound with
/// <c>[AsParameters]</c> (Minimal APIs) or <c>[FromQuery]</c> (MVC): camelCase keys, every parameter optional,
/// correct schemas for <c>Range&lt;T&gt;</c>, <c>QryxList&lt;T&gt;</c>, collections and enums, the filter operator of
/// each <c>[Filter]</c> property and the sortable fields of <c>sortBy</c>.
/// </summary>
public sealed class QryxOperationFilter : IOperationFilter
{
    /// <inheritdoc />
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);

        QryxOperationDocumenter.Apply(operation, context.ApiDescription, QryxUnusedSchemas.For(context.SchemaRepository));
    }
}
