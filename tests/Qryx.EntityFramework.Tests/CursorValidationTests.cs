using System.Text.Json.Nodes;
using Qryx.Core.Exceptions;
using Qryx.EntityFramework.Extensions;

namespace Qryx.EntityFramework.Tests;

/// <summary>
/// Client errors around <c>afterCursor</c>: every one must be a <see cref="QryxRequestException"/> (HTTP 400) with
/// ParamName <c>afterCursor</c>, thrown by the buffered AND the streaming API (the latter when the method is called).
/// </summary>
public class CursorValidationTests
{
    private const string CursorParam = "afterCursor";

    private static async Task<QryxRequestException> AssertRejectedEverywhereAsync(TestDbContext db, ProductQuery query, IQueryable<Product>? source = null)
    {
        source ??= db.Products;

        var entity = await Assert.ThrowsAsync<QryxRequestException>(() => source.ToPagedResultAsync(query));
        var view = await Assert.ThrowsAsync<QryxRequestException>(() => source.ToPagedResultAsync(query, TestData.ToView));
        var stream = Assert.Throws<QryxRequestException>(() => source.ToPagedAsyncEnumerable(query));
        var streamView = Assert.Throws<QryxRequestException>(() => source.ToPagedAsyncEnumerable(query, TestData.ToView));

        foreach (var ex in new[] { entity, view, stream, streamView })
            Assert.Equal(CursorParam, ex.ParamName);

        Assert.Equal(entity.Reason, view.Reason);
        return entity;
    }

    private static async Task<string> CursorAsync(TestDbContext db, string? sortBy, bool isDescending = false)
    {
        var page = await db.Products.ToPagedResultAsync(new ProductQuery { SortBy = sortBy, IsDescending = isDescending, PageSize = 5 });
        return page.NextCursor ?? throw new InvalidOperationException("expected a cursor");
    }

    [Theory]
    [InlineData("rank", false, "name", false)]
    [InlineData("rank", false, "rank desc", false)]
    [InlineData("rank", false, "rank", true)]                       // default direction flips the clause
    [InlineData("rank desc, name asc", false, "rank desc", false)]  // fewer keys
    [InlineData("rank desc", false, "rank desc, name", false)]      // more keys
    [InlineData("rank desc, name", false, "name, rank desc", false)] // same keys, different order
    [InlineData(null, false, null, true)]                           // default PK asc vs desc
    [InlineData(null, false, "rank", false)]
    [InlineData("rank", false, null, false)]
    public async Task Cursor_ReusedWithDifferentSort_IsRejected(string? createdWith, bool createdDesc, string? usedWith, bool usedDesc)
    {
        await using var db = TestData.CreateInMemory();
        var cursor = await CursorAsync(db, createdWith, createdDesc);

        // Always a 400 (the message is checked in the tests below)
        await AssertRejectedEverywhereAsync(db, new ProductQuery { SortBy = usedWith, IsDescending = usedDesc, AfterCursor = cursor });
    }

    [Theory] // regression: the fingerprint is checked before the key count
    [InlineData("rank desc, name asc", "rank desc")]
    [InlineData("rank desc", "rank desc, name")]
    public async Task Cursor_ReusedWithDifferentSort_DifferentKeyCount_SaysDifferentSortBy(string createdWith, string usedWith)
    {
        await using var db = TestData.CreateInMemory();
        var cursor = await CursorAsync(db, createdWith);

        var ex = await AssertRejectedEverywhereAsync(db, new ProductQuery { SortBy = usedWith, AfterCursor = cursor });

        Assert.Contains("different sortBy", ex.Reason);
    }

    [Fact]
    public async Task Cursor_ReusedWithDifferentSort_SameKeyCount_SaysDifferentSortBy()
    {
        await using var db = TestData.CreateInMemory();
        var cursor = await CursorAsync(db, "rank");

        var ex = await AssertRejectedEverywhereAsync(db, new ProductQuery { SortBy = "name", AfterCursor = cursor });

        Assert.Contains("different sortBy", ex.Reason);
    }

    [Fact]
    public async Task Cursor_WithForgedFingerprint_SaysDifferentSortBy()
    {
        await using var db = TestData.CreateInMemory();
        var payload = TestData.DecodeCursor(await CursorAsync(db, "rank"));
        payload[0] = "deadbeef";

        var ex = await AssertRejectedEverywhereAsync(db, new ProductQuery { SortBy = "rank", AfterCursor = TestData.EncodeCursor(payload) });

        Assert.Contains("different sortBy", ex.Reason);
    }

    public static TheoryData<string> GarbageCursors()
    {
        var data = new TheoryData<string>
        {
            "not-a-cursor!!",                                 // not base64url
            "abc",                                            // base64url, not JSON
            "0",                                              // legacy raw primary-key cursor
            "42",
            TestData.EncodeCursor(JsonValue.Create(42)!),     // JSON, not an array
            TestData.EncodeCursor(new JsonObject { ["id"] = 1 }),
            TestData.EncodeCursor(new JsonArray()),           // empty array
            new string('A', 5000),                            // longer than the cursor limit
        };
        return data;
    }

    [Theory]
    [MemberData(nameof(GarbageCursors))]
    public async Task GarbageCursor_IsRejected(string cursor)
    {
        await using var db = TestData.CreateInMemory();

        var ex = await AssertRejectedEverywhereAsync(db, new ProductQuery { SortBy = "rank", AfterCursor = cursor });

        Assert.Contains("invalid", ex.Reason);
    }

    [Theory]
    [InlineData("wrong-length")]
    [InlineData("string-for-int")]
    [InlineData("fraction-for-int")]
    [InlineData("null-value")]
    [InlineData("object-value")]
    [InlineData("int-for-string")]
    [InlineData("bad-date")]
    [InlineData("bad-guid")]
    public async Task TamperedCursor_IsRejected(string tamper)
    {
        await using var db = TestData.CreateInMemory();
        // Keys: Rank(int), Name(string), CreatedAt(DateTime), Code(Guid), Id(int)
        const string sortBy = "rank, name, createdAt, code";
        var payload = TestData.DecodeCursor(await CursorAsync(db, sortBy));
        Assert.Equal(6, payload.Count);

        switch (tamper)
        {
            case "wrong-length": payload.RemoveAt(payload.Count - 1); break;
            case "string-for-int": payload[1] = "one"; break;
            case "fraction-for-int": payload[1] = 1.5; break;
            case "null-value": payload[5] = null; break;
            case "object-value": payload[1] = new JsonObject { ["x"] = 1 }; break;
            case "int-for-string": payload[2] = 7; break;
            case "bad-date": payload[3] = "yesterday"; break;
            case "bad-guid": payload[4] = "not-a-guid"; break;
        }

        await AssertRejectedEverywhereAsync(db, new ProductQuery { SortBy = sortBy, AfterCursor = TestData.EncodeCursor(payload) });
    }

    [Fact]
    public async Task ReEncodedCursor_WithValidValues_IsAccepted()
    {
        // The cursor is opaque but not signed: a well-formed payload with the right fingerprint just seeks there.
        await using var db = TestData.CreateInMemory();
        var payload = TestData.DecodeCursor(await CursorAsync(db, null));
        payload[1] = 40;

        var page = await db.Products.ToPagedResultAsync(new ProductQuery { PageSize = 3, AfterCursor = TestData.EncodeCursor(payload) });

        Assert.Equal([41, 42, 43], page.Items.Select(x => x.Id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankCursor_MeansOffsetPagination(string cursor)
    {
        await using var db = TestData.CreateInMemory();

        var page = await db.Products.ToPagedResultAsync(new ProductQuery { Page = 2, PageSize = 5, AfterCursor = cursor });

        Assert.Equal([6, 7, 8, 9, 10], page.Items.Select(x => x.Id));
    }

    // ---- Nullable sort keys: offset works, keyset does not ----

    [Theory]
    [InlineData("category")]          // string?
    [InlineData("score desc")]        // int?
    [InlineData("rank, category")]    // a nullable key anywhere in the plan
    public async Task NullableSortKey_OffsetWorks_ButNoNextCursor(string sortBy)
    {
        await using var db = TestData.CreateInMemory();

        var entity = await db.Products.ToPagedResultAsync(new ProductQuery { SortBy = sortBy, PageSize = 5 });
        var view = await db.Products.ToPagedResultAsync(new ProductQuery { SortBy = sortBy, PageSize = 5 }, TestData.ToView);

        Assert.True(entity.HasNext);
        Assert.Null(entity.NextCursor);
        Assert.Equal(5, entity.Items.Count);
        Assert.True(view.HasNext);
        Assert.Null(view.NextCursor);
        Assert.Equal(entity.Items.Select(x => x.Id), view.Items.Select(x => x.Id));
    }

    [Fact]
    public async Task NullableSortKey_OffsetOrdering_HasPrimaryKeyTieBreaker()
    {
        await using var db = TestData.CreateInMemory();
        var expected = TestData.Expected(s => s.OrderBy(x => x.Category).ThenBy(x => x.Id));

        var ids = new List<int>();
        for (var p = 1; p <= 6; p++)
            ids.AddRange((await db.Products.ToPagedResultAsync(new ProductQuery { SortBy = "category", PageSize = 10, Page = p })).Items.Select(x => x.Id));

        Assert.Equal(expected, ids);
    }

    [Theory]
    [InlineData("category")]
    [InlineData("score")]
    public async Task NullableSortKey_AfterCursor_IsRejected(string sortBy)
    {
        await using var db = TestData.CreateInMemory();
        var cursor = await CursorAsync(db, null); // any well-formed cursor

        var ex = await AssertRejectedEverywhereAsync(db, new ProductQuery { SortBy = sortBy, AfterCursor = cursor });

        Assert.Contains("nullable", ex.Reason);
    }

    // ---- Developer OrderBy on the source ----

    [Fact]
    public async Task DeveloperOrderBy_IsKept_WhenNoSortBy_AndHasNoNextCursor()
    {
        await using var db = TestData.CreateInMemory();
        var source = db.Products.OrderByDescending(x => x.Price).ThenBy(x => x.Id);
        var expected = TestData.Expected(s => s.OrderByDescending(x => x.Price).ThenBy(x => x.Id));

        var page1 = await source.ToPagedResultAsync(new ProductQuery { PageSize = 5 });
        var page2 = await source.ToPagedResultAsync(new ProductQuery { PageSize = 5, Page = 2 }, TestData.ToView);
        var streamed = await source.ToPagedAsyncEnumerable(new ProductQuery { PageSize = 5 }).Select(x => x.Id).ToListAsync();

        Assert.Equal(expected.Take(5), page1.Items.Select(x => x.Id));
        Assert.Equal(expected.Skip(5).Take(5), page2.Items.Select(x => x.Id));
        Assert.Equal(expected.Take(5), streamed);
        Assert.True(page1.HasNext);
        Assert.Null(page1.NextCursor);
        Assert.True(page2.HasNext);
        Assert.Null(page2.NextCursor);
    }

    [Fact]
    public async Task DeveloperOrderBy_IsKept_WhenSortByIsInvalid()
    {
        await using var db = TestData.CreateInMemory();
        var source = db.Products.OrderByDescending(x => x.Id);

        var page = await source.ToPagedResultAsync(new ProductQuery { PageSize = 3, SortBy = "Secret" });

        Assert.Equal([53, 52, 51], page.Items.Select(x => x.Id));
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task DeveloperOrderBy_IsReplaced_ByValidSortBy_AndCursorWorks()
    {
        await using var db = TestData.CreateInMemory();
        var source = db.Products.OrderByDescending(x => x.Price);
        var expected = TestData.Expected(s => s.OrderBy(x => x.Rank).ThenBy(x => x.Id));

        var page1 = await source.ToPagedResultAsync(new ProductQuery { PageSize = 5, SortBy = "rank" });
        var page2 = await source.ToPagedResultAsync(new ProductQuery { PageSize = 5, SortBy = "rank", AfterCursor = page1.NextCursor });

        Assert.Equal(expected.Take(5), page1.Items.Select(x => x.Id));
        Assert.NotNull(page1.NextCursor);
        Assert.Equal(expected.Skip(5).Take(5), page2.Items.Select(x => x.Id));
    }

    [Fact]
    public async Task DeveloperOrderBy_WithAfterCursor_IsRejected()
    {
        await using var db = TestData.CreateInMemory();
        var cursor = await CursorAsync(db, null);
        var source = db.Products.OrderBy(x => x.Name);

        var ex = await AssertRejectedEverywhereAsync(db, new ProductQuery { AfterCursor = cursor }, source);

        Assert.Contains("custom OrderBy", ex.Reason);
    }
}
