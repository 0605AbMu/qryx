using Microsoft.Extensions.DependencyInjection;
using Qryx.Swashbuckle.Filters;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Qryx.Swashbuckle.Extensions;

public static class QryxSwaggerGenExtensions
{
    /// <summary>
    /// Registers Qryx operation filter into Swashbuckle SwaggerGenOptions.
    /// </summary>
    public static SwaggerGenOptions AddQryxFilters(this SwaggerGenOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.OperationFilter<QryxOperationFilter>();

        return options;
    }
}
