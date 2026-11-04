using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Qryx.AspNetCore.Filters;
using Qryx.AspNetCore.ModelBinding;
using Qryx.Expressions.Options;

namespace Qryx.AspNetCore.Extensions;

public static class QryxServiceCollectionExtensions
{
    /// <summary>
    /// Registers Qryx services: the configured <see cref="QryxOptions"/> singleton, the MVC model binder provider
    /// for Qryx DTOs and the global MVC <see cref="QryxExceptionFilter"/> (QryxRequestException -> HTTP 400).
    /// </summary>
    /// <remarks>
    /// The configured instance is also assigned to <see cref="QryxOptions.Global"/>, which the static query builders
    /// and EF Core extensions read. <see cref="QryxOptions.Global"/> is process-wide: when several hosts live in one
    /// process (e.g. integration tests) the last <c>AddQryx</c> call wins. Calling <c>AddQryx</c> repeatedly on the
    /// same <see cref="IServiceCollection"/> configures the same options instance and does not register duplicates.
    /// </remarks>
    public static IServiceCollection AddQryx(
        this IServiceCollection services,
        Action<QryxOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var existing = FindRegisteredInstance(services);
        var options = existing ?? new QryxOptions();
        configure?.Invoke(options);

        if (existing == null)
        {
            // Replace (not TryAdd): the DI singleton and QryxOptions.Global must be the same instance
            services.Replace(ServiceDescriptor.Singleton(options));
        }

        QryxOptions.Global = options;

        services.Configure<MvcOptions>(mvcOptions =>
        {
            if (!mvcOptions.ModelBinderProviders.OfType<QryxDtoModelBinderProvider>().Any())
            {
                mvcOptions.ModelBinderProviders.Insert(0, new QryxDtoModelBinderProvider());
            }

            if (!mvcOptions.Filters.OfType<QryxExceptionFilter>().Any())
            {
                mvcOptions.Filters.Add(new QryxExceptionFilter());
            }
        });

        return services;
    }

    private static QryxOptions? FindRegisteredInstance(IServiceCollection services)
    {
        for (int i = services.Count - 1; i >= 0; i--)
        {
            var descriptor = services[i];
            if (descriptor.ServiceType == typeof(QryxOptions) && !descriptor.IsKeyedService)
            {
                return descriptor.ImplementationInstance as QryxOptions;
            }
        }

        return null;
    }
}
