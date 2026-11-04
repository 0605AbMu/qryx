using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Qryx.AspNetCore.Extensions;
using Qryx.EntityFramework.Extensions;
using Qryx.Generated;
using Qryx.SampleApi.Models;
using Qryx.Swashbuckle.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Register DbContext with In-Memory database
builder.Services.AddDbContext<AppDbContext>(opt => opt.UseInMemoryDatabase("QryxSampleDb"));

// Register Qryx Core & AspNetCore services
builder.Services.AddQryx();

// Register Classic Swashbuckle with Qryx filters
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.AddQryxFilters();
});

var app = builder.Build();

// Enable Classic Swagger and SwaggerUI
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "Qryx Sample API v1");
});

// Seed mock data
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (!db.Users.Any())
    {
        var company = new Company { Name = "TechCorp Global" };
        var deptIt = new Department { Name = "IT Infrastructure", Company = company };
        var deptSales = new Department { Name = "Sales & Growth", Company = company };

        db.Companies.Add(company);
        db.Departments.AddRange(deptIt, deptSales);

        db.Users.AddRange(
            new User
            {
                FirstName = "Ali",
                LastName = "Valiyev",
                Age = 25,
                CreatedAt = DateTime.UtcNow.AddMonths(-3),
                Status = UserStatus.Active,
                Department = deptIt
            },
            new User
            {
                FirstName = "Alisher",
                LastName = "Navoiy",
                Age = 32,
                CreatedAt = DateTime.UtcNow.AddMonths(-1),
                Status = UserStatus.Pending,
                Department = deptIt
            },
            new User
            {
                FirstName = "Madina",
                LastName = "Karimova",
                Age = 29,
                CreatedAt = DateTime.UtcNow.AddMonths(-6),
                Status = UserStatus.Active,
                Department = deptSales
            },
            new User
            {
                FirstName = "Bobur",
                LastName = "Mirzo",
                Age = 40,
                CreatedAt = DateTime.UtcNow.AddYears(-1),
                Status = UserStatus.Inactive,
                Department = deptSales
            }
        );

        db.SaveChanges();
    }
}

// =========================================================================
// 1. Standart Dynamic API (Offset + Count + Multi-Sort)
// Example: GET /api/users?status=Active&minAge=20&sortBy=createdAt desc,age asc
// =========================================================================
app.MapGet("/api/users", async (
    [AsParameters] UserQueryDto query,
    AppDbContext db,
    CancellationToken ct) =>
{
    var result = await db.Users
        .AsNoTracking()
        .Include(u => u.Department)
            .ThenInclude(d => d.Company)
        .ToPagedResultAsync(query, ct);

    return Results.Ok(result);
}).WithQryx();

// =========================================================================
// 2. Keyset / Cursor Pagination (O(log N) Index Seek)
// Example: GET /api/users/cursor?sortBy=age desc&pageSize=20
//          -> javobdagi "nextCursor" ni o'zgartirmasdan keyingi so'rovga bering: ?sortBy=age desc&afterCursor=<nextCursor>
// =========================================================================
app.MapGet("/api/users/cursor", async (
    [AsParameters] UserQueryDto query,
    AppDbContext db,
    CancellationToken ct) =>
{
    var result = await db.Users
        .AsNoTracking()
        .ToPagedResultAsync(query, ct);

    return Results.Ok(result);
}).WithQryx();

// =========================================================================
// 3. Katta jadval: [NoTotal] DTO (includeTotal=true e'tiborsiz qoldiriladi, COUNT(*) hech qachon bajarilmaydi)
// Example: GET /api/users/fast?status=Active&pageSize=20&includeTotal=true  -> javobda "total" bo'lmaydi
// =========================================================================
app.MapGet("/api/users/fast", async (
    [AsParameters] FastUserQueryDto query,
    AppDbContext db,
    CancellationToken ct) =>
{
    var result = await db.Users
        .AsNoTracking()
        .ToPagedResultAsync(query, ct);

    return Results.Ok(result);
}).WithQryx();

// =========================================================================
// 4. Projection Pushdown (Faqat kerakli ustunlarni SQL darajasida tanlash)
// Example: GET /api/users/projection?status=Active
// =========================================================================
app.MapGet("/api/users/projection", async (
    [AsParameters] UserQueryDto query,
    AppDbContext db,
    CancellationToken ct) =>
{
    var result = await db.Users
        .AsNoTracking()
        .ToPagedResultAsync(
            request: query,
            selector: u => new UserSummaryDto(u.Id, u.FirstName, u.LastName, u.Age, u.Status, u.Department.Name),
            ct: ct
        );

    return Results.Ok(result);
}).WithQryx();

// =========================================================================
// 5. Zero-Copy Streaming (IAsyncEnumerable, RAM'da buffer qilmasdan HTTP stream)
// Example: GET /api/users/stream?status=Active
// =========================================================================
app.MapGet("/api/users/stream", (
    [AsParameters] UserQueryDto query,
    AppDbContext db) =>
{
    return Results.Ok(db.Users.AsNoTracking().ToPagedAsyncEnumerable(query));
}).WithQryx();

// =========================================================================
// 6. Roslyn Source Generator (Kompilyatsiya vaqtida generatsiya qilingan LINQ)
// Example: GET /api/users/generated?minAge=20&maxAge=40
// =========================================================================
app.MapGet("/api/users/generated", async (
    [AsParameters] UserQueryDto query,
    AppDbContext db,
    CancellationToken ct) =>
{
    var users = await db.Users
        .AsNoTracking()
        .ApplyGenerated(query) // compile-time generated plan; DTO mistakes are reported as QRYX001/QRYX002
        .ToListAsync(ct);

    return Results.Ok(users);
}).WithQryx();

app.Run();
