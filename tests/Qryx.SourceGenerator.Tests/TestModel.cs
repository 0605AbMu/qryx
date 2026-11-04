using Microsoft.EntityFrameworkCore;
using Qryx.Core.Attributes;
using Qryx.Core.Contracts;
using Qryx.Core.Enums;
using Qryx.Core.Types;

namespace Qryx.SourceGenerator.Tests;

public enum PersonStatus
{
    Active,
    Pending,
    Blocked
}

public class Company
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class Department
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int CompanyId { get; set; }
    public Company Company { get; set; } = null!;
}

public class Person
{
    public int Id { get; set; }

    [Sortable]
    public string FirstName { get; set; } = string.Empty;

    [Sortable]
    public string LastName { get; set; } = string.Empty;

    public string? Email { get; set; }

    [Sortable]
    public int Age { get; set; }

    public int? Score { get; set; }
    public long Balance { get; set; }
    public decimal Salary { get; set; }

    [Sortable]
    public DateTime CreatedAt { get; set; }

    public DateTime? DeletedAt { get; set; }
    public PersonStatus Status { get; set; }
    public Guid ExternalId { get; set; }
    public bool IsActive { get; set; }
    public string Notes { get; set; } = string.Empty;
    public int DepartmentId { get; set; }
    public Department Department { get; set; } = null!;
}

/// <summary>One DTO exercising every operator, value shape and target style supported by [Filter].</summary>
public record PersonQuery : QryxRequest<Person>
{
    // Comparisons
    [Filter(Op.Equals)]
    public PersonStatus? Status { get; init; }

    [Filter(nameof(Person.Status), Op.NotEquals)]
    public PersonStatus? ExcludeStatus { get; init; }

    [Filter(nameof(Person.Age), Op.GreaterThan)]
    public int? AgeAbove { get; init; }

    [Filter(Op.GreaterThanOrEqual)]
    public int? MinAge { get; init; }

    [Filter(nameof(Person.Age), Op.LessThan)]
    public int? AgeBelow { get; init; }

    [Filter(Op.LessThanOrEqual)]
    public int? MaxAge { get; init; }

    [Filter(Op.GreaterThanOrEqual)]
    public DateTime? FromCreatedAt { get; init; }

    [Filter(Op.LessThanOrEqual)]
    public DateTime? ToCreatedAt { get; init; }

    /// <summary>int value against a long column (numeric widening of the value, never of the column).</summary>
    [Filter(nameof(Person.Balance), Op.GreaterThanOrEqual)]
    public int? BalanceAtLeast { get; init; }

    /// <summary>int value against an int? column (lifted comparison).</summary>
    [Filter(nameof(Person.Score), Op.Equals)]
    public int? ScoreEquals { get; init; }

    [Filter(nameof(Person.Salary), Op.GreaterThanOrEqual)]
    public decimal? SalaryAtLeast { get; init; }

    [Filter(nameof(Person.ExternalId), Op.Equals)]
    public Guid? ExternalId { get; init; }

    [Filter(nameof(Person.IsActive), Op.Equals)]
    public bool? Active { get; init; }

    // Navigation paths (one of them case-insensitive)
    [Filter(Op.Equals, TargetProperty = "Department.Name")]
    public string? DepartmentName { get; init; }

    [Filter(Op.Equals, TargetProperty = "department.company.name")]
    public string? CompanyName { get; init; }

    // String operators
    [Filter(Op.StartsWith, TargetProperties = [nameof(Person.FirstName), nameof(Person.LastName)])]
    public string? FullName { get; init; }

    [Filter(nameof(Person.FirstName), Op.StartsWith, Invariant = true)]
    public string? FirstNameCi { get; init; }

    [Filter(nameof(Person.LastName), Op.EndsWith)]
    public string? LastNameEnds { get; init; }

    [Filter(nameof(Person.LastName), Op.EndsWith, Invariant = true)]
    public string? LastNameEndsCi { get; init; }

    [Filter(nameof(Person.Notes), Op.Contains)]
    public string? NotesContains { get; init; }

    [Filter(Op.Contains, TargetProperties = [nameof(Person.FirstName), nameof(Person.LastName)], Invariant = true)]
    public string? Search { get; init; }

    // Collections
    [Filter(nameof(Person.Status), Op.In)]
    public PersonStatus[]? Statuses { get; init; }

    [Filter(nameof(Person.Age), Op.In)]
    public List<int>? Ages { get; init; }

    [Filter(nameof(Person.Id), Op.In)]
    public QryxList<int>? Ids { get; init; }

    [Filter(nameof(Person.Status), Op.NotIn)]
    public IEnumerable<PersonStatus>? ExcludeStatuses { get; init; }

    // Ranges
    [Filter(nameof(Person.Age), Op.Between)]
    public Range<int>? AgeRange { get; init; }

    [Filter(nameof(Person.CreatedAt), Op.Between)]
    public Range<DateTime> CreatedRange { get; init; }

    [Filter(nameof(Person.Balance), Op.Between)]
    public Range<int>? BalanceRange { get; init; }

    // Null checks
    [Filter(nameof(Person.DeletedAt), Op.IsNull)]
    public bool? NotDeleted { get; init; }

    [Filter(nameof(Person.Email), Op.IsNotNull)]
    public bool? HasEmail { get; init; }

    [Filter(nameof(Person.DeletedAt), Op.IsNotNull)]
    public string? DeletedMarker { get; init; }

    // Delegated to the runtime engine
    [Filter(nameof(Person.Status), Op.Equals)]
    public string? StatusText { get; init; }

    [Filter(nameof(Person.Notes), Op.Fts)]
    public string? FullText { get; init; }

    [Filter(nameof(Person.Notes), Op.JsonContains)]
    public string? NotesJson { get; init; }
}

/// <summary>A request type without own filters: only sorting and pagination are generated.</summary>
public record PlainPersonQuery : QryxRequest<Person>;

/// <summary>Derived DTO without own filters inherits the generated filters of its base.</summary>
public record DerivedPersonQuery : PersonQuery;

public class PeopleContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<Person> People => Set<Person>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Company> Companies => Set<Company>();
}

public sealed class NpgsqlPeopleContext() : PeopleContext(
    new DbContextOptionsBuilder<PeopleContext>().UseNpgsql("Host=localhost;Database=x").Options);

public static class TestData
{
    public const int PeopleCount = 40;

    private static readonly string[] FirstNames = ["Alice", "alina", "Bob", "bobby", "Carl", "Dana", "Eve", "Frank"];
    private static readonly string[] LastNames = ["Smith", "smithson", "Jones", "O_Hara", "100%Real", "Lee", "White", "Brown", "back\\slash", "SMITH"];

    public static PeopleContext CreateInMemory()
    {
        var options = new DbContextOptionsBuilder<PeopleContext>()
            .UseInMemoryDatabase("qryx-generator-" + Guid.NewGuid())
            .Options;

        var context = new PeopleContext(options);
        Seed(context);
        return context;
    }

    private static void Seed(PeopleContext context)
    {
        var acme = new Company { Id = 1, Name = "Acme" };
        var globex = new Company { Id = 2, Name = "Globex" };
        var departments = new[]
        {
            new Department { Id = 1, Name = "Sales", Company = acme },
            new Department { Id = 2, Name = "sales-eu", Company = acme },
            new Department { Id = 3, Name = "Engineering", Company = globex },
            new Department { Id = 4, Name = "HR", Company = globex }
        };

        context.Companies.AddRange(acme, globex);
        context.Departments.AddRange(departments);

        var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 1; i <= PeopleCount; i++)
        {
            var createdAt = start.AddDays(i);
            context.People.Add(new Person
            {
                Id = i,
                FirstName = FirstNames[i % FirstNames.Length],
                LastName = LastNames[i % LastNames.Length],
                Email = i % 3 == 0 ? null : $"p{i}@example.com",
                Age = 18 + (i * 7 % 50),
                Score = i % 4 == 0 ? null : i * 3 % 100,
                Balance = i * 1000L,
                Salary = i * 250.5m,
                CreatedAt = createdAt,
                DeletedAt = i % 5 == 0 ? createdAt.AddDays(10) : null,
                Status = (PersonStatus)(i % 3),
                ExternalId = new Guid(i, 0, 0, new byte[8]),
                IsActive = i % 2 == 0,
                Notes = i % 4 == 0 ? $"note {i} urgent" : $"note {i} low",
                Department = departments[i % departments.Length]
            });
        }

        context.SaveChanges();
    }
}
