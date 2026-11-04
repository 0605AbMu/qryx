using Qryx.Core.Exceptions;
using Qryx.EntityFramework.Extensions;

namespace Qryx.EntityFramework.Tests;

public class OffsetPaginationTests
{
    [Fact]
    public async Task HasNext_ComesFromProbeRow()
    {
        await using var db = TestData.CreateInMemory();

        var middle = await db.Products.ToPagedResultAsync(new ProductQuery { Page = 5, PageSize = 10 });
        Assert.True(middle.HasNext);
        Assert.Equal(Enumerable.Range(41, 10), middle.Items.Select(x => x.Id));
        Assert.NotNull(middle.NextCursor);
        Assert.Null(middle.Total);

        var last = await db.Products.ToPagedResultAsync(new ProductQuery { Page = 6, PageSize = 10 }, TestData.ToView);
        Assert.False(last.HasNext);
        Assert.Null(last.NextCursor);
        Assert.Equal([51, 52, 53], last.Items.Select(x => x.Id));

        // Exactly a full last page: the probe row is absent, so no next page
        var exact = await db.Products.ToPagedResultAsync(new ProductQuery { Page = 1, PageSize = TestData.RowCount });
        Assert.False(exact.HasNext);
        Assert.Null(exact.NextCursor);
        Assert.Equal(TestData.RowCount, exact.Items.Count);

        var beyond = await db.Products.ToPagedResultAsync(new ProductQuery { Page = 7, PageSize = 10 });
        Assert.Empty(beyond.Items);
        Assert.False(beyond.HasNext);
        Assert.Null(beyond.NextCursor);
    }

    [Fact]
    public async Task PageSizeOne_WalksAllPages()
    {
        await using var db = TestData.CreateInMemory(rowCount: 3);

        var pages = new List<(int Id, bool HasNext)>();
        for (var p = 1; p <= 4; p++)
        {
            var page = await db.Products.ToPagedResultAsync(new ProductQuery { Page = p, PageSize = 1 });
            pages.AddRange(page.Items.Select(x => (x.Id, page.HasNext)));
        }

        Assert.Equal([(1, true), (2, true), (3, false)], pages);
    }

    [Theory]
    [MemberData(nameof(SortCase.Names), MemberType = typeof(SortCase))]
    public async Task OffsetPages_FollowReferenceOrdering(string sortCase)
    {
        var sort = SortCase.All[sortCase];
        await using var db = TestData.CreateInMemory();

        var ids = new List<int>();
        for (var p = 1; p <= 6; p++)
            ids.AddRange((await db.Products.ToPagedResultAsync(sort.Query(10, p), TestData.ToView)).Items.Select(x => x.Id));

        Assert.Equal(sort.ExpectedIds(), ids);
    }

    [Fact]
    public async Task SortBy_OnUniqueColumnDesc()
    {
        await using var db = TestData.CreateInMemory();

        var page = await db.Products.ToPagedResultAsync(new ProductQuery { PageSize = 3, SortBy = "code desc" }, TestData.ToView);

        Assert.Equal(TestData.Expected(s => s.OrderByDescending(x => x.Code)).Take(3), page.Items.Select(x => x.Id));
    }

    [Fact]
    public async Task SortBy_MoreThanFiveClauses_OnlyFirstFiveAreUsed()
    {
        await using var db = TestData.CreateInMemory();
        var five = SortCase.All["five keys"];

        var six = await db.Products.ToPagedResultAsync(new ProductQuery { PageSize = 10, SortBy = five.SortBy + ", name desc" });
        var reference = await db.Products.ToPagedResultAsync(five.Query(10));

        Assert.Equal(reference.Items.Select(x => x.Id), six.Items.Select(x => x.Id));
        Assert.Equal(reference.NextCursor, six.NextCursor);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task PageBelowOne_IsClampedToFirstPage(int page)
    {
        await using var db = TestData.CreateInMemory();

        var result = await db.Products.ToPagedResultAsync(new ProductQuery { Page = page, PageSize = 2 });

        Assert.Equal([1, 2], result.Items.Select(x => x.Id));
    }

    [Fact]
    public async Task NullPageAndPageSize_UseDefaults()
    {
        await using var db = TestData.CreateInMemory();

        var result = await db.Products.ToPagedResultAsync(new ProductQuery { Page = null, PageSize = null });

        Assert.Equal(Enumerable.Range(1, 20), result.Items.Select(x => x.Id));
        Assert.True(result.HasNext);
    }

    [Theory]
    [InlineData(1001, 10)]  // offset 10,000 == MaxOffset: allowed
    [InlineData(101, 100)]  // offset 10,000
    public async Task MaxOffset_Default_AllowsOffsetUpToLimit(int page, int pageSize)
    {
        await using var db = TestData.CreateInMemory();

        var result = await db.Products.ToPagedResultAsync(new ProductQuery { Page = page, PageSize = pageSize, IncludeTotal = true });

        Assert.Empty(result.Items);
        Assert.Equal(TestData.RowCount, result.Total);
    }

    [Theory]
    [InlineData(1002, 10)]
    [InlineData(102, 100)]
    [InlineData(int.MaxValue, 100)]
    public async Task MaxOffset_Default_DeeperPage_IsRejected(int page, int pageSize)
    {
        await using var db = TestData.CreateInMemory();
        var query = new ProductQuery { Page = page, PageSize = pageSize };

        var ex = await Assert.ThrowsAsync<QryxRequestException>(() => db.Products.ToPagedResultAsync(query));
        var view = await Assert.ThrowsAsync<QryxRequestException>(() => db.Products.ToPagedResultAsync(query, TestData.ToView));
        var stream = Assert.Throws<QryxRequestException>(() => db.Products.ToPagedAsyncEnumerable(query));
        var streamView = Assert.Throws<QryxRequestException>(() => db.Products.ToPagedAsyncEnumerable(query, TestData.ToView));

        Assert.All(new[] { ex, view, stream, streamView }, e => Assert.Equal("Page", e.ParamName));
        Assert.Contains("afterCursor", ex.Reason); // points the client to keyset pagination
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(3, false)]
    [InlineData(6, true)]
    [InlineData(9, false)]
    public async Task Streaming_Offset_ProducesSameRowsAsBuffered(int page, bool includeTotal)
    {
        await using var db = TestData.CreateInMemory();
        var query = new ProductQuery { Page = page, PageSize = 10, SortBy = "rank desc, name", IncludeTotal = includeTotal };

        var buffered = await db.Products.ToPagedResultAsync(query);
        var bufferedViews = await db.Products.ToPagedResultAsync(query, TestData.ToView);
        var streamed = await db.Products.ToPagedAsyncEnumerable(query).Select(x => x.Id).ToListAsync();
        var streamedViews = await db.Products.ToPagedAsyncEnumerable(query, TestData.ToView).Select(x => x.Id).ToListAsync();

        Assert.Equal(buffered.Items.Select(x => x.Id), streamed);
        Assert.Equal(bufferedViews.Items.Select(x => x.Id), streamedViews);
        Assert.Equal(page <= 6, streamed.Count > 0);
    }

    [Fact]
    public async Task Streaming_DeveloperOrderBy_IsKept()
    {
        await using var db = TestData.CreateInMemory();

        var streamed = await db.Products.OrderByDescending(x => x.Id)
            .ToPagedAsyncEnumerable(new ProductQuery { PageSize = 3, Page = 2 }, TestData.ToView)
            .Select(x => x.Id).ToListAsync();

        Assert.Equal([50, 49, 48], streamed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Streaming_HonorsCancellationToken(bool keyset)
    {
        await using var db = TestData.CreateInMemory();
        var cursor = (await db.Products.ToPagedResultAsync(new ProductQuery { PageSize = 5 })).NextCursor;
        var query = new ProductQuery { PageSize = 5, AfterCursor = keyset ? cursor : null };
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var entities = db.Products.ToPagedAsyncEnumerable(query, cts.Token);
        var views = db.Products.ToPagedAsyncEnumerable(query, TestData.ToView, cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in entities)
            {
            }
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in views)
            {
            }
        });
    }

    [Fact]
    public async Task Streaming_CancellationDuringEnumeration_Stops()
    {
        await using var db = TestData.CreateInMemory();
        using var cts = new CancellationTokenSource();
        var seen = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in db.Products.ToPagedAsyncEnumerable(new ProductQuery { PageSize = 50 }, cts.Token))
            {
                if (++seen == 3)
                    await cts.CancelAsync();
            }
        });

        Assert.Equal(3, seen);
    }

    [Fact]
    public async Task Buffered_HonorsCancellationToken()
    {
        await using var db = TestData.CreateInMemory();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.Products.ToPagedResultAsync(new ProductQuery(), cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.Products.ToPagedResultAsync(new ProductQuery(), TestData.ToView, cts.Token));
    }

    [Fact]
    public async Task NullArguments_Throw()
    {
        await using var db = TestData.CreateInMemory();

        await Assert.ThrowsAsync<ArgumentNullException>(() => db.Products.ToPagedResultAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => db.Products.ToPagedResultAsync<Product, ProductView>(new ProductQuery(), null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => ((IQueryable<Product>)null!).ToPagedResultAsync(new ProductQuery()));
        Assert.Throws<ArgumentNullException>(() => db.Products.ToPagedAsyncEnumerable(null!));
        Assert.Throws<ArgumentNullException>(() => db.Products.ToPagedAsyncEnumerable<Product, ProductView>(new ProductQuery(), null!));
        Assert.Throws<ArgumentNullException>(() => db.Products.ApplyQryx(null!));
    }

    [Fact]
    public async Task ApplyQryx_AppliesFilterAndDeterministicSort()
    {
        await using var db = TestData.CreateInMemory();

        var ids = db.Products.ApplyQryx(new ProductQuery { RankIs = 2, SortBy = "name desc" }).Select(x => x.Id).ToList();

        Assert.Equal(TestData.Expected(s => s.Where(x => x.Rank == 2).OrderByDescending(x => x.Name).ThenByDescending(x => x.Id)), ids);
    }
}
