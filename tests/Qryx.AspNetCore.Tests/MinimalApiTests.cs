using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Qryx.AspNetCore.Extensions;
using Qryx.AspNetCore.Tests.Infrastructure;
using Qryx.EntityFramework.Extensions;
using Qryx.Expressions.Options;

namespace Qryx.AspNetCore.Tests;

public sealed class MinimalApiTests : GlobalOptionsScope
{
    private static void MapEndpoints(WebApplication app)
    {
        app.MapGet("/items", async ([AsParameters] ItemQuery query, TestDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Items.AsNoTracking().ToPagedResultAsync(query, ct))).WithQryx();

        app.MapGet("/items/echo", ([AsParameters] ItemQuery query) =>
            Results.Ok(new { page = query.Page, pageSize = query.PageSize })).WithQryx();

        // Eager streaming: the query is built when the handler runs
        app.MapGet("/items/stream", ([AsParameters] ItemQuery query, TestDbContext db, CancellationToken ct) =>
            db.Items.AsNoTracking().ToPagedAsyncEnumerable(query, ct)).WithQryx();

        // Lazy streaming: the query is built on the first MoveNextAsync
        app.MapGet("/items/lazy", ([AsParameters] ItemQuery query, TestDbContext db, CancellationToken ct) =>
            LazyStream(db, query, ct)).WithQryx();

        var group = app.MapGroup("/group").WithQryx();
        group.MapGet("/items", async ([AsParameters] ItemQuery query, TestDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Items.AsNoTracking().ToPagedResultAsync(query, ct)));
    }

    private static async IAsyncEnumerable<Item> LazyStream(
        TestDbContext db,
        ItemQuery query,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        await foreach (var item in db.Items.AsNoTracking().ToPagedAsyncEnumerable(query, ct))
        {
            yield return item;
        }
    }

    private static async Task<(WebApplication App, HttpClient Client)> StartAsync(Action<QryxOptions>? configure = null)
    {
        var app = await TestHostFactory.StartAsync(MapEndpoints, configure);
        return (app, app.GetTestClient());
    }

    [Fact]
    public async Task ValidRequest_Returns200_WithItemsTotalAndHasNext()
    {
        var (app, client) = await StartAsync();
        await using var appScope = app;

        var response = await client.GetAsync("/items?pageSize=5&sortBy=Age&isDescending=true&includeTotal=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await TestHostFactory.ReadJsonAsync(response);
        Assert.Equal(5, json.GetProperty("items").GetArrayLength());
        Assert.Equal(12, json.GetProperty("total").GetInt32());
        Assert.True(json.GetProperty("hasNext").GetBoolean());
        Assert.Equal(30, json.GetProperty("items")[0].GetProperty("age").GetInt32());
    }

    [Fact]
    public async Task RouteGroup_WithQryx_AppliesFilter()
    {
        var (app, client) = await StartAsync(o => o.MaxPageSize = 2);
        await using var appScope = app;

        var response = await client.GetAsync("/group/items?pageSize=50");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await TestHostFactory.ReadJsonAsync(response);
        Assert.Equal(2, json.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task PageSizeAboveMax_IsClampedToConfiguredMaxPageSize()
    {
        var (app, client) = await StartAsync(o => o.MaxPageSize = 4);
        await using var appScope = app;

        var echo = await TestHostFactory.ReadJsonAsync(await client.GetAsync("/items/echo?pageSize=500"));
        Assert.Equal(4, echo.GetProperty("pageSize").GetInt32());

        var page = await TestHostFactory.ReadJsonAsync(await client.GetAsync("/items?pageSize=500"));
        Assert.Equal(4, page.GetProperty("items").GetArrayLength());
        Assert.True(page.GetProperty("hasNext").GetBoolean());
    }

    [Theory]
    [InlineData("", 3)]
    [InlineData("?pageSize=0", 3)]
    [InlineData("?pageSize=-5", 3)]
    [InlineData("?pageSize=7", 7)]
    public async Task MissingOrNonPositivePageSize_UsesConfiguredDefault(string queryString, int expected)
    {
        var (app, client) = await StartAsync(o => o.DefaultPageSize = 3);
        await using var appScope = app;

        var echo = await TestHostFactory.ReadJsonAsync(await client.GetAsync("/items/echo" + queryString));

        Assert.Equal(expected, echo.GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public async Task PageBelowOne_IsNormalizedToOne()
    {
        var (app, client) = await StartAsync();
        await using var appScope = app;

        var echo = await TestHostFactory.ReadJsonAsync(await client.GetAsync("/items/echo?page=-3"));

        Assert.Equal(1, echo.GetProperty("page").GetInt32());
    }

    [Fact]
    public async Task Filter_FallsBackToGlobalOptions_WhenNotRegisteredInDi()
    {
        QryxOptions.Global = new QryxOptions { MaxPageSize = 2 };

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();
        app.MapGet("/echo", ([AsParameters] ItemQuery query) => Results.Ok(new { pageSize = query.PageSize })).WithQryx();
        await app.StartAsync();

        var echo = await TestHostFactory.ReadJsonAsync(await app.GetTestClient().GetAsync("/echo?pageSize=50"));

        Assert.Equal(2, echo.GetProperty("pageSize").GetInt32());
    }

    [Theory]
    [InlineData("age=20..22", 3)]
    [InlineData("age=..20", 2)]
    [InlineData("age=29..", 2)]
    [InlineData("createdOn=2024-01-05", 1)]
    [InlineData("createdBetween=2024-01-02..2024-01-04", 3)]
    [InlineData("ids=1&ids=3&ids=99", 2)]
    [InlineData("status=Archived", 4)]
    [InlineData("statusName=Pending", 4)]
    public async Task ValidFilters_AreBoundAndApplied(string queryString, int expectedTotal)
    {
        var (app, client) = await StartAsync();
        await using var appScope = app;

        var response = await client.GetAsync("/items?" + queryString + "&includeTotal=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await TestHostFactory.ReadJsonAsync(response);
        Assert.Equal(expectedTotal, json.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task InvalidEnumString_EvaluatedByEngine_Returns400()
    {
        var (app, client) = await StartAsync();
        await using var appScope = app;

        var messages = await TestHostFactory.AssertValidationProblemAsync(
            await client.GetAsync("/items?statusName=Bogus"), "statusName");

        Assert.Contains("Bogus", messages[0]);
    }

    [Fact]
    public async Task InvalidTypedEnum_BindingFailure_Returns400ProblemWithKey()
    {
        var (app, client) = await StartAsync();
        await using var appScope = app;

        var messages = await TestHostFactory.AssertValidationProblemAsync(
            await client.GetAsync("/items?status=Bogus"), "status");

        Assert.Contains("Active", messages[0]);
    }

    [Fact]
    public async Task InvalidRange_BindingFailure_Returns400ProblemWithKey()
    {
        var (app, client) = await StartAsync();
        await using var appScope = app;

        await TestHostFactory.AssertValidationProblemAsync(await client.GetAsync("/items?age=30..20"), "age");
    }

    [Fact]
    public async Task InvalidCursor_Returns400()
    {
        var (app, client) = await StartAsync();
        await using var appScope = app;

        await TestHostFactory.AssertValidationProblemAsync(await client.GetAsync("/items?afterCursor=abc"), "afterCursor");
    }

    [Fact]
    public async Task CursorFromDifferentSortBy_Returns400()
    {
        var (app, client) = await StartAsync();
        await using var appScope = app;

        var first = await TestHostFactory.ReadJsonAsync(await client.GetAsync("/items?pageSize=3&sortBy=Name"));
        var cursor = first.GetProperty("nextCursor").GetString();

        var messages = await TestHostFactory.AssertValidationProblemAsync(
            await client.GetAsync($"/items?pageSize=3&sortBy=Age&afterCursor={cursor}"), "afterCursor");
        Assert.Contains(messages, m => m.Contains("different sortBy"));
    }

    [Fact]
    public async Task InFilterWith1001Values_Returns400()
    {
        var (app, client) = await StartAsync();
        await using var appScope = app;

        var queryString = string.Join("&", Enumerable.Range(1, 1001).Select(i => $"ids={i}"));

        await TestHostFactory.AssertValidationProblemAsync(await client.GetAsync("/items?" + queryString), "ids");
    }

    [Fact]
    public async Task DisallowedSortBy_WithThrowOnInvalidSortProperty_Returns400()
    {
        var (app, client) = await StartAsync(o => o.ThrowOnInvalidSortProperty = true);
        await using var appScope = app;

        await TestHostFactory.AssertValidationProblemAsync(await client.GetAsync("/items?sortBy=Secret"), "sortBy");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/items?sortBy=Name")).StatusCode);
    }

    [Fact]
    public async Task KeysetRequest_Returns200_WithoutTotal()
    {
        var (app, client) = await StartAsync();
        await using var appScope = app;

        // Offset page 2 (rows 6-10) returns the cursor; keyset continues with rows 11-12
        var page2 = await TestHostFactory.ReadJsonAsync(await client.GetAsync("/items?page=2&pageSize=5"));
        var cursor = page2.GetProperty("nextCursor").GetString();

        var response = await client.GetAsync($"/items?afterCursor={cursor}&pageSize=5");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await TestHostFactory.ReadJsonAsync(response);
        Assert.Equal([11, 12], json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt32()));
        Assert.False(json.TryGetProperty("total", out _));
        Assert.False(json.TryGetProperty("nextCursor", out _));
        Assert.False(json.GetProperty("hasNext").GetBoolean());
    }

    [Theory]
    [InlineData("/items/stream?pageSize=3", 3)]
    [InlineData("/items/lazy?pageSize=3", 3)]
    [InlineData("/items/lazy", 12)]
    [InlineData("/items/lazy?ids=999", 0)]
    [InlineData("/items/stream?pageSize=500", 12)]
    public async Task StreamingHandlers_StillStreamJsonArrays(string url, int expectedCount)
    {
        var (app, client) = await StartAsync();
        await using var appScope = app;

        var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await TestHostFactory.ReadJsonAsync(response);
        Assert.Equal(JsonValueKind.Array, json.ValueKind);
        Assert.Equal(expectedCount, json.GetArrayLength());
    }

    [Theory]
    [InlineData("/items/stream?afterCursor=abc", "afterCursor")]
    [InlineData("/items/lazy?afterCursor=abc", "afterCursor")]
    [InlineData("/items/lazy?statusName=Bogus", "statusName")]
    public async Task StreamingHandlers_InvalidInput_Returns400(string url, string expectedKey)
    {
        var (app, client) = await StartAsync();
        await using var appScope = app;

        await TestHostFactory.AssertValidationProblemAsync(await client.GetAsync(url), expectedKey);
    }
}
