using Qryx.Core.Exceptions;
using Qryx.EntityFramework.Extensions;
using Qryx.Expressions.Options;

namespace Qryx.EntityFramework.Tests;

/// <summary>
/// Tests that mutate the process-wide <see cref="QryxOptions.Global"/> run in this collection, which xUnit
/// executes alone (after the parallel collections), so other tests never observe the modified options.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GlobalOptionsCollection
{
    public const string Name = "Qryx global options";
}

[Collection(GlobalOptionsCollection.Name)]
public sealed class GlobalOptionsTests : IDisposable
{
    private readonly QryxOptions _original = QryxOptions.Global;

    public GlobalOptionsTests()
    {
        QryxOptions.Global = new QryxOptions { MaxPageSize = 5, DefaultPageSize = 3 };
    }

    public void Dispose() => QryxOptions.Global = _original;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MaxPageSize_IsRespected_InOffsetMode(bool includeTotal)
    {
        await using var db = TestData.CreateInMemory();
        var query = new ProductQuery { PageSize = 100, IncludeTotal = includeTotal };

        var entities = await db.Products.ToPagedResultAsync(query);
        var views = await db.Products.ToPagedResultAsync(query, TestData.ToView);

        Assert.Equal([1, 2, 3, 4, 5], entities.Items.Select(x => x.Id));
        Assert.Equal([1, 2, 3, 4, 5], views.Items.Select(x => x.Id));
        Assert.True(entities.HasNext); // the probe row (pageSize + 1) is not cut by the MaxPageSize clamp
        Assert.True(views.HasNext);
        Assert.Equal(includeTotal ? TestData.RowCount : null, entities.Total);
    }

    [Fact]
    public async Task MaxPageSize_IsRespected_InKeysetMode()
    {
        await using var db = TestData.CreateInMemory();
        var first = await db.Products.ToPagedResultAsync(new ProductQuery { PageSize = 100 });
        var query = new ProductQuery { AfterCursor = first.NextCursor, PageSize = 100 };

        var entities = await db.Products.ToPagedResultAsync(query);
        var views = await db.Products.ToPagedResultAsync(query, TestData.ToView);

        Assert.Equal([6, 7, 8, 9, 10], entities.Items.Select(x => x.Id));
        Assert.True(entities.HasNext);
        Assert.NotNull(entities.NextCursor);
        Assert.Equal([6, 7, 8, 9, 10], views.Items.Select(x => x.Id));
        Assert.True(views.HasNext);
        Assert.Equal(entities.NextCursor, views.NextCursor);
    }

    [Fact]
    public async Task MaxPageSize_IsRespected_InStreaming()
    {
        await using var db = TestData.CreateInMemory();
        var first = await db.Products.ToPagedResultAsync(new ProductQuery());

        var offset = await db.Products.ToPagedAsyncEnumerable(new ProductQuery { PageSize = 100 }).ToListAsync();
        var keyset = await db.Products.ToPagedAsyncEnumerable(new ProductQuery { AfterCursor = first.NextCursor, PageSize = 100 }, TestData.ToView).ToListAsync();

        Assert.Equal(5, offset.Count);
        Assert.Equal(5, keyset.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task DefaultPageSize_IsUsed_ForNonPositivePageSize(int pageSize)
    {
        await using var db = TestData.CreateInMemory();
        var first = await db.Products.ToPagedResultAsync(new ProductQuery { PageSize = pageSize });

        var keyset = await db.Products.ToPagedResultAsync(new ProductQuery { AfterCursor = first.NextCursor, PageSize = pageSize }, TestData.ToView);
        var streamed = await db.Products.ToPagedAsyncEnumerable(new ProductQuery { PageSize = pageSize }).ToListAsync();
        var page2 = await db.Products.ToPagedResultAsync(new ProductQuery { PageSize = pageSize, Page = 2 });

        Assert.Equal([1, 2, 3], first.Items.Select(x => x.Id));
        Assert.Equal([4, 5, 6], keyset.Items.Select(x => x.Id));
        Assert.Equal(3, streamed.Count);
        Assert.Equal([4, 5, 6], page2.Items.Select(x => x.Id)); // offset uses the same clamped size
    }

    [Fact]
    public async Task ThrowOnInvalidSortProperty_IsPassedToSortBuilder()
    {
        await using var db = TestData.CreateInMemory();
        var query = new ProductQuery { SortBy = "Secret" };

        // Default: silently ignored, PK fallback
        var lenient = await db.Products.ToPagedResultAsync(query);
        Assert.Equal([1, 2, 3, 4, 5], lenient.Items.Select(x => x.Id));

        QryxOptions.Global.ThrowOnInvalidSortProperty = true;
        Assert.Throws<QryxRequestException>(() => db.Products.ApplyQryx(query));
        await Assert.ThrowsAsync<QryxRequestException>(() => db.Products.ToPagedResultAsync(query));
        await Assert.ThrowsAsync<QryxRequestException>(() => db.Products.ToPagedResultAsync(query, TestData.ToView));
        Assert.Throws<QryxRequestException>(() => db.Products.ToPagedAsyncEnumerable(query));

        var tooMany = new ProductQuery { SortBy = "status, rank, createdAt, price, code, name" };
        var ex = await Assert.ThrowsAsync<QryxRequestException>(() => db.Products.ToPagedResultAsync(tooMany));
        Assert.Equal("SortBy", ex.ParamName);
    }

    [Fact]
    public async Task MaxOffset_Custom_IsEnforced()
    {
        QryxOptions.Global.MaxOffset = 20;
        await using var db = TestData.CreateInMemory();

        var allowed = await db.Products.ToPagedResultAsync(new ProductQuery { Page = 5, PageSize = 5 }); // offset 20
        Assert.Equal(Enumerable.Range(21, 5), allowed.Items.Select(x => x.Id));
        Assert.NotNull(allowed.NextCursor);

        var ex = await Assert.ThrowsAsync<QryxRequestException>(() => db.Products.ToPagedResultAsync(new ProductQuery { Page = 6, PageSize = 5 }));
        Assert.Equal("Page", ex.ParamName);
        Assert.Throws<QryxRequestException>(() => db.Products.ToPagedAsyncEnumerable(new ProductQuery { Page = 6, PageSize = 5 }, TestData.ToView));

        // Deeper rows stay reachable with the cursor of the last allowed offset page
        var deeper = await db.Products.ToPagedResultAsync(new ProductQuery { PageSize = 5, AfterCursor = allowed.NextCursor });
        Assert.Equal(Enumerable.Range(26, 5), deeper.Items.Select(x => x.Id));
    }

    [Fact]
    public async Task MaxOffset_Zero_AllowsOnlyFirstPage()
    {
        QryxOptions.Global.MaxOffset = 0;
        await using var db = TestData.CreateInMemory();

        var first = await db.Products.ToPagedResultAsync(new ProductQuery { Page = 1 });
        Assert.Equal(5, first.Items.Count); // PageSize 20 clamped to MaxPageSize 5

        await Assert.ThrowsAsync<QryxRequestException>(() => db.Products.ToPagedResultAsync(new ProductQuery { Page = 2 }));
    }

    [Fact]
    public async Task MaxOffset_Null_DisablesLimit()
    {
        QryxOptions.Global = new QryxOptions { MaxOffset = null };
        await using var db = TestData.CreateInMemory();

        var deep = await db.Products.ToPagedResultAsync(new ProductQuery { Page = 1_000_000, PageSize = 100, IncludeTotal = true });
        var max = await db.Products.ToPagedResultAsync(new ProductQuery { Page = int.MaxValue, PageSize = 100 });

        Assert.Empty(deep.Items);
        Assert.False(deep.HasNext);
        Assert.Equal(TestData.RowCount, deep.Total);
        Assert.Empty(max.Items);
    }
}
