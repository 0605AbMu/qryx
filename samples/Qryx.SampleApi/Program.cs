using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Qryx.AspNetCore.Extensions;
using Qryx.EntityFramework.Extensions;
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

// Minimal API Endpoint
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

app.Run();
