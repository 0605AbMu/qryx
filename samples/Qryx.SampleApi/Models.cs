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
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string FullName => $"{FirstName} {LastName}";
    public int Age { get; set; }
    public DateTime CreatedAt { get; set; }
    public UserStatus Status { get; set; }
    public Guid DepartmentId { get; set; }
    public Department Department { get; set; } = null!;
}

public record UserQueryDto : QryxRequest<User>
{
    // 1. Prefiks yondashuvi (Eng toza URL: ?minAge=20&maxAge=35)
    [Filter(nameof(User.Age), Op.GreaterThanOrEqual)]
    public int? MinAge { get; init; }

    [Filter(nameof(User.Age), Op.LessThanOrEqual)]
    public int? MaxAge { get; init; }

    [Filter(Op.In)]
    public UserStatus[]? Status { get; init; }

    // 2. Hisoblangan maydon (FullName): B-Tree indeksini saqlash uchun OR bilan ulanadi
    [Filter(Op.StartsWith, TargetProperties = [nameof(User.FirstName), nameof(User.LastName)])]
    public string? FullName { get; init; }

    // 3. Navigation property
    [Filter(Op.Equals, TargetProperty = "Department.Name")]
    public string? DepartmentName { get; init; }
}

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Company> Companies => Set<Company>();
}
