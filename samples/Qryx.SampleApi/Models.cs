using Microsoft.EntityFrameworkCore;
using Qryx.Core.Attributes;
using Qryx.Core.Contracts;
using Qryx.Core.Enums;

namespace Qryx.SampleApi.Models;

public enum UserStatus
{
    Active,
    Pending,
    Inactive,
    Suspended
}

public class Company
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
}

public class Department
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public Company Company { get; set; } = null!;
}

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Sortable]
    public string FirstName { get; set; } = string.Empty;

    [Sortable]
    public string LastName { get; set; } = string.Empty;

    public string FullName => $"{FirstName} {LastName}";

    [Sortable]
    public int Age { get; set; }

    [Sortable]
    public DateTime CreatedAt { get; set; }

    [Sortable]
    public UserStatus Status { get; set; }

    public Guid DepartmentId { get; set; }
    public Department Department { get; set; } = null!;
}

public record UserQueryDto : QryxRequest<User>
{
    // 1. Prefiks yondashuvi (Eng toza URL: ?minAge=20&maxAge=35)
    [Sortable]
    [Filter(nameof(User.Age), Op.GreaterThanOrEqual)]
    public int? MinAge { get; init; }

    [Sortable]
    [Filter(nameof(User.Age), Op.LessThanOrEqual)]
    public int? MaxAge { get; init; }

    [Sortable]
    [Filter(Op.In)]
    public UserStatus[]? Status { get; init; }

    // 2. Hisoblangan maydon (FullName): B-Tree indeksini saqlash uchun OR bilan ulanadi
    // Standart Invariant = false (direct LIKE, lower/upper chaqirilmaydi)
    [Sortable]
    [Filter(Op.StartsWith, TargetProperties = [nameof(User.FirstName), nameof(User.LastName)])]
    public string? FullName { get; init; }

    // Case-insensitive qidiruv: PostgreSQL da native ILIKE, boshqa DB larda UPPER()
    [Filter(Op.StartsWith, Invariant = true)]
    public string? FirstName { get; init; }

    // 3. Bog'langan jadval (Navigation property)
    [Filter(Op.Equals, TargetProperty = "Department.Name")]
    public string? DepartmentName { get; init; }
}

public record FastUserQueryDto : QryxRequest<User>
{
    public FastUserQueryDto()
    {
        IncludeTotalCount = false; // Single-query: COUNT(*) o'chiriladi, 3x tezlashadi
    }

    [Sortable]
    [Filter(Op.Equals)]
    public UserStatus? Status { get; init; }
}

public record UserSummaryDto(Guid Id, string FirstName, string LastName, int Age, UserStatus Status, string Department);

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Company> Companies => Set<Company>();
}
