using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Qryx.AspNetCore.ModelBinding;
using Qryx.Expressions.Options;

namespace Qryx.AspNetCore.Extensions;

public static class QryxServiceCollectionExtensions
{
    /// <summary>
    /// Registers Qryx services and MVC model binder providers.
    /// </summary>
    public static IServiceCollection AddQryx(
        this IServiceCollection services,
        Action<QryxOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new QryxOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);

        services.Configure<MvcOptions>(mvcOptions =>
        {
            mvcOptions.ModelBinderProviders.Insert(0, new QryxDtoModelBinderProvider());
        });

        return services;
    }
}
