using Microsoft.EntityFrameworkCore;
using Qryx.Core.Attributes;
using Qryx.Core.Contracts;
using Qryx.Core.Enums;
using Qryx.Core.Types;

namespace Qryx.ApiDocs.Tests.Infrastructure;

public enum PersonStatus
{
    Active = 0,
    Pending = 1,
    Blocked = 2
}

public sealed class Person
{
    public int Id { get; set; }

    public string FirstName { get; set; } = string.Empty;

    public string LastName { get; set; } = string.Empty;

    [Sortable]
    public int Age { get; set; }

    public decimal Salary { get; set; }

    public PersonStatus Status { get; set; }

    public DateOnly CreatedOn { get; set; }

    [Sortable]
    public DateTime CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid Code { get; set; }

    public string? Email { get; set; }
}

/// <summary>Covers every value type Qryx documents specially.</summary>
public record PersonQuery : QryxRequest<Person>
{
    [Filter(nameof(Person.Age), Op.GreaterThanOrEqual)]
    public int? MinAge { get; init; }

    [Filter(nameof(Person.FirstName), Op.StartsWith, Invariant = true)]
    public string? FirstNameStarts { get; init; }

    [Filter(Op.StartsWith, TargetProperties = [nameof(Person.FirstName), nameof(Person.LastName)])]
    public string? FullName { get; init; }

    [Sortable]
    [Filter(Op.Equals)]
    public string? LastName { get; init; }

    [Filter(nameof(Person.Status), Op.Equals)]
    public string? StatusName { get; init; }

    [Filter(nameof(Person.Age), Op.Between)]
    public Range<int>? AgeRange { get; init; }

    [Filter(nameof(Person.Salary), Op.Between)]
    public Range<decimal>? SalaryRange { get; init; }

    [Filter(nameof(Person.CreatedOn), Op.Between)]
    public Range<DateOnly>? CreatedRange { get; init; }

    [Filter(nameof(Person.Status), Op.In)]
    public QryxList<PersonStatus>? StatusList { get; init; }

    [Filter(nameof(Person.Id), Op.In)]
    public int[]? Ids { get; init; }

    [Filter(nameof(Person.Id), Op.NotIn)]
    public long[]? ExcludedIds { get; init; }

    [Filter(Op.Equals)]
    public PersonStatus? Status { get; init; }

    [Filter(nameof(Person.Status), Op.In)]
    public PersonStatus[]? Statuses { get; init; }

    [Filter(Op.Equals)]
    public DateOnly? CreatedOn { get; init; }

    [Filter(Op.Equals)]
    public Guid? Code { get; init; }

    [Filter(Op.GreaterThanOrEqual)]
    public DateTime? FromCreatedAt { get; init; }

    [Filter(Op.LessThanOrEqual)]
    public DateTimeOffset? ToUpdatedAt { get; init; }

    [Filter(nameof(Person.Email), Op.IsNull)]
    public bool? EmailMissing { get; init; }
}

/// <summary>Query DTO for which the total count is disabled.</summary>
[NoTotal]
public record PersonFastQuery : QryxRequest<Person>
{
    [Filter(nameof(Person.Age), Op.Between)]
    public Range<int>? AgeRange { get; init; }
}

/// <summary>
/// MVC-only DTO: <c>List&lt;T&gt;</c> binds from repeated keys in MVC, while Minimal APIs reject it at startup
/// ("Body was inferred"), so it is not used with <c>[AsParameters]</c>.
/// </summary>
public record PersonListQuery : QryxRequest<Person>
{
    [Filter(nameof(Person.Id), Op.In)]
    public List<int>? IdList { get; init; }

    [Filter(nameof(Person.Status), Op.NotIn)]
    public List<PersonStatus>? ExcludedStatuses { get; init; }
}

public enum TicketPriority
{
    Low,
    High
}

/// <summary>Entity that never appears in a response body: its enum is referenced only by a query parameter.</summary>
public sealed class Ticket
{
    public int Id { get; set; }

    public TicketPriority Priority { get; set; }
}

public record TicketQuery : QryxRequest<Ticket>
{
    [Filter(Op.Equals)]
    public TicketPriority? Priority { get; init; }
}

public sealed class PeopleDbContext(DbContextOptions<PeopleDbContext> options) : DbContext(options)
{
    public DbSet<Person> People => Set<Person>();

    public static IEnumerable<Person> Seed() =>
        Enumerable.Range(1, 10).Select(i => new Person
        {
            Id = i,
            FirstName = $"First{i}",
            LastName = $"Last{i}",
            Age = 18 + i * 2,
            Salary = 1000m * i,
            Status = (PersonStatus)(i % 3),
            CreatedOn = new DateOnly(2024, 1, i),
            CreatedAt = new DateTime(2024, 1, i, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTimeOffset(2024, 2, i, 0, 0, 0, TimeSpan.Zero),
            Code = Guid.NewGuid(),
            Email = i % 2 == 0 ? $"p{i}@example.com" : null
        });
}
