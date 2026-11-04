using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Qryx.Core.Attributes;
using Qryx.Core.Contracts;
using Qryx.Core.Enums;
using Qryx.Core.Exceptions;
using Qryx.Core.Types;
using Qryx.EntityFramework.Extensions;
using Qryx.Expressions.Builders;
using Qryx.Expressions.Metadata;

namespace Qryx.Tests;

/// <summary>
/// Security / robustness guards of the core engine: bounded caches, cursor whitelist,
/// client errors as QryxRequestException, In-list limit, Range/Between, PostgreSQL FTS and jsonb translation.
/// </summary>
public class CoreHardeningTests
{
    public enum Level
    {
        Low,
        High
    }

    public class Product
    {
        public int Id { get; set; }

        [Sortable]
        public string Name { get; set; } = string.Empty;

        [Sortable]
        public int Rank { get; set; }

        public decimal Price { get; set; }
        public Level Level { get; set; }
        public Guid Code { get; set; }
        public DateTime CreatedAt { get; set; }
        public string SecretHash { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Metadata { get; set; } = "{}";
    }

    /// <summary>Separate entity so cache-size assertions are not affected by other tests.</summary>
    public class CacheProbeEntity
    {
        public int Id { get; set; }

        [Sortable]
        public string Title { get; set; } = string.Empty;
    }

    public record ProductQuery : QryxRequest<Product>
    {
        [Filter(nameof(Product.Level), Op.Equals)]
        public string? LevelName { get; init; }

        [Filter(nameof(Product.Code), Op.Equals)]
        public string? CodeText { get; init; }

        [Filter(nameof(Product.Id), Op.In)]
        public int[]? Ids { get; init; }

        [Filter(nameof(Product.Price), Op.Between)]
        public Range<decimal>? PriceRange { get; init; }

        [Filter(nameof(Product.CreatedAt), Op.Between)]
        public Range<DateTime>? Created { get; init; }

        [Filter(nameof(Product.Description), Op.Fts)]
        public string? Search { get; init; }

        [Filter(nameof(Product.Metadata), Op.JsonContains)]
        public string? Meta { get; init; }

        [Sortable(nameof(Product.Price))]
        public string? PriceSort { get; init; }
    }

    public class NpgsqlContext : DbContext
    {
        public DbSet<Product> Products => Set<Product>();

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder.UseNpgsql("Host=localhost;Database=qryx_tests");

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Product>().Property(p => p.Metadata).HasColumnType("jsonb");
    }

    public class InMemoryContext(DbContextOptions<InMemoryContext> options) : DbContext(options)
    {
        public DbSet<Product> Products => Set<Product>();
    }

    private static InMemoryContext CreateInMemory(params Product[] products)
    {
        var options = new DbContextOptionsBuilder<InMemoryContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new InMemoryContext(options);
        db.Products.AddRange(products);
        db.SaveChanges();
        return db;
    }

    // ---------- 1. Bounded caches ----------

    [Fact]
    public void RandomSortBy_ShouldNotGrowPropertyCache()
    {
        var data = new List<CacheProbeEntity>().AsQueryable();
        var metadata = MetadataCache.GetEntityMetadata<CacheProbeEntity>();
        SortBuilder.Apply(data, "Title").ToList();
        var before = metadata.CachedPropertyCount;

        for (int i = 0; i < 2000; i++)
        {
            SortBuilder.Apply(data, "junk" + i).ToList();
        }

        Assert.Equal(before, metadata.CachedPropertyCount);
    }

    [Fact]
    public void CasingVariants_ShouldReuseSingleCachedSortStep()
    {
        var data = new List<CacheProbeEntity>().AsQueryable();
        SortBuilder.Apply(data, "Title").ToList();
        var before = SortBuilder.CountCachedSortSteps(typeof(CacheProbeEntity));

        foreach (var variant in new[] { "title", "TITLE", "tItLe", "TiTlE asc" })
        {
            SortBuilder.Apply(data, variant).ToList();
        }

        Assert.Equal(before, SortBuilder.CountCachedSortSteps(typeof(CacheProbeEntity)));
    }

    [Fact]
    public void OverlongSortBy_ShouldBeIgnored_OrRejectedWhenThrowing()
    {
        var data = new List<Product> { new() { Id = 2, Name = "b" }, new() { Id = 1, Name = "a" } }.AsQueryable();
        var longSort = new string('x', 300);

        Assert.Same(data, SortBuilder.Apply(data, longSort));
        var ex = Assert.Throws<QryxRequestException>(() => SortBuilder.Apply(data, longSort, throwOnInvalidProperty: true));
        Assert.Equal("SortBy", ex.ParamName);
    }

    [Fact]
    public void SortBy_ShouldApplyAtMostFiveClauses()
    {
        var data = new List<WideEntity>().AsQueryable();
        const string sortBy = "a, b, c, d, e, f";

        var sorted = SortBuilder.Apply(data, sortBy);

        // 5 client clauses + primary-key tie-breaker; the 6th clause (f) is dropped
        Assert.Equal(6, CountOrderingCalls(sorted.Expression));
        Assert.Equal(["A", "B", "C", "D", "E", "Id"], SortBuilder.ResolveSort<WideEntity>(sortBy).Keys.Select(k => k.Path));
        Assert.Throws<QryxRequestException>(() => SortBuilder.Apply(data, sortBy, throwOnInvalidProperty: true));
    }

    [Fact]
    public void DuplicateSortClauses_ShouldBeMergedIntoOneKey()
    {
        var plan = SortBuilder.ResolveSort<WideEntity>("a, A desc, a");

        Assert.Equal([new QryxSortKey("A", false), new QryxSortKey("Id", false)], plan.Keys);
    }

    public class WideEntity
    {
        public int Id { get; set; }
        [Sortable] public int A { get; set; }
        [Sortable] public int B { get; set; }
        [Sortable] public int C { get; set; }
        [Sortable] public int D { get; set; }
        [Sortable] public int E { get; set; }
        [Sortable] public int F { get; set; }
    }

    [Fact]
    public void RequestWithOnlyInvalidSortBy_ShouldStillBeDeterministic()
    {
        var data = new List<Product> { new() { Id = 3 }, new() { Id = 1 }, new() { Id = 2 } }.AsQueryable();
        var request = new ProductQuery { SortBy = "SecretHash" };

        var ids = SortBuilder.Apply(data, request).Select(p => p.Id).ToList();

        Assert.Equal([1, 2, 3], ids);
    }

    private static int CountOrderingCalls(Expression expression)
    {
        int count = 0;
        while (expression is MethodCallExpression call)
        {
            if (call.Method.Name.StartsWith("OrderBy", StringComparison.Ordinal) ||
                call.Method.Name.StartsWith("ThenBy", StringComparison.Ordinal))
            {
                count++;
            }
            expression = call.Arguments[0];
        }
        return count;
    }

    // ---------- 2. Sort plan + keyset cursors derived from sortBy ----------

    [Fact]
    public void SortPlan_ShouldUseWhitelistedCanonicalKeys_PlusPrimaryKeyTieBreaker()
    {
        var plan = SortBuilder.ResolveSort<Product>("rank desc, NAME, secretHash, rank", dtoType: typeof(ProductQuery));

        // secretHash is not [Sortable] -> skipped; duplicate rank removed; Id appended with the last clause's direction
        Assert.Equal(
            [new QryxSortKey("Rank", true), new QryxSortKey("Name", false), new QryxSortKey("Id", false)],
            plan.Keys);
        Assert.True(plan.HasClientKeys);
    }

    [Fact]
    public void SortPlan_WithoutSortBy_IsPrimaryKey_InRequestedDirection()
    {
        var plan = SortBuilder.ResolveSort<Product>(sortBy: null, isDescending: true);

        Assert.Equal([new QryxSortKey("Id", true)], plan.Keys);
        Assert.False(plan.HasClientKeys);
    }

    [Fact]
    public void DtoOnlySortable_IsAllowed_OnlyWithDtoType()
    {
        // Price is [Sortable] only on the DTO (via [Sortable(nameof(Product.Price))])
        Assert.DoesNotContain(SortBuilder.ResolveSort<Product>("price").Keys, k => k.Path == "Price");
        Assert.Contains(SortBuilder.ResolveSort<Product>("price", dtoType: typeof(ProductQuery)).Keys, k => k.Path == "Price");
    }

    [Theory]
    [InlineData("rank")]
    [InlineData("rank desc")]
    [InlineData("rank desc, name asc")]
    [InlineData("name, rank desc")]
    [InlineData(null)]
    public void KeysetCursor_ShouldWalkTiesAndMixedDirections_WithoutSkippingOrDuplicating(string? sortBy)
    {
        // Rank has many duplicates: rows sharing the last value of a page must not be skipped
        var products = Enumerable.Range(1, 23).Select(i => new Product { Id = i, Rank = i % 4, Name = "n" + (i % 3) }).ToList();
        var data = products.AsQueryable();
        var plan = SortBuilder.ResolveSort<Product>(sortBy);
        var expected = SortBuilder.ApplySort(data, plan).Select(p => p.Id).ToList();

        var seen = new List<int>();
        var page = SortBuilder.ApplySort(data, plan).Take(5).ToList(); // offset page 1
        while (page.Count > 0)
        {
            seen.AddRange(page.Select(p => p.Id));
            var cursor = PaginationBuilder.CreateCursor(page[^1], plan);
            Assert.NotNull(cursor);
            page = PaginationBuilder.ApplyKeyset(data, cursor, plan, 5).ToList();
        }

        Assert.Equal(expected, seen);
    }

    [Fact]
    public void Cursor_FromDifferentSortBy_ShouldBeRejected()
    {
        var data = new List<Product>().AsQueryable();
        var cursor = PaginationBuilder.CreateCursor(new Product { Id = 1, Rank = 2 }, SortBuilder.ResolveSort<Product>("rank"))!;

        var ex = Assert.Throws<QryxRequestException>(() =>
            PaginationBuilder.ApplyKeyset(data, cursor, SortBuilder.ResolveSort<Product>("rank desc"), 10));

        Assert.Equal("afterCursor", ex.ParamName);
        Assert.Contains("different sortBy", ex.Reason);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("WzEsMl0")]   // valid base64url JSON [1,2] without fingerprint
    [InlineData("2000")]      // 1.x raw primary-key cursor
    public void GarbageCursor_ShouldBeRejected(string cursor)
    {
        var data = new List<Product>().AsQueryable();

        var ex = Assert.Throws<QryxRequestException>(() =>
            PaginationBuilder.ApplyKeyset(data, cursor, SortBuilder.ResolveSort<Product>(null), 10));

        Assert.Equal("afterCursor", ex.ParamName);
    }

    [Fact]
    public void StringSortKey_ShouldTranslateToParameterizedSqlComparison()
    {
        using var db = new NpgsqlContext();
        var plan = SortBuilder.ResolveSort<Product>("name");
        var token = PaginationBuilder.CreateCursor(new Product { Id = 7, Name = "Mango" }, plan)!;

        var sql = PaginationBuilder.ApplyKeyset(db.Products, token, plan, 10).ToQueryString();

        Assert.Contains("p.\"Name\" > @", sql);
        Assert.Contains("p.\"Id\" > @", sql);
        Assert.Contains("ORDER BY p.\"Name\", p.\"Id\"", sql);
        Assert.DoesNotContain("Mango", sql.Split('\n').Where(l => !l.StartsWith("--")).Aggregate("", string.Concat));
    }

    // ---------- 3. Client errors -> QryxRequestException ----------

    [Theory]
    [InlineData("Low,High")] // Enum.Parse would read it as the flags value Low|High = High (silently wrong)
    [InlineData("7")]        // undefined numeric value of a non-[Flags] enum
    public void CommaOrUndefinedEnumValue_ShouldThrowQryxRequestException(string value)
    {
        var data = new List<Product>().AsQueryable();

        var ex = Assert.Throws<QryxRequestException>(() => FilterBuilder.Apply(data, new ProductQuery { LevelName = value }));

        Assert.Equal(nameof(ProductQuery.LevelName), ex.ParamName);
    }

    [Theory]
    [InlineData("high", Level.High)]
    [InlineData("1", Level.High)]
    public void EnumName_IsCaseInsensitive_AndDefinedNumbersAreAccepted(string value, Level expected)
    {
        var data = new List<Product> { new() { Id = 1, Level = Level.Low }, new() { Id = 2, Level = Level.High } }.AsQueryable();

        var result = FilterBuilder.Apply(data, new ProductQuery { LevelName = value }).ToList();

        Assert.Equal(expected, Assert.Single(result).Level);
    }

    [Fact]
    public void InvalidEnumString_ShouldThrowQryxRequestException_WithAllowedValues()
    {
        var data = new List<Product>().AsQueryable();

        var ex = Assert.Throws<QryxRequestException>(() => FilterBuilder.Apply(data, new ProductQuery { LevelName = "Medium" }));

        Assert.Equal(nameof(ProductQuery.LevelName), ex.ParamName);
        Assert.Contains("Low, High", ex.Reason);
    }

    [Fact]
    public void InvalidGuidString_ShouldThrowQryxRequestException()
    {
        var data = new List<Product>().AsQueryable();

        var ex = Assert.Throws<QryxRequestException>(() => FilterBuilder.Apply(data, new ProductQuery { CodeText = "not-a-guid" }));

        Assert.Equal(nameof(ProductQuery.CodeText), ex.ParamName);
    }

    [Fact]
    public async Task InvalidCursorValue_ShouldThrowQryxRequestException()
    {
        await using var db = CreateInMemory(new Product { Id = 1 });

        var ex = await Assert.ThrowsAsync<QryxRequestException>(() =>
            db.Products.ToPagedResultAsync(new ProductQuery { AfterCursor = "abc" }));

        Assert.Equal("afterCursor", ex.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankCursor_ShouldFallBackToOffsetPaging(string cursor)
    {
        await using var db = CreateInMemory(new Product { Id = 2 }, new Product { Id = 1 });

        var result = await db.Products.ToPagedResultAsync(new ProductQuery { AfterCursor = cursor, IncludeTotal = true });

        Assert.Equal(2, result.Total);
        Assert.Equal([1, 2], result.Items.Select(p => p.Id));
    }

    // ---------- 4. In-list limit ----------

    [Fact]
    public void InFilter_ShouldRejectMoreThanMaxValues()
    {
        var data = new List<Product>().AsQueryable();

        Assert.NotNull(FilterBuilder.Apply(data, new ProductQuery { Ids = Enumerable.Range(1, 1000).ToArray() }));
        var ex = Assert.Throws<QryxRequestException>(() =>
            FilterBuilder.Apply(data, new ProductQuery { Ids = Enumerable.Range(1, 1001).ToArray() }));
        Assert.Equal(nameof(ProductQuery.Ids), ex.ParamName);
    }

    // ---------- Range<T> / Between ----------

    [Theory]
    [InlineData("10..20", 10.0, 20.0)]
    [InlineData("10..", 10.0, null)]
    [InlineData("..20", null, 20.0)]
    [InlineData("1.5..2.5", 1.5, 2.5)]
    public void Range_ShouldParse(string text, double? from, double? to)
    {
        Assert.True(Range<decimal>.TryParse(text, null, out var range));
        Assert.Equal(from.HasValue ? (decimal)from.Value : null, range.From);
        Assert.Equal(to.HasValue ? (decimal)to.Value : null, range.To);
    }

    [Theory]
    [InlineData("20..10")]
    [InlineData("abc..1")]
    [InlineData("15")]
    public void Range_ShouldRejectInvalid(string text)
    {
        Assert.False(Range<int>.TryParse(text, null, out _));
    }

    [Fact]
    public async Task Between_ShouldFilterInclusiveBounds()
    {
        await using var db = CreateInMemory(
            new Product { Id = 1, Price = 5 },
            new Product { Id = 2, Price = 10 },
            new Product { Id = 3, Price = 15 },
            new Product { Id = 4, Price = 20 },
            new Product { Id = 5, Price = 25 });

        var both = await db.Products.ToPagedResultAsync(new ProductQuery { PriceRange = new Range<decimal> { From = 10, To = 20 } });
        var lower = await db.Products.ToPagedResultAsync(new ProductQuery { PriceRange = new Range<decimal> { From = 20 } });
        var none = await db.Products.ToPagedResultAsync(new ProductQuery { PriceRange = new Range<decimal>() });

        Assert.Equal([2, 3, 4], both.Items.Select(p => p.Id));
        Assert.Equal([4, 5], lower.Items.Select(p => p.Id));
        Assert.Equal(5, none.Items.Count);
    }

    [Fact]
    public void Between_ShouldBeIndexFriendlyParameterizedRange()
    {
        using var db = new NpgsqlContext();
        var request = new ProductQuery { Created = new Range<DateTime> { From = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), To = new DateTime(2024, 12, 31, 0, 0, 0, DateTimeKind.Utc) } };

        var sql = db.Products.ApplyQryx(request).ToQueryString();

        Assert.Matches(@"""CreatedAt"" >= @\w+ AND p\.""CreatedAt"" <= @\w+", sql);
    }

    // ---------- PostgreSQL FTS / jsonb ----------

    [Fact]
    public void Fts_OnPostgreSql_ShouldUseTsVectorAndWebSearchQuery()
    {
        using var db = new NpgsqlContext();

        var sql = db.Products.ApplyQryx(new ProductQuery { Search = "fast \"red car\" -used" }).ToQueryString();

        Assert.Contains("to_tsvector(p.\"Description\") @@ websearch_to_tsquery(@", sql);
    }

    [Fact]
    public void JsonContains_WithJsonObject_ShouldUseJsonbContainment()
    {
        using var db = new NpgsqlContext();

        var sql = db.Products.ApplyQryx(new ProductQuery { Meta = "{\"tag\":\"new\"}" }).ToQueryString();

        Assert.Contains("p.\"Metadata\" @> @", sql);
    }

    [Fact]
    public void JsonContains_WithPlainText_OnPostgreSql_ShouldUseKeyExistence()
    {
        using var db = new NpgsqlContext();

        var sql = db.Products.ApplyQryx(new ProductQuery { Meta = "tag" }).ToQueryString();

        // LIKE on a jsonb column fails in PostgreSQL ("jsonb ~~ jsonb"), so plain text means "top-level key exists"
        Assert.Contains("p.\"Metadata\" ? @", sql);
        Assert.DoesNotContain("LIKE", sql);
    }

    [Fact]
    public async Task JsonContains_OnNonPostgreSql_ShouldFallBackToContains()
    {
        await using var db = CreateInMemory(
            new Product { Id = 1, Metadata = "{\"tag\":\"new\"}" },
            new Product { Id = 2, Metadata = "{\"tag\":\"old\"}" });

        var result = await db.Products.ToPagedResultAsync(new ProductQuery { Meta = "new" });

        Assert.Equal([1], result.Items.Select(p => p.Id));
    }
}
