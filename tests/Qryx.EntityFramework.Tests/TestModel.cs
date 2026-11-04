using System.Buffers.Text;
using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Qryx.Core.Attributes;
using Qryx.Core.Contracts;
using Qryx.Core.Enums;

namespace Qryx.EntityFramework.Tests;

public enum ProductStatus
{
    Draft = 0,
    Active = 1,
    Archived = 2
}

public class Product
{
    public int Id { get; set; }

    /// <summary>Non-unique sortable column (4 buckets: many ties).</summary>
    [Sortable]
    public int Rank { get; set; }

    /// <summary>Non-unique sortable string (7 distinct values).</summary>
    [Sortable]
    public string Name { get; set; } = string.Empty;

    [Sortable]
    public DateTime CreatedAt { get; set; }

    [Sortable]
    public decimal Price { get; set; }

    [Sortable]
    public ProductStatus Status { get; set; }

    /// <summary>Unique, but not ordered like Id.</summary>
    [Sortable]
    public Guid Code { get; set; }

    /// <summary>Nullable reference sort key: keyset pagination is not possible.</summary>
    [Sortable]
    public string? Category { get; set; }

    /// <summary>Nullable value sort key: keyset pagination is not possible.</summary>
    [Sortable]
    public int? Score { get; set; }

    /// <summary>Not sortable: must never be usable as a sort/cursor key.</summary>
    public string Secret { get; set; } = string.Empty;
}

public sealed class ProductView
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
}

public record ProductQuery : QryxRequest<Product>
{
    [Filter(nameof(Product.Rank), Op.Equals)]
    public int? RankIs { get; init; }
}

/// <summary>Very large table: <c>includeTotal</c> is ignored.</summary>
[NoTotal]
public record NoTotalProductQuery : QryxRequest<Product>;

public sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
}

internal static class TestData
{
    public const int RowCount = 53;
    public const int RankBuckets = 4;

    public static readonly Expression<Func<Product, ProductView>> ToView = x => new ProductView { Id = x.Id, Name = x.Name };

    private static readonly DateTime BaseDate = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The seed rows (LINQ-to-objects reference for every ordering assertion).</summary>
    public static IReadOnlyList<Product> All { get; } = Enumerable.Range(1, RowCount).Select(Create).ToList();

    private static Product Create(int id) => new()
    {
        Id = id,
        Rank = id % RankBuckets,
        Name = $"N{(id * 5) % 7}",
        CreatedAt = BaseDate.AddDays(id % 3).AddHours(id % 2),
        Price = 10m + (id % 5) * 0.25m,
        Status = (ProductStatus)(id % 3),
        Code = new Guid((id * 7919) % 1000, (short)(id % 11), 0, 1, 2, 3, 4, 5, 6, 7, (byte)id),
        Category = id % 3 == 0 ? null : $"C{id % 5}",
        Score = id % 2 == 0 ? id : null,
        Secret = $"secret-{id}"
    };

    public static TestDbContext CreateInMemory(int rowCount = RowCount)
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        var db = new TestDbContext(options);

        // Ids are inserted in a shuffled order so a walk cannot pass by accident of insertion order.
        var ids = Enumerable.Range(1, rowCount).OrderBy(i => (i * 7919) % RowCount).ToList();
        foreach (var id in ids)
            db.Products.Add(Create(id));

        db.SaveChanges();
        db.ChangeTracker.Clear();
        return db;
    }

    public static TestDbContext CreateNpgsql()
    {
        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseNpgsql("Host=localhost;Database=x")
            .Options;

        return new TestDbContext(options);
    }

    /// <summary>Reference ordering of the seed rows (LINQ to objects), as ids.</summary>
    public static List<int> Expected(Func<IEnumerable<Product>, IEnumerable<Product>> order) =>
        order(All).Select(x => x.Id).ToList();

    /// <summary>Base64url JSON payload of a cursor (<c>[fingerprint, v1..vn]</c>).</summary>
    public static JsonArray DecodeCursor(string cursor) =>
        JsonNode.Parse(Base64Url.DecodeFromChars(cursor))!.AsArray();

    public static string EncodeCursor(JsonNode payload) =>
        Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(payload));
}

/// <summary>Named orderings shared by walk tests: sortBy + isDescending + LINQ-to-objects reference.</summary>
public sealed record SortCase(string Name, string? SortBy, bool IsDescending, Func<IEnumerable<Product>, IEnumerable<Product>> Reference)
{
    public static readonly IReadOnlyDictionary<string, SortCase> All = new[]
    {
        new SortCase("rank asc", "rank", false, s => s.OrderBy(x => x.Rank).ThenBy(x => x.Id)),
        new SortCase("rank desc", "Rank desc", false, s => s.OrderByDescending(x => x.Rank).ThenByDescending(x => x.Id)),
        new SortCase("rank (IsDescending)", "rank", true, s => s.OrderByDescending(x => x.Rank).ThenByDescending(x => x.Id)),
        new SortCase("rank desc, name asc", "rank desc, name asc", false,
            s => s.OrderByDescending(x => x.Rank).ThenBy(x => x.Name).ThenBy(x => x.Id)),
        new SortCase("name asc, rank desc", "name, rank desc", false,
            s => s.OrderBy(x => x.Name).ThenByDescending(x => x.Rank).ThenByDescending(x => x.Id)),
        new SortCase("five keys", "status, rank desc, createdAt, price desc, code", false,
            s => s.OrderBy(x => x.Status).ThenByDescending(x => x.Rank).ThenBy(x => x.CreatedAt)
                  .ThenByDescending(x => x.Price).ThenBy(x => x.Code).ThenBy(x => x.Id)),
        new SortCase("default", null, false, s => s.OrderBy(x => x.Id)),
        new SortCase("default desc", null, true, s => s.OrderByDescending(x => x.Id)),
        new SortCase("invalid sortBy -> PK", "Secret", false, s => s.OrderBy(x => x.Id)),
    }.ToDictionary(x => x.Name);

    public static TheoryData<string> Names()
    {
        var data = new TheoryData<string>();
        foreach (var name in All.Keys)
            data.Add(name);
        return data;
    }

    public ProductQuery Query(int pageSize, int page = 1) => new()
    {
        SortBy = SortBy,
        IsDescending = IsDescending,
        PageSize = pageSize,
        Page = page
    };

    public List<int> ExpectedIds() => TestData.Expected(Reference);

    public override string ToString() => Name;
}
