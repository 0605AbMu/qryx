# Qryx (`/krɪks/`)

**Parser-free, DTO-Centric, Strongly-Typed Dynamic Query Engine for .NET 10 / C# 14**

Qryx REST API so'rov parametrlarini to'g'ridan-to'g'ri xavfsiz, yuqori unumdorlikka ega va indekslarga mos Entity Framework Core `IQueryable` Expression Tree'lariga o'girib beruvchi zamonaviy dinamik so'rov dvigatelidir.

---

## 📦 Loyiha Arxitekturasi

Loyiha toza arxitekturaga asoslangan 6 ta NuGet paketidan va namuna loyihasidan iborat:

```text
src/
├── Qryx.Core/              # Interfeyslar, [Filter] atributi, Op enum, QryxList<T> (0 dependencies)
├── Qryx.Expressions/       # Expression Tree Builder, Compiled Delegate Cache, Reflection Inspector
├── Qryx.EntityFramework/   # EF Core IQueryable extension'lar, ToPagedResultAsync, PagedResult<T>
├── Qryx.AspNetCore/        # ModelBinder, Minimal API / Controller integratsiyasi, EndpointFilter
├── Qryx.OpenApi/           # Modern .NET 10 Microsoft.AspNetCore.OpenApi transformerlari
└── Qryx.Swashbuckle/       # Classic Swashbuckle (SwaggerGen) IOperationFilter integratsiyasi
benchmarks/
└── Qryx.Benchmarks/        # BenchmarkDotNet & High-speed diagnostic suite (Gridify vs Sieve vs OData vs Qryx)
samples/
└── Qryx.SampleApi/         # Minimal API + EF Core InMemory + Classic Swashbuckle namunasi
```

---

## 🚀 Asosiy Xususiyatlar

1. **Parser-free & Type-safe**: Satrli so'rovlar (OData/GraphQL) parserlarisiz, to'liq C# kuchli tiplashtirilgan DTO shartnomalari.
2. **Eng toza URL (Prefiks yondashuvi)**: `minAge=20&maxAge=35`, `fromDate=...&toDate=...` konvensiyasi bo'yicha oraliq filtrlar avtomatik moslashtiriladi.
3. **Multi-Target & Indeks Do'stligi**: Hisoblangan yoki birlashgan maydonlar (masalan, `FullName`) bir nechta ustunga (`FirstName`, `LastName`) `Expression.OrElse` orqali tarjima qilinadi va B-Tree indekslarini buzmaydi.
4. **Minimal Heap Allocation**: Reflection faqat birinchi so'rovda ishlaydi, qolgan so'rovlar kompilyatsiya qilingan delegatlar va keshdagi metadata orqali bajariladi.
5. **DoS Guard**: `PageSize` maksimal 100 bilan cheklangan (`Math.Min(PageSize, 100)`).
6. **SQL Injection Free**: Barcha parametrlar EF Core parametrlariga aylanadi (`@p0`, `@p1`).
7. **Keyset / Cursor Pagination**: Katta hajmdagi ma'lumotlar uchun `AfterCursor` orqali tezkor sahifalash.
8. **Ixcham va qulay JSON envelope**: API javobi faqat `items`, `total` va `hasNext` maydonlarini qaytaradi.
9. **Classic Swashbuckle & Modern OpenAPI**: Classic Swagger UI (`Swashbuckle.AspNetCore`) va .NET 10 ning yangi OpenAPI kutubxonalari bilan to'liq integratsiya.

---

## 💻 Tez Boshlash (Quick Start)

### 1. DTO E'lon Qilish

```csharp
using Qryx.Core.Attributes;
using Qryx.Core.Contracts;
using Qryx.Core.Enums;

public record UserQueryDto : QryxRequest<User>
{
    // 1. Prefiks yondashuvi (Eng toza URL: ?minAge=20&maxAge=35)
    // Avtomatik ravishda User.Age ustuniga moslashadi
    [Filter(nameof(User.Age), Op.GreaterThanOrEqual)]
    public int? MinAge { get; init; }

    [Filter(nameof(User.Age), Op.LessThanOrEqual)]
    public int? MaxAge { get; init; }

    // 2. Enum yoki massiv filtri (?status=Active&status=Pending)
    [Filter(Op.In)]
    public UserStatus[]? Status { get; init; }

    // 3. Hisoblangan maydon (FullName): Qaysi Entity ustunlariga tushishi ko'rsatiladi
    // Bu yerda DB indeksi buzilmasligi uchun ikkala ustunga OR bilan ulanadi
    [Filter(Op.StartsWith, TargetProperties = [nameof(User.FirstName), nameof(User.LastName)])]
    public string? FullName { get; init; }

    // 4. Navigation property (?departmentName=Sales)
    [Filter(Op.Equals, TargetProperty = "Department.Name")]
    public string? DepartmentName { get; init; }
}
```

### 2. Minimal API Integratsiyasi

```csharp
using Microsoft.EntityFrameworkCore;
using Qryx.AspNetCore.Extensions;
using Qryx.EntityFramework.Extensions;
using Qryx.Swashbuckle.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(opt => opt.UseSqlServer(...));
builder.Services.AddQryx();

// Classic Swashbuckle integratsiyasi
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.AddQryxFilters();
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.MapGet("/api/users", async (
    [AsParameters] UserQueryDto query,
    AppDbContext db,
    CancellationToken ct) =>
{
    var result = await db.Users
        .AsNoTracking()
        .ToPagedResultAsync(query, ct);

    return Results.Ok(result);
}).WithQryx();

app.Run();
```

### 3. HTTP So'rov va Javob Namunasi

**So'rov:**
```http
GET /api/users?fullName=Ali&minAge=20&maxAge=35&status=Active&departmentName=Sales&sortBy=CreatedAt&isDescending=true&page=1&pageSize=10
```

**Javob:**
```json
{
  "items": [
    {
      "id": 1,
      "firstName": "Alisher",
      "lastName": "Navoiy",
      "age": 25,
      "status": "Active"
    }
  ],
  "total": 1,
  "hasNext": false
}
```

---

## 🛠 Loyihani Qirish va Ishga Tushirish

```bash
# Barcha loyihalarni kompilyatsiya qilish
dotnet build Qryx.slnx

# Sample API'ni ishga tushirish
dotnet run --project samples/Qryx.SampleApi/Qryx.SampleApi.csproj
```

Swagger UI brauzerda: `http://localhost:5000/swagger`
API Endpoint: `http://localhost:5000/api/users`

---

## ⚡ Performance Benchmark (Qryx vs Gridify vs Sieve vs OData)

To'liq benchmark loyihasi [Qryx.Benchmarks](file:///Users/0605abmu/Desktop/work/qryx/benchmarks/Qryx.Benchmarks) manzilida joylashgan bo'lib, har xil sharoitlardagi sinovlarni o'z ichiga oladi:

| Ko'rsatkich / Sharoit | Qryx | Gridify | Sieve | OData |
| :--- | :--- | :--- | :--- | :--- |
| **So'rov tuzish (Construction)** | **8.57 μs** (1.00x) | 30.74 μs (3.59x sekin) | 28.50 μs (3.33x sekin) | 45.30 μs (5.29x sekin) |
| **Xotira sarfi (Allocated Heap)** | **4.9 KB** | 25.5 KB (5.2x ko'p) | 23.7 KB (4.8x ko'p) | 43.8 KB (8.9x ko'p) |
| **Real PostgreSQL DB (10k items)** | **653 μs (26.4 KB)** | 602 μs (46.8 KB) | 691 μs (51.4 KB) | 644 μs (69.5 KB) |
| **Parallel yuklama (Concurrency)** | **1,005,000+ ops/s** | ~185,000 ops/s | ~88,000 ops/s | ~101,000 ops/s |

Benchmarklarni ishga tushirish:
```bash
# Barcha testlarni (Construction, InMemory, PostgreSQL, Concurrency) ishga tushirish:
dotnet run -c Release --project benchmarks/Qryx.Benchmarks/Qryx.Benchmarks.csproj -- --fast

# Faqat Real PostgreSQL Docker bazasini testlash:
dotnet run -c Release --project benchmarks/Qryx.Benchmarks/Qryx.Benchmarks.csproj -- --postgres

# Rasmiy BenchmarkDotNet testlari:
dotnet run -c Release --project benchmarks/Qryx.Benchmarks/Qryx.Benchmarks.csproj -- --bdn
```
