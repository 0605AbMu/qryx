using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Qryx.AspNetCore.Extensions;
using Qryx.AspNetCore.Tests.Infrastructure;
using Qryx.EntityFramework.Extensions;

namespace Qryx.AspNetCore.Tests;

/// <summary>
/// Enum values in the query string. Enum.TryParse reads "Active,Archived" as the flags value Active|Archived,
/// which for a non-[Flags] enum is a different single member (here: Archived) - a silently wrong filter.
/// Both binders must reject it; QryxList&lt;T&gt; is the supported comma-separated list type.
/// </summary>
public sealed class EnumBindingTests : GlobalOptionsScope
{
    private static async Task<(WebApplication App, HttpClient Client)> StartAsync()
    {
        var app = await TestHostFactory.StartAsync(
            a =>
            {
                a.MapControllers();
                a.MapGet("/items", async ([AsParameters] ItemQuery query, TestDbContext db, CancellationToken ct) =>
                    Results.Ok(await db.Items.AsNoTracking().ToPagedResultAsync(query, ct))).WithQryx();
            },
            addControllers: true);
        return (app, app.GetTestClient());
    }

    private static async Task<int[]> IdsAsync(HttpClient client, string url)
    {
        var json = await TestHostFactory.ReadJsonAsync(await client.GetAsync(url));
        return json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt32()).Order().ToArray();
    }

    [Theory]
    [InlineData("/items")]
    [InlineData("/mvc/items")]
    public async Task CommaSeparatedEnumArray_IsRejected_NotSilentlyMisread(string path)
    {
        var (app, client) = await StartAsync();
        await using var _ = app;

        var messages = await TestHostFactory.AssertValidationProblemAsync(
            await client.GetAsync($"{path}?statuses=Active,Archived&pageSize=100"), "statuses");

        Assert.Contains(messages, m => m.Contains("Active,Archived"));
    }

    [Theory]
    [InlineData("/items")]
    [InlineData("/mvc/items")]
    public async Task RepeatedKeys_And_QryxListComma_SelectTheSameRows(string path)
    {
        var (app, client) = await StartAsync();
        await using var _ = app;

        var repeated = await IdsAsync(client, $"{path}?statuses=Active&statuses=Archived&pageSize=100");
        var listComma = await IdsAsync(client, $"{path}?statusList=Active,Archived&pageSize=100");
        var listRepeated = await IdsAsync(client, $"{path}?statusList=Active&statusList=Archived&pageSize=100");
        var archivedOnly = await IdsAsync(client, $"{path}?statuses=Archived&pageSize=100");

        Assert.NotEmpty(repeated);
        Assert.Equal(repeated, listComma);
        Assert.Equal(repeated, listRepeated);
        Assert.True(repeated.Length > archivedOnly.Length, "Active,Archived must not collapse to Archived only");
    }

    [Fact]
    public async Task MinimalApi_LowercaseEnumName_Returns400_WithCaseSensitivityHint()
    {
        var (app, client) = await StartAsync();
        await using var _ = app;

        var messages = await TestHostFactory.AssertValidationProblemAsync(await client.GetAsync("/items?status=active"), "status");

        Assert.Contains(messages, m => m.Contains("case-sensitive"));
    }

    [Fact]
    public async Task Mvc_LowercaseEnumName_IsAccepted()
    {
        var (app, client) = await StartAsync();
        await using var _ = app;

        var lower = await IdsAsync(client, "/mvc/items?status=active&pageSize=100");
        var exact = await IdsAsync(client, "/mvc/items?status=Active&pageSize=100");

        Assert.NotEmpty(exact);
        Assert.Equal(exact, lower);
    }
}
