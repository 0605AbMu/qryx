using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Qryx.AspNetCore.Extensions;
using Qryx.EntityFramework.Extensions;
using Qryx.EntityFramework.Models;
using Qryx.Expressions.Options;
#if NET10_0_OR_GREATER
using Qryx.OpenApi.Extensions;
#endif
using Qryx.Swashbuckle.Extensions;

namespace Qryx.ApiDocs.Tests.Infrastructure;

[ApiController]
[Route("mvc/people")]
public sealed class PeopleController(PeopleDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<Person>>> Get([FromQuery] PersonQuery query, CancellationToken ct) =>
        Ok(await db.People.AsNoTracking().ToPagedResultAsync(query, ct));

    [HttpGet("fast")]
    public async Task<ActionResult<PagedResult<Person>>> GetFast([FromQuery] PersonFastQuery query, CancellationToken ct) =>
        Ok(await db.People.AsNoTracking().ToPagedResultAsync(query, ct));

    [HttpGet("list")]
    public async Task<ActionResult<PagedResult<Person>>> GetList([FromQuery] PersonListQuery query, CancellationToken ct) =>
        Ok(await db.People.AsNoTracking().ToPagedResultAsync(query, ct));
}

/// <summary>
/// One in-process app (TestServer) exposing the same DTOs as Minimal API and MVC endpoints, documented by both
/// Swashbuckle (<c>/swagger/v1/swagger.json</c>) and Microsoft.AspNetCore.OpenApi (<c>/openapi/v1.json</c>).
/// </summary>
public sealed class ApiDocsHost : IAsyncLifetime
{
    public const string SwaggerPath = "/swagger/v1/swagger.json";
    public const string OpenApiPath = "/openapi/v1.json";

    /// <summary>Configured <see cref="QryxOptions.MaxPageSize"/> (non-default to prove the documents read it).</summary>
    public const int MaxPageSize = 50;

    private readonly QryxOptions _savedGlobal = QryxOptions.Global;
    private WebApplication? _app;

    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        var databaseName = Guid.NewGuid().ToString("N");
        builder.Services.AddDbContext<PeopleDbContext>(o => o.UseInMemoryDatabase(databaseName));
        builder.Services.AddQryx(o => o.MaxPageSize = MaxPageSize);
        builder.Services.AddControllers().AddApplicationPart(typeof(ApiDocsHost).Assembly);
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(o => o.AddQryxFilters());
#if NET10_0_OR_GREATER
        builder.Services.AddOpenApi(o => o.AddQryxTransformers());
#endif

        var app = builder.Build();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PeopleDbContext>();
            db.People.AddRange(PeopleDbContext.Seed());
            await db.SaveChangesAsync();
        }

        app.UseSwagger();
#if NET10_0_OR_GREATER
        app.MapOpenApi();
#endif
        app.MapControllers();

        app.MapGet("/minimal/people", async ([AsParameters] PersonQuery query, PeopleDbContext db, CancellationToken ct) =>
            Results.Ok(await db.People.AsNoTracking().ToPagedResultAsync(query, ct))).WithQryx();

        app.MapGet("/minimal/people/fast", async ([AsParameters] PersonFastQuery query, PeopleDbContext db, CancellationToken ct) =>
            Results.Ok(await db.People.AsNoTracking().ToPagedResultAsync(query, ct))).WithQryx();

        // Returns no Ticket: TicketPriority is referenced only by the (rewritten) query parameter schema.
        app.MapGet("/minimal/tickets/count", ([AsParameters] TicketQuery query) => Results.Ok(0)).WithQryx();

        await app.StartAsync();
        _app = app;
        Client = app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        if (_app != null)
            await _app.DisposeAsync();
        QryxOptions.Global = _savedGlobal;
    }

    public async Task<JsonObject> GetDocumentAsync(string path)
    {
        var response = await Client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"GET {path} returned {(int)response.StatusCode}: {body}");
        return JsonNode.Parse(body)!.AsObject();
    }
}
