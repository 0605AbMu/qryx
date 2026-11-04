using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Qryx.AspNetCore.Extensions;
using Qryx.AspNetCore.Tests.Infrastructure;
using Qryx.EntityFramework.Extensions;
using Qryx.Expressions.Options;

namespace Qryx.AspNetCore.Tests;

/// <summary>
/// Deep OFFSET is a DoS vector (the database walks every skipped row). Offsets above QryxOptions.MaxOffset
/// (default 10,000) must become HTTP 400 that points the client to keyset pagination.
/// </summary>
public sealed class DeepPagingTests : GlobalOptionsScope
{
    private static async Task<(WebApplication App, HttpClient Client)> StartAsync(Action<QryxOptions>? configure = null)
    {
        var app = await TestHostFactory.StartAsync(
            a =>
            {
                a.MapControllers();
                a.MapGet("/items", async ([AsParameters] ItemQuery query, TestDbContext db, CancellationToken ct) =>
                    Results.Ok(await db.Items.AsNoTracking().ToPagedResultAsync(query, ct))).WithQryx();
            },
            configure,
            addControllers: true);
        return (app, app.GetTestClient());
    }

    [Theory]
    [InlineData("/items")]
    [InlineData("/mvc/items")]
    public async Task PageBeyondMaxOffset_Returns400_WithKeysetHint(string path)
    {
        var (app, client) = await StartAsync();
        await using var _ = app;

        var messages = await TestHostFactory.AssertValidationProblemAsync(
            await client.GetAsync($"{path}?page=200000&pageSize=100"), "page");

        Assert.Contains(messages, m => m.Contains("afterCursor"));
    }

    [Theory]
    [InlineData("/items")]
    [InlineData("/mvc/items")]
    public async Task LastAllowedPage_IsServed(string path)
    {
        var (app, client) = await StartAsync();
        await using var _ = app;

        // offset (101 - 1) * 100 = 10,000 == default MaxOffset
        var response = await client.GetAsync($"{path}?page=101&pageSize=100");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ConfiguredMaxOffset_IsHonored()
    {
        var (app, client) = await StartAsync(o => o.MaxOffset = 2);
        await using var _ = app;

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/items?page=2&pageSize=2")).StatusCode);
        await TestHostFactory.AssertValidationProblemAsync(await client.GetAsync("/items?page=3&pageSize=2"), "page");
    }

    [Fact]
    public async Task NullMaxOffset_DisablesTheLimit()
    {
        var (app, client) = await StartAsync(o => o.MaxOffset = null);
        await using var _ = app;

        var json = await TestHostFactory.ReadJsonAsync(await client.GetAsync("/items?page=200000&pageSize=100"));

        Assert.Empty(json.GetProperty("items").EnumerateArray());
    }
}
