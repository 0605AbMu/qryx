using Microsoft.EntityFrameworkCore;
using Qryx.Core.Attributes;
using Qryx.Core.Contracts;
using Qryx.Core.Enums;
using Qryx.Core.Types;

namespace Qryx.AspNetCore.Tests.Infrastructure;

public enum ItemStatus
{
    Active = 0,
    Pending = 1,
    Archived = 2
}

public sealed class Item
{
    public int Id { get; set; }

    [Sortable]
    public string Name { get; set; } = string.Empty;

    [Sortable]
    public int Age { get; set; }

    public ItemStatus Status { get; set; }

    public DateOnly CreatedOn { get; set; }

    public string Secret { get; set; } = string.Empty;
}

public record ItemQuery : QryxRequest<Item>
{
    /// <summary>String filter against an enum column: converted (and validated) by the query engine.</summary>
    [Filter(Op.Equals, nameof(Item.Status))]
    public string? StatusName { get; init; }

    /// <summary>Typed enum filter: parsed by the HTTP binding layer.</summary>
    [Filter(Op.Equals)]
    public ItemStatus? Status { get; init; }

    [Filter(nameof(Item.Age), Op.Between)]
    public Range<int>? Age { get; init; }

    [Filter(nameof(Item.Id), Op.In)]
    public int[]? Ids { get; init; }

    /// <summary>Enum array: repeated keys only (?statuses=A&amp;statuses=B).</summary>
    [Filter(nameof(Item.Status), Op.In)]
    public ItemStatus[]? Statuses { get; init; }

    /// <summary>QryxList: comma-separated (?statusList=A,B) and repeated keys.</summary>
    [Filter(nameof(Item.Status), Op.In)]
    public QryxList<ItemStatus>? StatusList { get; init; }

    [Filter(Op.Equals)]
    public DateOnly? CreatedOn { get; init; }

    [Filter(nameof(Item.CreatedOn), Op.Between)]
    public Range<DateOnly>? CreatedBetween { get; init; }
}

public sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
{
    public DbSet<Item> Items => Set<Item>();

    public static IEnumerable<Item> SeedItems() =>
        Enumerable.Range(1, 12).Select(i => new Item
        {
            Id = i,
            Name = $"Item {i:D2}",
            Age = 18 + i,
            Status = (ItemStatus)(i % 3),
            CreatedOn = new DateOnly(2024, 1, i),
            Secret = $"secret-{i}"
        });
}
