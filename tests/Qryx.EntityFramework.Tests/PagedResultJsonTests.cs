using System.Text.Json;
using System.Text.Json.Nodes;
using Qryx.EntityFramework.Extensions;
using Qryx.EntityFramework.Models;

namespace Qryx.EntityFramework.Tests;

/// <summary>Wire shape of <see cref="PagedResult{T}"/> with System.Text.Json web defaults (ASP.NET Core).</summary>
public class PagedResultJsonTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void AllFields_AreSerialized_InCamelCase()
    {
        var result = new PagedResult<int> { Items = [1, 2], HasNext = true, NextCursor = "abc", Total = 3 };

        Assert.Equal("""{"items":[1,2],"hasNext":true,"nextCursor":"abc","total":3}""", JsonSerializer.Serialize(result, Web));
    }

    [Fact]
    public void NullCursorAndTotal_AreOmitted()
    {
        var result = new PagedResult<int> { Items = [1], HasNext = false };

        Assert.Equal("""{"items":[1],"hasNext":false}""", JsonSerializer.Serialize(result, Web));
    }

    [Fact]
    public void ZeroTotal_IsSerialized_AndEmptyItemsIsAnArray()
    {
        var result = new PagedResult<int> { Total = 0 };

        Assert.Equal("""{"items":[],"hasNext":false,"total":0}""", JsonSerializer.Serialize(result, Web));
    }

    [Fact]
    public void RemovedLegacyFields_AreNotSerialized()
    {
        var json = JsonNode.Parse(JsonSerializer.Serialize(new PagedResult<int> { Items = [1], HasNext = true, NextCursor = "c", Total = 9 }, Web))!.AsObject();

        Assert.Equal(["items", "hasNext", "nextCursor", "total"], json.Select(p => p.Key));
        foreach (var legacy in new[] { "page", "pageSize", "totalCount", "totalPages", "hasPrevious" })
            Assert.False(json.ContainsKey(legacy), legacy);
    }

    [Fact]
    public async Task RealPages_HaveExpectedKeys()
    {
        await using var db = TestData.CreateInMemory();

        var first = await db.Products.ToPagedResultAsync(new ProductQuery { PageSize = 5 }, TestData.ToView);
        var withTotal = await db.Products.ToPagedResultAsync(new ProductQuery { PageSize = 5, IncludeTotal = true }, TestData.ToView);
        var last = await db.Products.ToPagedResultAsync(new ProductQuery { PageSize = 5, Page = 11 }, TestData.ToView);

        Assert.Equal(["items", "hasNext", "nextCursor"], Keys(first));
        Assert.Equal(["items", "hasNext", "nextCursor", "total"], Keys(withTotal));
        Assert.Equal(["items", "hasNext"], Keys(last));

        var item = JsonNode.Parse(JsonSerializer.Serialize(first, Web))!["items"]![0]!.AsObject();
        Assert.Equal(["id", "name"], item.Select(p => p.Key));
    }

    [Fact]
    public async Task NextCursor_SurvivesJsonRoundTrip()
    {
        await using var db = TestData.CreateInMemory();
        var first = await db.Products.ToPagedResultAsync(new ProductQuery { PageSize = 5, SortBy = "createdAt desc, price" }, TestData.ToView);

        var cursor = JsonNode.Parse(JsonSerializer.Serialize(first, Web))!["nextCursor"]!.GetValue<string>();
        Assert.Equal(first.NextCursor, cursor);
        Assert.Matches("^[A-Za-z0-9_-]+$", cursor); // base64url, URL-safe without escaping

        var next = await db.Products.ToPagedResultAsync(new ProductQuery { PageSize = 5, SortBy = "createdAt desc, price", AfterCursor = cursor }, TestData.ToView);
        Assert.Equal(
            TestData.Expected(s => s.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Price).ThenBy(x => x.Id)).Skip(5).Take(5),
            next.Items.Select(x => x.Id));
    }

    private static IEnumerable<string> Keys<T>(PagedResult<T> result) =>
        JsonNode.Parse(JsonSerializer.Serialize(result, Web))!.AsObject().Select(p => p.Key).ToList();
}
