using Qryx.EntityFramework.Extensions;
using Qryx.EntityFramework.Models;

namespace Qryx.EntityFramework.Tests;

/// <summary>
/// Cursor walks: start with an OFFSET page (<c>?page=1</c>), then follow <c>nextCursor</c> as <c>afterCursor</c>
/// until <c>hasNext</c> is false. Every row must be returned exactly once, in the reference ordering
/// (sortBy clauses + primary-key tie-breaker).
/// </summary>
public class KeysetPaginationTests
{
    private const int PageSize = 7;

    private delegate Task<PagedResult<int>> FetchIds(TestDbContext db, ProductQuery query);

    private static readonly FetchIds Entities = async (db, q) =>
    {
        var page = await db.Products.ToPagedResultAsync(q);
        return new PagedResult<int> { Items = page.Items.Select(x => x.Id).ToList(), HasNext = page.HasNext, NextCursor = page.NextCursor, Total = page.Total };
    };

    private static readonly FetchIds Views = async (db, q) =>
    {
        var page = await db.Products.ToPagedResultAsync(q, TestData.ToView);
        return new PagedResult<int> { Items = page.Items.Select(x => x.Id).ToList(), HasNext = page.HasNext, NextCursor = page.NextCursor, Total = page.Total };
    };

    private static readonly FetchIds Scalars = async (db, q) =>
    {
        var page = await db.Products.ToPagedResultAsync(q, x => x.Id);
        return new PagedResult<int> { Items = page.Items.ToList(), HasNext = page.HasNext, NextCursor = page.NextCursor, Total = page.Total };
    };

    private static FetchIds Variant(string variant) => variant switch
    {
        "entity" => Entities,
        "projection" => Views,
        "scalar" => Scalars,
        _ => throw new ArgumentOutOfRangeException(nameof(variant))
    };

    public static TheoryData<string, string> WalkCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var name in SortCase.All.Keys)
        {
            data.Add(name, "entity");
            data.Add(name, "projection");
        }

        data.Add("rank desc, name asc", "scalar");
        data.Add("five keys", "scalar");
        return data;
    }

    private static async Task<(List<int> Ids, List<string?> Cursors)> WalkAsync(TestDbContext db, FetchIds fetch, ProductQuery first)
    {
        var ids = new List<int>();
        var cursors = new List<string?>();
        var query = first;

        for (var pages = 1; ; pages++)
        {
            Assert.True(pages <= TestData.RowCount + 1, "Walk did not terminate.");

            var page = await fetch(db, query);
            ids.AddRange(page.Items);
            cursors.Add(page.NextCursor);

            if (!page.HasNext)
            {
                Assert.Null(page.NextCursor); // no cursor without a next page
                break;
            }

            Assert.Equal(PageSize, page.Items.Count);
            Assert.False(string.IsNullOrEmpty(page.NextCursor), "A keyset-capable sort must return nextCursor whenever hasNext is true.");
            query = first with { AfterCursor = page.NextCursor };
        }

        return (ids, cursors);
    }

    [Theory]
    [MemberData(nameof(WalkCases))]
    public async Task Walk_FromOffsetPage1_WithNextCursor_ReturnsEveryRowOnce_InReferenceOrder(string sortCase, string variant)
    {
        var sort = SortCase.All[sortCase];
        await using var db = TestData.CreateInMemory();

        var (ids, cursors) = await WalkAsync(db, Variant(variant), sort.Query(PageSize));

        Assert.Equal(sort.ExpectedIds(), ids);
        Assert.Equal(TestData.RowCount, ids.Distinct().Count());
        Assert.Equal((int)Math.Ceiling(TestData.RowCount / (double)PageSize), cursors.Count);
    }

    [Theory]
    [MemberData(nameof(SortCase.Names), MemberType = typeof(SortCase))]
    public async Task Walk_ProjectionCursors_AreIdenticalToEntityCursors(string sortCase)
    {
        var sort = SortCase.All[sortCase];
        await using var db = TestData.CreateInMemory();

        var (_, entityCursors) = await WalkAsync(db, Entities, sort.Query(PageSize));
        var (_, viewCursors) = await WalkAsync(db, Views, sort.Query(PageSize));
        var (_, scalarCursors) = await WalkAsync(db, Scalars, sort.Query(PageSize));

        Assert.Equal(entityCursors, viewCursors);
        Assert.Equal(entityCursors, scalarCursors);
    }

    [Theory]
    [MemberData(nameof(SortCase.Names), MemberType = typeof(SortCase))]
    public async Task OffsetPageN_NextCursor_YieldsExactlyOffsetPageNPlus1(string sortCase)
    {
        var sort = SortCase.All[sortCase];
        await using var db = TestData.CreateInMemory();
        var pageCount = (int)Math.Ceiling(TestData.RowCount / (double)PageSize);

        for (var n = 1; n < pageCount; n++)
        {
            var offsetN = await db.Products.ToPagedResultAsync(sort.Query(PageSize, n));
            var offsetNext = await db.Products.ToPagedResultAsync(sort.Query(PageSize, n + 1));
            Assert.True(offsetN.HasNext);
            Assert.NotNull(offsetN.NextCursor);

            var keyset = await db.Products.ToPagedResultAsync(sort.Query(PageSize) with { AfterCursor = offsetN.NextCursor });
            var keysetViews = await db.Products.ToPagedResultAsync(sort.Query(PageSize) with { AfterCursor = offsetN.NextCursor }, TestData.ToView);

            Assert.Equal(offsetNext.Items.Select(x => x.Id), keyset.Items.Select(x => x.Id));
            Assert.Equal(offsetNext.Items.Select(x => x.Id), keysetViews.Items.Select(x => x.Id));
            Assert.Equal(offsetNext.HasNext, keyset.HasNext);
            Assert.Equal(offsetNext.NextCursor, keyset.NextCursor); // same last row -> same cursor
        }
    }

    [Fact]
    public async Task OffsetPages_ReturnNextCursor_EvenWithoutKeysetRequest()
    {
        await using var db = TestData.CreateInMemory();

        var page = await db.Products.ToPagedResultAsync(new ProductQuery { Page = 3, PageSize = 5, SortBy = "rank" });
        var view = await db.Products.ToPagedResultAsync(new ProductQuery { Page = 3, PageSize = 5, SortBy = "rank" }, TestData.ToView);

        Assert.True(page.HasNext);
        Assert.NotNull(page.NextCursor);
        Assert.Equal(page.NextCursor, view.NextCursor);
        Assert.Null(page.Total);
    }

    [Fact]
    public async Task Keyset_IgnoresPage()
    {
        await using var db = TestData.CreateInMemory();
        var first = await db.Products.ToPagedResultAsync(new ProductQuery { PageSize = 5 });

        // Page is irrelevant once afterCursor is set (even a page deeper than MaxOffset is not rejected)
        var a = await db.Products.ToPagedResultAsync(new ProductQuery { PageSize = 5, AfterCursor = first.NextCursor, Page = 1 });
        var b = await db.Products.ToPagedResultAsync(new ProductQuery { PageSize = 5, AfterCursor = first.NextCursor, Page = 1_000_000 });

        Assert.Equal([6, 7, 8, 9, 10], a.Items.Select(x => x.Id));
        Assert.Equal(a.Items.Select(x => x.Id), b.Items.Select(x => x.Id));
    }

    [Fact]
    public async Task Keyset_LastPage_ExactlyFull_HasNoNext()
    {
        await using var db = TestData.CreateInMemory(rowCount: 10);
        var first = await db.Products.ToPagedResultAsync(new ProductQuery { PageSize = 5 });
        Assert.True(first.HasNext);

        var last = await db.Products.ToPagedResultAsync(new ProductQuery { PageSize = 5, AfterCursor = first.NextCursor });
        Assert.Equal([6, 7, 8, 9, 10], last.Items.Select(x => x.Id));
        Assert.False(last.HasNext);
        Assert.Null(last.NextCursor);
    }

    [Fact]
    public async Task Keyset_WithFilter_WalksOnlyMatchingRows()
    {
        await using var db = TestData.CreateInMemory();
        var first = new ProductQuery { RankIs = 2, SortBy = "name desc", PageSize = 4 };
        var expected = TestData.Expected(s => s.Where(x => x.Rank == 2).OrderByDescending(x => x.Name).ThenByDescending(x => x.Id));

        var ids = new List<int>();
        var query = first;
        while (true)
        {
            var page = await db.Products.ToPagedResultAsync(query, TestData.ToView);
            ids.AddRange(page.Items.Select(x => x.Id));
            if (!page.HasNext)
                break;
            query = first with { AfterCursor = page.NextCursor };
        }

        Assert.Equal(expected, ids);
    }

    [Fact]
    public async Task Keyset_CanonicalSortBy_SpellingVariants_ShareCursors()
    {
        await using var db = TestData.CreateInMemory();
        var page = await db.Products.ToPagedResultAsync(new ProductQuery { SortBy = "Rank desc, Name asc", PageSize = 5 });

        // Case, whitespace, duplicate clauses and an explicit-vs-default direction resolve to the same plan
        string[] variants = ["rank desc,name", " RANK DESC , name ASC ", "rank desc, name asc, rank", "rank desc, name asc, Secret"];
        foreach (var sortBy in variants)
        {
            var next = await db.Products.ToPagedResultAsync(new ProductQuery { SortBy = sortBy, PageSize = 5, AfterCursor = page.NextCursor });
            Assert.Equal(
                TestData.Expected(s => s.OrderByDescending(x => x.Rank).ThenBy(x => x.Name).ThenBy(x => x.Id)).Skip(5).Take(5),
                next.Items.Select(x => x.Id));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Streaming_Keyset_ProducesSameRowsAsBuffered(bool descending)
    {
        await using var db = TestData.CreateInMemory();
        var first = await db.Products.ToPagedResultAsync(new ProductQuery { SortBy = "rank", IsDescending = descending, PageSize = PageSize });
        var query = new ProductQuery { SortBy = "rank", IsDescending = descending, PageSize = PageSize, AfterCursor = first.NextCursor };

        var buffered = await db.Products.ToPagedResultAsync(query);
        var bufferedViews = await db.Products.ToPagedResultAsync(query, TestData.ToView);
        var streamed = await db.Products.ToPagedAsyncEnumerable(query).Select(x => x.Id).ToListAsync();
        var streamedViews = await db.Products.ToPagedAsyncEnumerable(query, TestData.ToView).Select(x => x.Id).ToListAsync();

        Assert.Equal(PageSize, buffered.Items.Count);
        Assert.Equal(buffered.Items.Select(x => x.Id), streamed);
        Assert.Equal(bufferedViews.Items.Select(x => x.Id), streamedViews);
        Assert.Equal(streamed, streamedViews);
    }

    [Fact]
    public async Task Projection_AnonymousSelector_ProducesSameCursor()
    {
        await using var db = TestData.CreateInMemory();
        var query = new ProductQuery { SortBy = SortCase.All["five keys"].SortBy, PageSize = 6 };

        var entity = await db.Products.ToPagedResultAsync(query);
        var anonymous = await db.Products.ToPagedResultAsync(query, x => new { x.Id, Upper = x.Name.ToUpper() });

        Assert.Equal(entity.Items.Select(x => x.Id), anonymous.Items.Select(x => x.Id));
        Assert.Equal(entity.Items.Select(x => x.Name.ToUpper()), anonymous.Items.Select(x => x.Upper));
        Assert.NotNull(entity.NextCursor);
        Assert.Equal(entity.NextCursor, anonymous.NextCursor);
        Assert.Equal(7, TestData.DecodeCursor(entity.NextCursor!).Count); // fingerprint + 5 sortBy keys + Id
    }
}
