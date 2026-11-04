using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Qryx.AspNetCore.Extensions;
using Qryx.Expressions.Options;

namespace Qryx.AspNetCore.Tests.Infrastructure;

/// <summary>Builds an in-process WebApplication on TestServer with a seeded InMemory database.</summary>
internal static class TestHostFactory
{
    public static async Task<WebApplication> StartAsync(
        Action<WebApplication> map,
        Action<QryxOptions>? configure = null,
        bool addControllers = false)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            // Production: Minimal APIs do not throw on bad requests (ThrowOnBadRequest = false)
            EnvironmentName = Environments.Production
        });

        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        var databaseName = Guid.NewGuid().ToString("N");
        builder.Services.AddDbContext<TestDbContext>(o => o.UseInMemoryDatabase(databaseName));
        builder.Services.AddQryx(configure);

        if (addControllers)
        {
            builder.Services.AddControllers().AddApplicationPart(typeof(TestHostFactory).Assembly);
        }

        var app = builder.Build();

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            db.Items.AddRange(TestDbContext.SeedItems());
            await db.SaveChangesAsync();
        }

        map(app);
        await app.StartAsync();
        return app;
    }

    public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.False(string.IsNullOrWhiteSpace(body), $"Expected a JSON body, status {(int)response.StatusCode}.");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    /// <summary>Asserts an HTTP 400 validation problem and returns the messages for <paramref name="expectedKey"/>.</summary>
    public static async Task<string[]> AssertValidationProblemAsync(HttpResponseMessage response, string expectedKey)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"Expected 400 but got {(int)response.StatusCode}: {body}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal(400, root.GetProperty("status").GetInt32());

        var errors = root.GetProperty("errors");
        Assert.True(errors.TryGetProperty(expectedKey, out var messages), $"Missing error key '{expectedKey}' in: {body}");
        var result = messages.EnumerateArray().Select(m => m.GetString()!).ToArray();
        Assert.NotEmpty(result);
        return result;
    }
}

/// <summary>
/// Saves <see cref="QryxOptions.Global"/> before each test and restores it afterwards
/// (AddQryx assigns it; test parallelization is disabled for this assembly).
/// </summary>
public abstract class GlobalOptionsScope : IDisposable
{
    private readonly QryxOptions _savedGlobal = QryxOptions.Global;

    public void Dispose()
    {
        QryxOptions.Global = _savedGlobal;
        GC.SuppressFinalize(this);
    }
}
