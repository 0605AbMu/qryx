using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Qryx.EntityFramework.Extensions;
using Qryx.EntityFramework.Internal;
using Qryx.Expressions.Builders;
using Xunit.Abstractions;

namespace Qryx.EntityFramework.Tests;

/// <summary>
/// SQL translation checks against the Npgsql provider (no database needed: ToQueryString only).
/// </summary>
public class NpgsqlTranslationTests(ITestOutputHelper output)
{
    private static int CountOf(string sql, string token) => Regex.Matches(sql, Regex.Escape(token), RegexOptions.IgnoreCase).Count;

    /// <summary>The statement without the <c>-- @p='...'</c> parameter comment lines EF prepends.</summary>
    private static string Statement(string sql) =>
        string.Join('\n', sql.Split('\n').Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal)));

    /// <summary>A cursor for <paramref name="sortBy"/> with distinctive values (so inlined literals would be visible).</summary>
    private static string Cursor(string? sortBy, bool isDescending, params object[] values)
    {
        var plan = SortBuilder.ResolveSort<Product>(sortBy, isDescending, typeof(ProductQuery));
        return PaginationBuilder.CreateCursorFromValues<Product>(plan, values) ?? throw new InvalidOperationException("no cursor");
    }

    private string Sql(IQueryable query)
    {
        var sql = query.ToQueryString();
        output.WriteLine(sql);
        output.WriteLine("");
        return sql;
    }

    private static void AssertParameterizedSeek(string sql, params string[] forbiddenLiterals)
    {
        var statement = Statement(sql);
        Assert.Equal(1, CountOf(statement, "SELECT"));
        Assert.Contains("-- @", sql);            // values travel as parameters...
        Assert.Contains("WHERE", statement);
        Assert.Contains("LIMIT @", statement);   // ...including the limit (pageSize + 1)
        foreach (var literal in forbiddenLiterals)
            Assert.DoesNotContain(literal, statement); // ...never as inlined literals
        Assert.DoesNotMatch(@"[<>=]\s*'", statement);       // no string literal comparisons
        Assert.DoesNotMatch(@"[<>=]\s*-?\d", statement);    // no numeric literal comparisons
    }

    [Fact]
    public void EntityKeyset_MultiKeyMixedDirections_IsParameterized_WithTieBreaker()
    {
        using var db = TestData.CreateNpgsql();
        var request = new ProductQuery { SortBy = "rank desc, name asc", AfterCursor = Cursor("rank desc, name asc", false, 3, "N5", 4242), PageSize = 20 };

        var page = QryxExtensions.BuildPage(db.Products, request, 21);
        var sql = Sql(page.Query);

        Assert.True(page.IsKeyset);
        Assert.Equal(0, page.Skip);
        Assert.Equal("-Rank|+Name|+Id", page.Plan!.ToString());
        Assert.Contains("ORDER BY p.\"Rank\" DESC, p.\"Name\", p.\"Id\"", sql);
        Assert.DoesNotContain("OFFSET", sql);
        AssertParameterizedSeek(sql, "4242", "N5");
    }

    [Theory]
    [InlineData(false, "ORDER BY p.\"Rank\", p.\"Id\"")]
    [InlineData(true, "ORDER BY p.\"Rank\" DESC, p.\"Id\" DESC")]
    public void EntityKeyset_SingleNonUniqueKey_HasTieBreaker(bool descending, string orderBy)
    {
        using var db = TestData.CreateNpgsql();
        var request = new ProductQuery { SortBy = "rank", IsDescending = descending, AfterCursor = Cursor("rank", descending, 3, 4242) };

        var sql = Sql(QryxExtensions.BuildPage(db.Products, request, 21).Query);

        Assert.Contains(orderBy, sql);
        AssertParameterizedSeek(sql, "4242");
    }

    [Theory]
    [InlineData(false, "ORDER BY p.\"Id\"")]
    [InlineData(true, "ORDER BY p.\"Id\" DESC")]
    public void EntityKeyset_DefaultSort_IsPrimaryKey(bool descending, string orderBy)
    {
        using var db = TestData.CreateNpgsql();
        var request = new ProductQuery { IsDescending = descending, AfterCursor = Cursor(null, descending, 4242) };

        var sql = Sql(QryxExtensions.BuildPage(db.Products, request, 21).Query);

        Assert.Contains(orderBy, sql);
        AssertParameterizedSeek(sql, "4242");
    }

    [Fact]
    public void EntityKeyset_AllKeyTypes_AreParameterized()
    {
        using var db = TestData.CreateNpgsql();
        const string sortBy = "status, rank desc, createdAt, price desc, code";
        var code = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var request = new ProductQuery
        {
            SortBy = sortBy,
            AfterCursor = Cursor(sortBy, false, ProductStatus.Archived, 3, new DateTime(2031, 5, 6, 7, 8, 9, DateTimeKind.Utc), 98.76m, code, 4242)
        };

        var sql = Sql(QryxExtensions.BuildPage(db.Products, request, 21).Query);

        Assert.Contains("ORDER BY p.\"Status\", p.\"Rank\" DESC, p.\"CreatedAt\", p.\"Price\" DESC, p.\"Code\", p.\"Id\"", sql);
        AssertParameterizedSeek(sql, "4242", "98.76", "2031", "11111111");
    }

    [Fact]
    public void ProjectionKeyset_IsSingleParameterizedSelect_WithKeyColumns()
    {
        using var db = TestData.CreateNpgsql();
        var request = new ProductQuery { SortBy = "rank desc, name asc", AfterCursor = Cursor("rank desc, name asc", false, 3, "N5", 4242) };

        var page = QryxExtensions.BuildPage(db.Products, request, 21);
        var sql = Sql(KeysetProjection.Select(page.Query, TestData.ToView, page.Plan!));

        Assert.Contains("ORDER BY p.\"Rank\" DESC, p.\"Name\", p.\"Id\"", sql);
        AssertParameterizedSeek(sql, "4242", "N5");
        Assert.DoesNotContain("\"Secret\"", sql);  // only projected + key columns are read
        Assert.DoesNotContain("\"Category\"", sql);
        Assert.Contains("p.\"Rank\"", Statement(sql).Split("FROM")[0]); // key column selected next to the projection
    }

    [Fact]
    public void ProjectionKeyset_SixKeys_IsSingleSelect()
    {
        using var db = TestData.CreateNpgsql();
        const string sortBy = "status, rank desc, createdAt, price desc, code";
        var request = new ProductQuery
        {
            SortBy = sortBy,
            AfterCursor = Cursor(sortBy, false, ProductStatus.Active, 3, new DateTime(2031, 5, 6, 0, 0, 0, DateTimeKind.Utc), 98.76m, Guid.NewGuid(), 4242)
        };

        var page = QryxExtensions.BuildPage(db.Products, request, 21);
        Assert.Equal(KeysetRow<object>.MaxKeys, page.Plan!.Keys.Count);
        Assert.True(KeysetProjection.CanProject(page.Plan));

        var sql = Sql(KeysetProjection.Select(page.Query, x => x.Name, page.Plan));

        Assert.Equal(1, CountOf(Statement(sql), "SELECT"));
        Assert.DoesNotContain("\"Secret\"", sql);
    }

    [Fact]
    public void ProjectionKeyset_AnonymousAndScalarSelectors_Translate()
    {
        using var db = TestData.CreateNpgsql();
        var request = new ProductQuery { SortBy = "rank", AfterCursor = Cursor("rank", false, 1, 4242) };
        var page = QryxExtensions.BuildPage(db.Products, request, 21);

        var anonymous = Sql(KeysetProjection.Select(page.Query, x => new { x.Name, Upper = x.Name.ToUpper() }, page.Plan!));
        var scalar = Sql(KeysetProjection.Select(page.Query, x => x.Name, page.Plan!));

        Assert.Equal(1, CountOf(Statement(anonymous), "SELECT"));
        Assert.Contains("upper(", anonymous);
        Assert.Equal(1, CountOf(Statement(scalar), "SELECT"));
        AssertParameterizedSeek(anonymous, "4242");
    }

    [Fact]
    public void ProjectionOffset_IsSingleSelect_WithKeyColumns()
    {
        using var db = TestData.CreateNpgsql();
        var page = QryxExtensions.BuildPage(db.Products, new ProductQuery { SortBy = "rank", Page = 3, PageSize = 10 }, 11);

        var sql = Sql(KeysetProjection.Select(page.Query, TestData.ToView, page.Plan!));

        Assert.Equal(1, CountOf(Statement(sql), "SELECT"));
        Assert.Contains("ORDER BY p.\"Rank\", p.\"Id\"", sql);
    }

    [Theory]
    [InlineData(3, 10, 20)]
    [InlineData(1001, 10, 10_000)]
    public void Offset_UsesLimitAndOffsetParameters_WithTieBreaker(int pageNumber, int pageSize, int expectedSkip)
    {
        using var db = TestData.CreateNpgsql();
        var page = QryxExtensions.BuildPage(db.Products, new ProductQuery { SortBy = "rank desc", Page = pageNumber, PageSize = pageSize }, pageSize + 1);

        var sql = Sql(page.Query);

        Assert.False(page.IsKeyset);
        Assert.Equal(expectedSkip, page.Skip);
        Assert.Contains("ORDER BY p.\"Rank\" DESC, p.\"Id\" DESC", sql);
        Assert.Matches(@"LIMIT @\w+ OFFSET @\w+", Statement(sql));
        Assert.DoesNotContain(expectedSkip.ToString(), Statement(sql));
    }

    [Fact]
    public void Offset_DefaultSort_IsPrimaryKey()
    {
        using var db = TestData.CreateNpgsql();

        var sql = Sql(QryxExtensions.BuildPage(db.Products, new ProductQuery { Page = 2 }, 21).Query);

        Assert.Contains("ORDER BY p.\"Id\"", sql);
        Assert.Matches(@"LIMIT @\w+ OFFSET @\w+", Statement(sql));
    }

    [Fact]
    public void Offset_WithFilter_FilterIsParameterized()
    {
        using var db = TestData.CreateNpgsql();
        var page = QryxExtensions.BuildPage(db.Products, new ProductQuery { RankIs = 3, SortBy = "name" }, 21);

        var sql = Sql(page.Query);
        var count = Sql(page.Filtered); // the COUNT source: filter only, no paging

        Assert.Contains("p.\"Rank\" = @", sql);
        Assert.Contains("ORDER BY p.\"Name\", p.\"Id\"", sql);
        Assert.DoesNotContain("LIMIT", count);
        Assert.DoesNotContain("ORDER BY", count);
        Assert.Contains("p.\"Rank\" = @", count);
    }

    [Fact]
    public void DeveloperOrderBy_IsKept_AndHasNoPlan()
    {
        using var db = TestData.CreateNpgsql();
        var page = QryxExtensions.BuildPage(db.Products.OrderByDescending(x => x.Price), new ProductQuery(), 21);

        var sql = Sql(page.Query);

        Assert.Null(page.Plan);
        Assert.Contains("ORDER BY p.\"Price\" DESC", sql);
        Assert.DoesNotContain("\"Id\" DESC", sql);
    }

    [Fact]
    public void ApplyQryx_SortBy_HasTieBreaker()
    {
        using var db = TestData.CreateNpgsql();

        var sql = Sql(db.Products.ApplyQryx(new ProductQuery { SortBy = "Name" }));

        Assert.Contains("ORDER BY p.\"Name\", p.\"Id\"", sql);
    }

    [Fact]
    public void Cursor_Payload_IsFingerprintPlusValues()
    {
        var cursor = Cursor("rank desc, name asc", false, 3, "N5", 4242);
        var payload = TestData.DecodeCursor(cursor);

        Assert.Equal(4, payload.Count);
        Assert.Equal(JsonValueKind.String, payload[0]!.GetValueKind());
        Assert.Equal(3, payload[1]!.GetValue<int>());
        Assert.Equal("N5", payload[2]!.GetValue<string>());
        Assert.Equal(4242, payload[3]!.GetValue<int>());
    }
}
