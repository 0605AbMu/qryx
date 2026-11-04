# Qryx (`/krɪks/`)

**English** | [O'zbekcha](https://github.com/0605AbMu/qryx/blob/master/README.uz.md)

[![NuGet](https://img.shields.io/nuget/v/Qryx.AspNetCore.svg?label=NuGet)](https://www.nuget.org/packages/Qryx.AspNetCore/)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://github.com/0605AbMu/qryx/blob/master/LICENSE)
[![.NET 8 | 10](https://img.shields.io/badge/.NET-8%20%7C%2010-512BD4.svg)](https://dotnet.microsoft.com/)

**Parser-free, DTO-centric, strongly-typed dynamic query engine for .NET 8 and .NET 10**

Qryx turns REST query strings directly into safe, parameterized, index-friendly Entity Framework Core `IQueryable` expression trees. You describe filters on a plain C# DTO; Qryx binds the query string, filters, sorts and pages, and returns a compact response.

> 📖 **Full developer guide** (architecture, every operator with URL examples, pagination strategies, production pitfalls, database recipes):
> [docs/USAGE.md](https://github.com/0605AbMu/qryx/blob/master/docs/USAGE.md) — currently in Uzbek.

---

## 📦 Architecture

Qryx is split into 7 NuGet packages plus a sample and a benchmark suite:

```text
src/
├── Qryx.Core/              # Contracts, [Filter]/[Sortable]/[NoTotal], Op, Range<T>, QryxList<T> (0 dependencies)
├── Qryx.Expressions/       # Expression tree builder, parameterized SQL, sort plans, keyset cursors, caches
├── Qryx.EntityFramework/   # EF Core extensions: ToPagedResultAsync, ToPagedAsyncEnumerable, PagedResult<T>
├── Qryx.AspNetCore/        # Model binding, Minimal API / MVC integration, HTTP 400 mapping
├── Qryx.SourceGenerator/   # Roslyn incremental source generator (compile-time plans and diagnostics)
├── Qryx.OpenApi/           # .NET 10 Microsoft.AspNetCore.OpenApi transformers
└── Qryx.Swashbuckle/       # Classic Swashbuckle (SwaggerGen) filters
benchmarks/
└── Qryx.Benchmarks/        # BenchmarkDotNet and fast diagnostic suite (Qryx vs Gridify vs Sieve vs OData)
samples/
└── Qryx.SampleApi/         # Minimal API + EF Core InMemory + Swashbuckle sample
```

### NuGet packages

| Package | Version | Purpose |
| :--- | :--- | :--- |
| [Qryx.Core](https://www.nuget.org/packages/Qryx.Core/) | [![NuGet](https://img.shields.io/nuget/v/Qryx.Core.svg)](https://www.nuget.org/packages/Qryx.Core/) | DTO contracts: `QryxRequest<T>`, `[Filter]`, `[Sortable]`, `[NoTotal]`, `Op`, `Range<T>`, `QryxList<T>`, `QryxRequestException` |
| [Qryx.Expressions](https://www.nuget.org/packages/Qryx.Expressions/) | [![NuGet](https://img.shields.io/nuget/v/Qryx.Expressions.svg)](https://www.nuget.org/packages/Qryx.Expressions/) | Expression tree engine (filtering, sorting, paging) |
| [Qryx.EntityFramework](https://www.nuget.org/packages/Qryx.EntityFramework/) | [![NuGet](https://img.shields.io/nuget/v/Qryx.EntityFramework.svg)](https://www.nuget.org/packages/Qryx.EntityFramework/) | `ToPagedResultAsync`, `ToPagedAsyncEnumerable`, `ApplyQryx`, `PagedResult<T>`, keyset |
| [Qryx.AspNetCore](https://www.nuget.org/packages/Qryx.AspNetCore/) | [![NuGet](https://img.shields.io/nuget/v/Qryx.AspNetCore.svg)](https://www.nuget.org/packages/Qryx.AspNetCore/) | `AddQryx()`, `.WithQryx()`, MVC binder, HTTP 400 mapping |
| [Qryx.SourceGenerator](https://www.nuget.org/packages/Qryx.SourceGenerator/) | [![NuGet](https://img.shields.io/nuget/v/Qryx.SourceGenerator.svg)](https://www.nuget.org/packages/Qryx.SourceGenerator/) | `ApplyGenerated(dto)`, `QRYX001`/`QRYX002` diagnostics |
| [Qryx.OpenApi](https://www.nuget.org/packages/Qryx.OpenApi/) | [![NuGet](https://img.shields.io/nuget/v/Qryx.OpenApi.svg)](https://www.nuget.org/packages/Qryx.OpenApi/) | .NET 10 `Microsoft.AspNetCore.OpenApi` transformers (`AddQryxTransformers()`) |
| [Qryx.Swashbuckle](https://www.nuget.org/packages/Qryx.Swashbuckle/) | [![NuGet](https://img.shields.io/nuget/v/Qryx.Swashbuckle.svg)](https://www.nuget.org/packages/Qryx.Swashbuckle/) | Classic Swashbuckle (SwaggerGen) filters (`AddQryxFilters()`) |

Typical Web API installation:
```bash
dotnet add package Qryx.AspNetCore
dotnet add package Qryx.EntityFramework
# Optional: Qryx.Swashbuckle (or Qryx.OpenApi) for API docs, Qryx.SourceGenerator for compile-time diagnostics
```
`Qryx.AspNetCore` and `Qryx.EntityFramework` bring `Qryx.Core` and `Qryx.Expressions` as dependencies. If your DTOs live in a separate (domain/application) project, `Qryx.Core` alone is enough there. `Qryx.SourceGenerator` is an analyzer and brings no dependencies, so install it together with `Qryx.EntityFramework`.

### Supported frameworks

| Package | .NET 8 (EF Core 8) | .NET 10 (EF Core 10) |
| :--- | :---: | :---: |
| `Qryx.Core`, `Qryx.Expressions`, `Qryx.EntityFramework`, `Qryx.AspNetCore`, `Qryx.Swashbuckle` | ✅ | ✅ |
| `Qryx.SourceGenerator` (`netstandard2.0` analyzer) | ✅ | ✅ |
| `Qryx.OpenApi` | ❌ use `Qryx.Swashbuckle` | ✅ |

`Qryx.OpenApi` builds on the document transformers of `Microsoft.AspNetCore.OpenApi`, which exist from .NET 9, so it targets .NET 10 only. Behavior is identical on both runtimes, and the whole test suite runs on each. Microsoft support for .NET 8 and EF Core 8 ends on **November 10, 2026**, so plan to move to .NET 10 for continued security updates.

---

## 🚀 Features

1. **Parser-free & type-safe**: no string query language (OData/GraphQL style) to parse; filters are strongly-typed C# DTO contracts.
2. **Database-provider independent**: produces standard LINQ expressions, so it works with any EF Core provider (PostgreSQL, SQL Server, SQLite, MySQL, Oracle, InMemory). PostgreSQL (Npgsql) is detected automatically and gets native `ILIKE`, full-text search and jsonb translations; other providers use standard fallbacks. Tests and benchmarks run on PostgreSQL and EF InMemory.
3. **Clean URLs (prefix convention)**: `minAge=20&maxAge=35`, `fromDate=...&toDate=...` map to range filters automatically.
4. **Multi-target & index friendly**: computed fields (e.g. `FullName`) map to several columns (`FirstName`, `LastName`) with `Expression.OrElse`, keeping B-tree indexes usable.
5. **Minimal heap allocation**: reflection runs only on first use; later requests use compiled delegates and cached metadata.
6. **DoS guards**: `PageSize` is silently clamped to 100 (`QryxOptions.MaxPageSize`). OFFSET is limited to 10,000 (`QryxOptions.MaxOffset`; use keyset for deeper pages) and `In` lists to 1,000 values; anything beyond returns 400. `sortBy` is limited to 256 characters / 5 columns; extra clauses are ignored (400 with `ThrowOnInvalidSortProperty = true`).
7. **SQL-injection free & plan-cache friendly**: every filter and cursor value becomes a SQL parameter (`@p`), never a literal. EF Core does not recompile the query and the database keeps one plan.
8. **Keyset / cursor pagination**: every page (offset pages too) returns `nextCursor`; pass it to `?afterCursor=` to continue at $O(\log N)$ regardless of depth. The cursor is derived from `sortBy` (+ primary-key tie-breaker), supports mixed directions, and never skips rows with equal values.
9. **Compact JSON envelope**: `{ "items", "hasNext", "nextCursor"?, "total"? }`. `nextCursor` is present when a next page exists, `total` only with `?includeTotal=true`.
10. **Classic Swashbuckle & modern OpenAPI**: full integration with Swagger UI (`Swashbuckle.AspNetCore`) and .NET 10 OpenAPI; every Qryx parameter is documented as optional with the correct schema.
11. **Database-aware case-insensitive matching**: with `Invariant = true` (default `false`, i.e. plain `LIKE`), `StartsWith`, `EndsWith` and `Contains` compile to native `ILIKE ... ESCAPE '\'` on PostgreSQL (user-supplied `%` and `_` are escaped). Other databases use an `UPPER()` fallback.
12. **One query per page (default)**: `Take(pageSize + 1)` yields `items`, `hasNext` and `nextCursor` in a single SQL statement; `COUNT(*)` runs only with `?includeTotal=true` (and never for `[NoTotal]` DTOs on huge tables). 2.3x faster than count + data.
13. **Zero-reflection hot path**: `PropertyChainCache`, `ClauseCache` and `SortStepCache` remove reflection lookups, generic method construction and `sortBy` re-parsing from repeated requests (they run on first encounter only).
14. **DB observability (`TagWith`, optional)**: with `QryxOptions.EnableQueryTagging = true` (static, default `false`) every SQL statement gets a `-- Qryx: DtoName` comment, which helps DBAs analyze slow queries.
15. **Zero-copy streaming (`ToPagedAsyncEnumerable`)**: streams one page as `IAsyncEnumerable` straight to the HTTP response without buffering a `List<T>` (`pageSize` is still capped by `MaxPageSize`; for an unpaged export use `ApplyQryx(query).AsAsyncEnumerable()`).
16. **Fast compiled setters in model binding**: query parameters are bound with compiled delegates instead of `PropertyInfo.SetValue`.
17. **Roslyn source generator (`Qryx.SourceGenerator`)**: analyzes DTOs at compile time and generates a pre-built, parameterized query plan per DTO (`query.ApplyGenerated(dto)`); misconfiguration is reported at compile time as `QRYX001`/`QRYX002`.
18. **Advanced operators**: full-text search (`Op.Fts` → PostgreSQL `websearch_to_tsquery`), jsonb search (`Op.JsonContains` → `@>` / `?`) and range filters (`Op.Between` + `Range<T>`, `?age=20..35`).
19. **Client errors are HTTP 400**: invalid values, tampered cursors or cursors created for another `sortBy`, pages deeper than `MaxOffset`, more than 1,000 `In` values and (with `ThrowOnInvalidSortProperty = true`) disallowed sort columns raise `QryxRequestException`, returned as `ValidationProblemDetails` (Minimal API: `.WithQryx()`, MVC: `AddQryx()`).

---

## 💻 Quick start

### 1. Declare a DTO

```csharp
using Qryx.Core.Attributes;
using Qryx.Core.Contracts;
using Qryx.Core.Enums;

public record UserQueryDto : QryxRequest<User>
{
    // 1. Prefix convention (clean URL: ?minAge=20&maxAge=35)
    // Mapped to the User.Age column
    [Filter(nameof(User.Age), Op.GreaterThanOrEqual)]
    public int? MinAge { get; init; }

    [Filter(nameof(User.Age), Op.LessThanOrEqual)]
    public int? MaxAge { get; init; }

    // 2. Enum / array filter (?status=Active&status=Pending)
    [Filter(Op.In)]
    public UserStatus[]? Status { get; init; }

    // 3. Computed field (FullName): the entity columns it maps to are listed explicitly.
    // Both columns are combined with OR so database indexes stay usable.
    // Invariant = true: native ILIKE on PostgreSQL (no lower()/upper() around the column),
    // UPPER(column) LIKE @p fallback on other databases.
    [Filter(Op.StartsWith, TargetProperties = [nameof(User.FirstName), nameof(User.LastName)], Invariant = true)]
    public string? FullName { get; init; }

    // 4. Navigation property (?departmentName=Sales)
    [Filter(Op.Equals, TargetProperty = "Department.Name")]
    public string? DepartmentName { get; init; }
}
```

### 2. Minimal API integration

```csharp
using Microsoft.EntityFrameworkCore;
using Qryx.AspNetCore.Extensions;
using Qryx.EntityFramework.Extensions;
using Qryx.Swashbuckle.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(opt => opt.UseSqlServer(...));
builder.Services.AddQryx();

// Classic Swashbuckle integration
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

### 3. HTTP request and response

**Request:**
```http
GET /api/users?fullName=Ali&minAge=20&maxAge=35&status=Active&departmentName=Sales&sortBy=CreatedAt&isDescending=true&page=1&pageSize=10
```
`sortBy=CreatedAt` only works when `User.CreatedAt` (or a DTO property) is marked `[Sortable]`; otherwise it is ignored.

**Response:**
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
  "hasNext": false
}
```
`total` is returned only with `?includeTotal=true`. When a next page exists, the response also contains `"nextCursor"`: pass it to `?afterCursor=` together with the same `sortBy`. Enums appear as names because `JsonStringEnumConverter` is assumed to be configured (otherwise `"status": 0`).

---

## 🛠 Build and run

```bash
# Build everything
dotnet build Qryx.slnx

# Run all tests
dotnet test Qryx.slnx

# Run the sample API
dotnet run --project samples/Qryx.SampleApi/Qryx.SampleApi.csproj
```

Swagger UI: `http://localhost:5052/swagger`
API endpoint: `http://localhost:5052/api/users`
(the port comes from the `http` profile in `samples/Qryx.SampleApi/Properties/launchSettings.json`)

---

## ⚡ Performance benchmark (Qryx vs Gridify vs Sieve vs OData)

The full benchmark project lives in [benchmarks/Qryx.Benchmarks](https://github.com/0605AbMu/qryx/tree/master/benchmarks/Qryx.Benchmarks). The numbers come from the `--fast` diagnostic runner (.NET 10, Release, PostgreSQL 17 in Docker, 10,000 rows). All frameworks run the same filter: the runner compares the ID sets each framework selects before timing anything. Each value is the median of 5–7 interleaved rounds whose order rotates every round.

| Metric / scenario | Qryx (Source Gen) | Qryx (Dynamic) | Gridify | Sieve | OData |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **Query construction** | **2.71 μs / 3.1 KB** | **2.80 μs / 3.1 KB** | 13.47 μs / 25.3 KB (5x slower) | 18.84 μs / 23.8 KB (7x) | 29.95 μs / 44.3 KB (11x) |
| **Concurrency (Parallel.For)** | **613 ns (1.63M ops/s)** | **628 ns (1.59M ops/s)** | 3.13 μs (5x slower) | 7.72 μs (12x) | 6.94 μs (11x) |
| **Real PostgreSQL, same values** | 774 μs / 29 KB | **747 μs / 29 KB** | 762 μs / 45 KB | 845 μs / 50 KB | 838 μs / 70 KB |
| **Real PostgreSQL, new values per request** | — | **760 μs** | 3.19 ms (**4.2x slower**) | 914 μs | 878 μs |
| **EF InMemory, same values** ¹ | 539 μs | 531 μs | **360 μs** | 834 μs | 707 μs |
| **EF InMemory, new values per request** | — | **581 μs** | 3.78 ms (6.5x slower) | 802 μs | 821 μs |

**Pagination strategies (PostgreSQL):** count + data 1.16 ms · single query 514 μs (2.3x faster) · keyset cursor 488 μs (2.4x faster) · zero-copy stream 511 μs.

**Npgsql Auto Prepare** (`Max Auto Prepare=50;Auto Prepare Min Usages=2` in the connection string, no code change): Qryx gets **10–15% faster** because its parameterized SQL text never changes and PostgreSQL reuses the prepared plan. Gridify gains 2–3%, and nothing when values change, because its literal SQL differs every time.

¹ EF InMemory re-reads query parameters for every row, so Gridify, which inlines SQL literals, looks faster there. That advantage disappears on a real database, and when values change per request Gridify recompiles the query every time.
On a 10k-row table most of the time is the network roundtrip, so differences of ±5% are within noise.

Running the benchmarks (the PostgreSQL suites connect to `localhost:5432`; connection string in `benchmarks/Qryx.Benchmarks/Services/PostgresBenchmarkHelper.cs`):
```bash
# All suites (construction, InMemory, PostgreSQL, concurrency, pagination, unique values, Auto Prepare)
dotnet run -c Release --project benchmarks/Qryx.Benchmarks/Qryx.Benchmarks.csproj -- --fast

# Only "new values per request" (realistic API traffic)
dotnet run -c Release --project benchmarks/Qryx.Benchmarks/Qryx.Benchmarks.csproj -- --unique

# Only Npgsql Auto Prepare (prepared statements)
dotnet run -c Release --project benchmarks/Qryx.Benchmarks/Qryx.Benchmarks.csproj -- --prepare

# Only real PostgreSQL
dotnet run -c Release --project benchmarks/Qryx.Benchmarks/Qryx.Benchmarks.csproj -- --postgres

# Official BenchmarkDotNet runs
dotnet run -c Release --project benchmarks/Qryx.Benchmarks/Qryx.Benchmarks.csproj -- --bdn
```

---

## 🤝 Contributing

Contributions are welcome: bug reports, documentation (including English translations of the guide), tests and features. Read [CONTRIBUTING.md](https://github.com/0605AbMu/qryx/blob/master/CONTRIBUTING.md) before opening a pull request, and follow the [Code of Conduct](https://github.com/0605AbMu/qryx/blob/master/CODE_OF_CONDUCT.md).

- 📝 Changes per release: [CHANGELOG.md](https://github.com/0605AbMu/qryx/blob/master/CHANGELOG.md)
- 🔒 Security issues: please report them privately as described in [SECURITY.md](https://github.com/0605AbMu/qryx/blob/master/SECURITY.md), not in public issues.

## 📄 License

Qryx is licensed under the [MIT License](https://github.com/0605AbMu/qryx/blob/master/LICENSE).
