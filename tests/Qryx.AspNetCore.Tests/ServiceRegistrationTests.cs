using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Qryx.AspNetCore.Extensions;
using Qryx.AspNetCore.Filters;
using Qryx.AspNetCore.ModelBinding;
using Qryx.AspNetCore.Tests.Infrastructure;
using Qryx.Expressions.Options;

namespace Qryx.AspNetCore.Tests;

public sealed class ServiceRegistrationTests : GlobalOptionsScope
{
    [Fact]
    public void AddQryx_RegistersConfiguredSingleton_AndAssignsGlobal()
    {
        var services = new ServiceCollection();

        services.AddQryx(o =>
        {
            o.MaxPageSize = 42;
            o.DefaultPageSize = 7;
            o.ThrowOnInvalidSortProperty = true;
        });

        using var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<QryxOptions>();

        Assert.Same(resolved, QryxOptions.Global);
        Assert.Equal(42, resolved.MaxPageSize);
        Assert.Equal(7, resolved.DefaultPageSize);
        Assert.True(resolved.ThrowOnInvalidSortProperty);
    }

    [Fact]
    public void AddQryx_WithoutConfigure_UsesDefaults()
    {
        var services = new ServiceCollection();

        services.AddQryx();

        using var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<QryxOptions>();
        Assert.Same(resolved, QryxOptions.Global);
        Assert.Equal(100, resolved.MaxPageSize);
        Assert.Equal(20, resolved.DefaultPageSize);
    }

    [Fact]
    public void AddQryx_CalledTwice_ReusesInstance_AndRegistersMvcServicesOnce()
    {
        var services = new ServiceCollection();
        services.AddMvcCore();

        services.AddQryx(o => o.MaxPageSize = 10);
        services.AddQryx(o => o.DefaultPageSize = 5);

        using var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<QryxOptions>();
        Assert.Single(services, d => d.ServiceType == typeof(QryxOptions));
        Assert.Same(resolved, QryxOptions.Global);
        Assert.Equal(10, resolved.MaxPageSize);
        Assert.Equal(5, resolved.DefaultPageSize);

        var mvcOptions = provider.GetRequiredService<IOptions<MvcOptions>>().Value;
        Assert.Single(mvcOptions.ModelBinderProviders.OfType<QryxDtoModelBinderProvider>());
        Assert.IsType<QryxDtoModelBinderProvider>(mvcOptions.ModelBinderProviders[0]);
        Assert.Single(mvcOptions.Filters.OfType<QryxExceptionFilter>());
    }

    [Fact]
    public void AddQryx_ReplacesPreviouslyRegisteredFactory_SoDiAndGlobalMatch()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_ => new QryxOptions { MaxPageSize = 3 });

        services.AddQryx(o => o.MaxPageSize = 9);

        using var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<QryxOptions>();
        Assert.Same(resolved, QryxOptions.Global);
        Assert.Equal(9, resolved.MaxPageSize);
    }

    [Fact]
    public void AddQryx_InvalidConfiguration_Throws_AndLeavesGlobalUntouched()
    {
        var before = QryxOptions.Global;
        var services = new ServiceCollection();

        Assert.Throws<ArgumentOutOfRangeException>(() => services.AddQryx(o => o.MaxPageSize = 0));

        Assert.Same(before, QryxOptions.Global);
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(QryxOptions));
    }
}
