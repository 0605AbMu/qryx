# Qryx (`/krɪks/`)

**Parser-free, DTO-Centric, Strongly-Typed Dynamic Query Engine for .NET 10 / C# 14**

Qryx REST API so'rov parametrlarini to'g'ridan-to'g'ri xavfsiz, yuqori unumdorlikka ega va indekslarga mos Entity Framework Core `IQueryable` Expression Tree'lariga o'girib beruvchi zamonaviy dinamik so'rov dvigatelidir.

---

## 📦 Loyiha Arxitekturasi

Loyiha toza arxitekturaga asoslangan 7 ta NuGet paketidan va namuna loyihasidan iborat:

```text
src/
├── Qryx.Core/              # Interfeyslar, [Filter] atributi, Op enum, QryxList<T> (0 dependencies)
├── Qryx.Expressions/       # Expression Tree Builder, Compiled Delegate Cache, Reflection Inspector
├── Qryx.EntityFramework/   # EF Core IQueryable extension'lar, ToPagedResultAsync, ToPagedAsyncEnumerable, PagedResult<T>
├── Qryx.AspNetCore/        # ModelBinder, Minimal API / Controller integratsiyasi, Fast Compiled Setters
├── Qryx.SourceGenerator/   # Roslyn Incremental Source Generator (Compile-time code generation)
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
2. **100% Database-Provider Independence**: Hech qanday ma'lumotlar bazasiga qattiq bog'lanmagan. PostgreSQL, SQL Server, SQLite, MySQL, MariaDB, Oracle va EF Core InMemory bilan bir xil kafolatlangan unumdorlikda ishlaydi.
3. **Eng toza URL (Prefiks yondashuvi)**: `minAge=20&maxAge=35`, `fromDate=...&toDate=...` konvensiyasi bo'yicha oraliq filtrlar avtomatik moslashtiriladi.
4. **Multi-Target & Indeks Do'stligi**: Hisoblangan yoki birlashgan maydonlar (masalan, `FullName`) bir nechta ustunga (`FirstName`, `LastName`) `Expression.OrElse` orqali tarjima qilinadi va B-Tree indekslarini buzmaydi.
5. **Minimal Heap Allocation**: Reflection faqat birinchi so'rovda ishlaydi, qolgan so'rovlar kompilyatsiya qilingan delegatlar va keshdagi metadata orqali bajariladi.
6. **DoS Guard**: `PageSize` maksimal 100 bilan cheklangan (`Math.Min(PageSize, 100)`).
7. **SQL Injection Free**: Barcha parametrlar EF Core parametrlariga aylanadi (`@p0`, `@p1`).
8. **Keyset / Cursor Pagination & NextCursor**: Katta hajmdagi ma'lumotlar uchun `AfterCursor` orqali tezkor $O(\log N)$ sahifalash.
9. **Ixcham va qulay JSON envelope**: API javobi faqat `items`, `total` va `hasNext` maydonlarini qaytaradi.
10. **Classic Swashbuckle & Modern OpenAPI**: Classic Swagger UI (`Swashbuckle.AspNetCore`) va .NET 10 ning yangi OpenAPI kutubxonalari bilan to'liq integratsiya.
11. **Database-Aware Invariant String Matching**: `StartsWith`, `EndsWith`, `Contains` biznes operatorlari uchun `Invariant = true` (standart) bo'lganda, PostgreSQL provayderida avtomatik ravishda native `ILIKE` ga kompilyatsiya qilinadi. Baza ustuniga `lower()` yoki `upper()` funksiyalari o'ralmasligi sababli PostgreSQL B-Tree indekslaridan to'liq foydalaniladi. Boshqa ma'lumotlar bazalarida esa xavfsiz `lower()` fallback ishlatiladi.
12. **Single-Query Pagination (`IncludeTotalCount = false`)**: `COUNT(*)` so'rovini chetlab o'tib, bitta so'rovda `Take(pageSize + 1)` bilan `items` va `hasNext` ni aniqlaydi. Bazaga tushadigan yuklamani 50% ga qisqartiradi va javob qaytarish vaqtini 2.8x ga tezlashtiradi.
13. **Zero-Reflection Hot-Path**: `PropertyChainCache` va `SortStepCache` orqali so'rov bajarilish vaqtida barcha reflection lookup'lar, generik metod chaqiruvlari va satr bo'linishlari (`.Split`) butunlay yo'q qilingan; saralash nanosoniyada ishlaydi.
14. **DB Observability (`TagWith`)**: Avtomatik ravishda har bir SQL ga `-- Qryx: DtoName` izohini qo'shadi, bu esa DBA larga sekin so'rovlarni tahlil qilishni osonlashtiradi.
15. **Zero-Copy Streaming (`ToPagedAsyncEnumerable`)**: Katta hajmdagi ma'lumotlarni RAM ga yig'masdan to'g'ridan-to'g'ri `IAsyncEnumerable` sifatida HTTP response streamiga oqizish imkoniyati.
16. **ModelBinding Fast Compiled Setters**: HTTP parametrlarini DTO ga bog'lashda `prop.SetValue` reflection o'rniga kompilyatsiya qilingan delegatlar ishlatiladi.
17. **Roslyn Source Generator (`Qryx.SourceGenerator`)**: DTO larni kompilyatsiya vaqtida tahlil qilib, compile-time tezkor extension methodlarni generatsiya qiladi.
18. **Ilg'or Operatorlar**: Full-Text Search (`Op.Fts`) va JSONB qidiruv (`Op.JsonContains`).

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
    // Bu yerda DB indeksi buzilmasligi uchun ikkala ustunga OR bilan ulanadi.
    // Invariant = true (default): PostgreSQL'da to'g'ridan-to'g'ri ILIKE ga aylanadi (lower() chaqirmaydi),
    // boshqa ma'lumotlar bazalarida esa xavfsiz lower() fallback ishlatiladi.
    [Filter(Op.StartsWith, TargetProperties = [nameof(User.FirstName), nameof(User.LastName)], Invariant = true)]
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

| Ko'rsatkich / Sharoit | Qryx (Compiled AST) | Qryx (Source Gen) | Gridify | Sieve | OData |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **So'rov tuzish (Construction)** | **6.27 μs (4.0x tezroq)** | **14.55 μs (1.7x tezroq)** | 25.07 μs | 27.60 μs | 46.84 μs |
| **Xotira sarfi (Allocated Heap)** | **3.8 KB (Eng kam!)** | **7.7 KB** | 25.4 KB (6.7x ko'p) | 23.7 KB (6.2x ko'p) | 43.9 KB (11.5x ko'p) |
| **Real PostgreSQL DB (10k items)** | **639 μs (21.1 KB)** | 751 μs (36.5 KB) | 612 μs (46.5 KB) | 607 μs (50.4 KB) | 611 μs (67.7 KB) |
| **Parallel yuklama (Concurrency)** | **1,270,000+ ops/s** (786 ns) | **761,000+ ops/s** (1.31 μs) | ~205,000 ops/s (6.2x sekin) | ~88,000 ops/s (14.4x sekin) | ~77,000 ops/s (16.4x sekin) |
| **Single-Query Sahifalash (Postgres)** | **238 μs (3.25x tezroq)** | - | Qo'llab-quvvatlamaydi | Qo'llab-quvvatlamaydi | Qo'llab-quvvatlamaydi |
| **Zero-Copy Stream (IAsyncEnumerable)** | **243 μs (3.19x tezroq)** | - | Qo'llab-quvvatlamaydi | Qo'llab-quvvatlamaydi | Qo'llab-quvvatlamaydi |

Benchmarklarni ishga tushirish:
```bash
# Barcha testlarni (Construction, InMemory, PostgreSQL, Concurrency) ishga tushirish:
dotnet run -c Release --project benchmarks/Qryx.Benchmarks/Qryx.Benchmarks.csproj -- --fast

# Faqat Real PostgreSQL Docker bazasini testlash:
dotnet run -c Release --project benchmarks/Qryx.Benchmarks/Qryx.Benchmarks.csproj -- --postgres

# Rasmiy BenchmarkDotNet testlari:
dotnet run -c Release --project benchmarks/Qryx.Benchmarks/Qryx.Benchmarks.csproj -- --bdn
```
