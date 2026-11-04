using Qryx.EntityFramework.Extensions;
using Qryx.EntityFramework.Models;

namespace Qryx.EntityFramework.Tests;

/// <summary>
/// <c>includeTotal</c>: off by default; when on, <c>total</c> is the number of rows matching the filters for every
/// page shape (on the last offset page it is derived as skip + items, otherwise COUNT); <c>[NoTotal]</c> DTOs never
/// return it.
/// </summary>
public class TotalTests
{
    private static async Task<(PagedResult<Product> Entity, PagedResult<ProductView> View)> BothAsync(TestDbContext db, ProductQuery query) =>
        (await db.Products.ToPagedResultAsync(query), await db.Products.ToPagedResultAsync(query, TestData.ToView));

    [Theory]
    [InlineData(1, 10)]   // first page
    [InlineData(3, 10)]   // middle page
    [InlineData(6, 10)]   // last, partial page (skip + items, no COUNT)
    [InlineData(1, 53)]   // single exactly-full page
    [InlineData(1, 100)]  // single partial page
    [InlineData(2, 53)]   // empty page right after the end
    [InlineData(7, 10)]   // empty page beyond the end (must fall back to COUNT)
    [InlineData(50, 10)]  // far beyond the end
    public async Task IncludeTotal_Offset_IsCorrectOnEveryPage(int page, int pageSize)
    {
        await using var db = TestData.CreateInMemory();
        var query = new ProductQuery { Page = page, PageSize = pageSize, IncludeTotal = true };

        var (entity, view) = await BothAsync(db, query);

        Assert.Equal(TestData.RowCount, entity.Total);
        Assert.Equal(TestData.RowCount, view.Total);

        var expectedItems = Math.Clamp(TestData.RowCount - (page - 1) * pageSize, 0, pageSize);
        Assert.Equal(expectedItems, entity.Items.Count);
        Assert.Equal(expectedItems, view.Items.Count);
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(3, 5)]   // last page of the filtered rows (14 rows with Rank == 1)
    [InlineData(4, 5)]   // empty page beyond the filtered end
    public async Task IncludeTotal_CountsFilteredRows(int page, int pageSize)
    {
        await using var db = TestData.CreateInMemory();
        var expected = TestData.All.Count(x => x.Rank == 1);

        var (entity, view) = await BothAsync(db, new ProductQuery { RankIs = 1, Page = page, PageSize = pageSize, IncludeTotal = true });

        Assert.Equal(expected, entity.Total);
        Assert.Equal(expected, view.Total);
    }

    [Fact]
    public async Task IncludeTotal_Keyset_IsTotalOfAllMatchingRows_NotTheRemainder()
    {
        await using var db = TestData.CreateInMemory();
        var first = await db.Products.ToPagedResultAsync(new ProductQuery { SortBy = "rank", PageSize = 20 });

        var middle = new ProductQuery { SortBy = "rank", PageSize = 20, AfterCursor = first.NextCursor, IncludeTotal = true };
        var (entity, view) = await BothAsync(db, middle);
        Assert.Equal(TestData.RowCount, entity.Total);
        Assert.Equal(TestData.RowCount, view.Total);

        // Last keyset page (13 rows, hasNext false) still reports the full total
        var last = await db.Products.ToPagedResultAsync(middle with { AfterCursor = entity.NextCursor });
        Assert.False(last.HasNext);
        Assert.Equal(13, last.Items.Count);
        Assert.Equal(TestData.RowCount, last.Total);
    }

    [Fact]
    public async Task IncludeTotal_EmptyTable_IsZero()
    {
        await using var db = TestData.CreateInMemory(rowCount: 0);

        var (entity, view) = await BothAsync(db, new ProductQuery { IncludeTotal = true });
        var beyond = await db.Products.ToPagedResultAsync(new ProductQuery { Page = 3, IncludeTotal = true });

        Assert.Equal(0, entity.Total);
        Assert.Equal(0, view.Total);
        Assert.Equal(0, beyond.Total);
        Assert.Empty(entity.Items);
        Assert.False(entity.HasNext);
        Assert.Null(entity.NextCursor);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task Total_IsNull_UnlessRequested(bool? includeTotal)
    {
        await using var db = TestData.CreateInMemory();
        var first = await db.Products.ToPagedResultAsync(new ProductQuery { PageSize = 10, IncludeTotal = includeTotal });

        PagedResult<ProductView>[] pages =
        [
            await db.Products.ToPagedResultAsync(new ProductQuery { PageSize = 10, Page = 3, IncludeTotal = includeTotal }, TestData.ToView),
            await db.Products.ToPagedResultAsync(new ProductQuery { PageSize = 10, Page = 6, IncludeTotal = includeTotal }, TestData.ToView),
            await db.Products.ToPagedResultAsync(new ProductQuery { PageSize = 10, Page = 9, IncludeTotal = includeTotal }, TestData.ToView),
            await db.Products.ToPagedResultAsync(new ProductQuery { PageSize = 10, AfterCursor = first.NextCursor, IncludeTotal = includeTotal }, TestData.ToView),
        ];

        Assert.Null(first.Total);
        Assert.All(pages, p => Assert.Null(p.Total));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(9)]
    public async Task NoTotalDto_NeverReturnsTotal(int page)
    {
        await using var db = TestData.CreateInMemory();
        var query = new NoTotalProductQuery { Page = page, PageSize = 10, IncludeTotal = true };

        var entity = await db.Products.ToPagedResultAsync(query);
        var view = await db.Products.ToPagedResultAsync(query, TestData.ToView);

        Assert.Null(entity.Total);
        Assert.Null(view.Total);
        Assert.Equal(page == 1, entity.HasNext);
    }

    [Fact]
    public async Task NoTotalDto_Keyset_NeverReturnsTotal_PagingStillWorks()
    {
        await using var db = TestData.CreateInMemory();
        var first = await db.Products.ToPagedResultAsync(new NoTotalProductQuery { PageSize = 10, IncludeTotal = true });
        var next = await db.Products.ToPagedResultAsync(new NoTotalProductQuery { PageSize = 10, IncludeTotal = true, AfterCursor = first.NextCursor });

        Assert.Null(first.Total);
        Assert.NotNull(first.NextCursor);
        Assert.Null(next.Total);
        Assert.Equal(Enumerable.Range(11, 10), next.Items.Select(x => x.Id));
    }
}
