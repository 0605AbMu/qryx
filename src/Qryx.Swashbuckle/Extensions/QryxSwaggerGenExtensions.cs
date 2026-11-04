using Microsoft.Extensions.DependencyInjection;
using Qryx.Swashbuckle.Filters;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Qryx.Swashbuckle.Extensions;

public static class QryxSwaggerGenExtensions
{
    /// <summary>
    /// Registers the Qryx filters into Swashbuckle <see cref="SwaggerGenOptions"/>: <see cref="QryxOperationFilter"/>
    /// documents the query parameters of Qryx DTOs (Minimal APIs and MVC), and a document filter removes the component
    /// schemas that only the replaced parameter schemas used.
    /// </summary>
    public static SwaggerGenOptions AddQryxFilters(this SwaggerGenOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.OperationFilter<QryxOperationFilter>();
        options.DocumentFilter<QryxDocumentFilter>();

        return options;
    }
}
