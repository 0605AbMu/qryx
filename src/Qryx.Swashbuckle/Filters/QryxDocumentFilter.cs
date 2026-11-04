using Microsoft.OpenApi;
using Qryx.ApiDocs.Internal;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Qryx.Swashbuckle.Filters;

/// <summary>
/// Removes component schemas that only the parameters rewritten by <see cref="QryxOperationFilter"/> referenced
/// (e.g. the <c>Int32Range</c> object schema Swashbuckle generates for a <c>Range&lt;int&gt;</c> query parameter).
/// </summary>
internal sealed class QryxDocumentFilter : IDocumentFilter
{
    public void Apply(OpenApiDocument swaggerDoc, DocumentFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(swaggerDoc);
        ArgumentNullException.ThrowIfNull(context);

        QryxUnusedSchemas.RemoveUnreferenced(swaggerDoc, context.SchemaRepository);
    }
}
