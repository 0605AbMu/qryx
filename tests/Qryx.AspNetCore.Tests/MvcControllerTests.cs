using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Qryx.AspNetCore.Tests.Infrastructure;
using Qryx.EntityFramework.Extensions;
using Qryx.EntityFramework.Models;
using Qryx.Expressions.Options;

namespace Qryx.AspNetCore.Tests;

[ApiController]
[Route("mvc/items")]
public sealed class ItemsController(TestDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<Item>>> Get([FromQuery] ItemQuery query, CancellationToken ct) =>
        Ok(await db.Items.AsNoTracking().ToPagedResultAsync(query, ct));

    [HttpGet("echo")]
    public ActionResult<ItemQuery> Echo([FromQuery] ItemQuery query) => Ok(query);

    [HttpGet("stream")]
    public IAsyncEnumerable<Item> Stream([FromQuery] ItemQuery query, CancellationToken ct) =>
        db.Items.AsNoTracking().ToPagedAsyncEnumerable(query, ct);
}

public sealed class MvcControllerTests : GlobalOptionsScope
{
    private static async Task<(WebApplication App, HttpClient Client)> StartAsync(Action<QryxOptions>? configure = null)
    {
        var app = await TestHostFactory.StartAsync(a => a.MapControllers(), configure, addControllers: true);
        return (app, app.GetTestClient());
    }

    [Fact]
    public async Task ValidRequest_Returns200_WithItemsTotalAndHasNext()
    {
        var (app, client) = await StartAsync();
        await using var appScope = app;

        var response = await client.GetAsync("/mvc/items?pageSize=5&page=3&includeTotal=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await TestHostFactory.ReadJsonAsync(response);
        Assert.Equal(2, json.GetProperty("items").GetArrayLength());
        Assert.Equal(12, json.GetProperty("total").GetInt32());
        Assert.False(json.GetProperty("hasNext").GetBoolean());
    }

    [Fact]
    public async Task Binder_BindsAllStandardProperties()
    {
        var (app, client) = await StartAsync();
        await using var appScope = app;

        var json = await TestHostFactory.ReadJsonAsync(await client.GetAsync(
            "/mvc/items/echo?page=2&pageSize=7&sortBy=Name&sortBy=Age%20desc&isDescending=true" +
            "&afterCursor=abc&includeTotal=true"));

        Assert.Equal(2, json.GetProperty("page").GetInt32());
        Assert.Equal(7, json.GetProperty("pageSize").GetInt32());
        Assert.Equal("Name,Age desc", json.GetProperty("sortBy").GetString());
        Assert.True(json.GetProperty("isDescending").GetBoolean());
        Assert.Equal("abc", json.GetProperty("afterCursor").GetString());
        Assert.True(json.GetProperty("includeTotal").GetBoolean());
        Assert.False(json.TryGetProperty("cursorProperty", out _));
    }

    [Fact]
    public async Task Binder_BindsRangeDateOnlyEnumAndCollections()
    {
        var (app, client) = await StartAsync();
        await using var appScope = app;

        var json = await TestHostFactory.ReadJsonAsync(await client.GetAsync(
            "/mvc/items/echo?age=20..30&createdOn=2024-01-05&createdBetween=2024-01-02..&status=archived&ids=1&ids=&ids=3"));

        var age = json.GetProperty("age");
        Assert.Equal(20, age.GetProperty("from").GetInt32());
        Assert.Equal(30, age.GetProperty("to").GetInt32());
        Assert.Equal("2024-01-05", json.GetProperty("createdOn").GetString());
        Assert.Equal("2024-01-02", json.GetProperty("createdBetween").GetProperty("from").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("createdBetween").GetProperty("to").ValueKind);
        Assert.Equal((int)ItemStatus.Archived, json.GetProperty("status").GetInt32());
        Assert.Equal([1, 3], json.GetProperty("ids").EnumerateArray().Select(e => e.GetInt32()).ToArray());
    }

    [Fact]
    public async Task Binder_AcceptsNumericEnumValues()
    {
        var (app, client) = await StartAsync();
        await using var appScope = app;

        var json = await TestHostFactory.ReadJsonAsync(await client.GetAsync("/mvc/items/echo?status=1"));

        Assert.Equal((int)ItemStatus.Pending, json.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Binder_ClampsPageSizeAndAppliesDefaults_FromOptions()
    {
        var (app, client) = await StartAsync(o =>
        {
            o.MaxPageSize = 6;
            o.DefaultPageSize = 4;
        });
        await using var appScope = app;

        var clamped = await TestHostFactory.ReadJsonAsync(await client.GetAsync("/mvc/items/echo?pageSize=1000&page=0"));
        Assert.Equal(6, clamped.GetProperty("pageSize").GetInt32());
        Assert.Equal(1, clamped.GetProperty("page").GetInt32());

        var defaulted = await TestHostFactory.ReadJsonAsync(await client.GetAsync("/mvc/items/echo"));
        Assert.Equal(4, defaulted.GetProperty("pageSize").GetInt32());

        var page = await TestHostFactory.ReadJsonAsync(await client.GetAsync("/mvc/items?pageSize=1000"));
        Assert.Equal(6, page.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Total_IsOmittedUnlessRequested()
    {
        var (app, client) = await StartAsync();
        await using var appScope = app;

        var json = await TestHostFactory.ReadJsonAsync(await client.GetAsync("/mvc/items?pageSize=5"));
        var explicitFalse = await TestHostFactory.ReadJsonAsync(await client.GetAsync("/mvc/items?pageSize=5&includeTotal=false"));

        Assert.False(json.TryGetProperty("total", out _));
        Assert.False(explicitFalse.TryGetProperty("total", out _));
        Assert.True(json.TryGetProperty("nextCursor", out _));
        Assert.True(json.GetProperty("hasNext").GetBoolean());
        Assert.Equal(5, json.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task RangeFilter_IsApplied()
    {
        var (app, client) = await StartAsync();
        await using var appScope = app;

        var json = await TestHostFactory.ReadJsonAsync(await client.GetAsync("/mvc/items?age=20..22&createdOn=2024-01-03&includeTotal=true"));

        Assert.Equal(1, json.GetProperty("total").GetInt32());
        Assert.Equal(3, json.GetProperty("items")[0].GetProperty("id").GetInt32());
    }

    [Theory]
    [InlineData("status=Bogus", "status")]
    [InlineData("status=99", "status")]
    [InlineData("age=30..20", "age")]
    [InlineData("age=abc", "age")]
    [InlineData("createdOn=not-a-date", "createdOn")]
    [InlineData("ids=1&ids=x", "ids")]
    [InlineData("page=abc", "page")]
    [InlineData("pageSize=big", "pageSize")]
    [InlineData("includeTotal=maybe", "includeTotal")]
    [InlineData("isDescending=2", "isDescending")]
    public async Task InvalidValue_Returns400_ViaModelState(string queryString, string expectedKey)
    {
        var (app, client) = await StartAsync();
        await using var appScope = app;

        await TestHostFactory.AssertValidationProblemAsync(await client.GetAsync("/mvc/items?" + queryString), expectedKey);
    }

    [Fact]
    public async Task InFilterWith1001Values_Returns400_ViaModelState()
    {
        var (app, client) = await StartAsync();
        await using var appScope = app;

        var queryString = string.Join("&", Enumerable.Range(1, 1001).Select(i => $"ids={i}"));

        var messages = await TestHostFactory.AssertValidationProblemAsync(await client.GetAsync("/mvc/items?" + queryString), "ids");
        Assert.Contains("1000", messages[0]);
    }

    [Theory]
    [InlineData("statusName=Bogus", "statusName")]
    [InlineData("afterCursor=abc", "afterCursor")]
    [InlineData("page=999999", "page")]
    public async Task QryxRequestException_FromAction_Returns400_ViaExceptionFilter(string queryString, string expectedKey)
    {
        var (app, client) = await StartAsync();
        await using var appScope = app;

        var response = await client.GetAsync("/mvc/items?" + queryString);

        await TestHostFactory.AssertValidationProblemAsync(response, expectedKey);
    }

    [Fact]
    public async Task DisallowedSortBy_WithThrowOnInvalidSortProperty_Returns400()
    {
        var (app, client) = await StartAsync(o => o.ThrowOnInvalidSortProperty = true);
        await using var appScope = app;

        await TestHostFactory.AssertValidationProblemAsync(await client.GetAsync("/mvc/items?sortBy=Secret"), "sortBy");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/mvc/items?sortBy=Age")).StatusCode);
    }

    [Fact]
    public async Task ExceptionFilter_ProducesApiControllerShapedProblem()
    {
        var (app, client) = await StartAsync();
        await using var appScope = app;

        var json = await TestHostFactory.ReadJsonAsync(await client.GetAsync("/mvc/items?afterCursor=abc"));

        Assert.Equal("One or more validation errors occurred.", json.GetProperty("title").GetString());
        Assert.True(json.TryGetProperty("traceId", out _));
    }

    [Fact]
    public async Task StreamingAction_Works_AndEagerQueryErrorsReturn400()
    {
        var (app, client) = await StartAsync();
        await using var appScope = app;

        var ok = await client.GetAsync("/mvc/items/stream?pageSize=4");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(4, (await TestHostFactory.ReadJsonAsync(ok)).GetArrayLength());

        await TestHostFactory.AssertValidationProblemAsync(await client.GetAsync("/mvc/items/stream?afterCursor=abc"), "afterCursor");
    }
}
