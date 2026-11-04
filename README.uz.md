# Qryx (`/krɪks/`)

[English](README.md) | **O'zbekcha**

**Parser-free, DTO-Centric, Strongly-Typed Dynamic Query Engine for .NET 8 and .NET 10**

Qryx REST API so'rov parametrlarini to'g'ridan-to'g'ri xavfsiz, yuqori unumdorlikka ega va indekslarga mos Entity Framework Core `IQueryable` Expression Tree'lariga o'girib beruvchi zamonaviy dinamik so'rov dvigatelidir.

> 📖 **Dasturchilar uchun to'liq qo'llanma, arxitekturaviy tavsiyalar va ishlab chiqarishdagi kritik nuqtalar**:  
> [Dasturchilar uchun To'liq Qo'llanma (USAGE.md)](docs/USAGE.md) faylida batafsil yoritilgan.

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

### NuGet Paketlari

| Paket | Versiya | Vazifasi |
| :--- | :--- | :--- |
| [Qryx.Core](https://www.nuget.org/packages/Qryx.Core/) | [![NuGet](https://img.shields.io/nuget/v/Qryx.Core.svg)](https://www.nuget.org/packages/Qryx.Core/) | DTO shartnomalari: `QryxRequest<T>`, `[Filter]`, `[Sortable]`, `[NoTotal]`, `Op`, `Range<T>`, `QryxList<T>`, `QryxRequestException` |
| [Qryx.Expressions](https://www.nuget.org/packages/Qryx.Expressions/) | [![NuGet](https://img.shields.io/nuget/v/Qryx.Expressions.svg)](https://www.nuget.org/packages/Qryx.Expressions/) | Expression tree dvigateli (filtr, saralash, sahifalash) |
| [Qryx.EntityFramework](https://www.nuget.org/packages/Qryx.EntityFramework/) | [![NuGet](https://img.shields.io/nuget/v/Qryx.EntityFramework.svg)](https://www.nuget.org/packages/Qryx.EntityFramework/) | `ToPagedResultAsync`, `ToPagedAsyncEnumerable`, `ApplyQryx`, `PagedResult<T>`, keyset |
| [Qryx.AspNetCore](https://www.nuget.org/packages/Qryx.AspNetCore/) | [![NuGet](https://img.shields.io/nuget/v/Qryx.AspNetCore.svg)](https://www.nuget.org/packages/Qryx.AspNetCore/) | `AddQryx()`, `.WithQryx()`, MVC binder, HTTP 400 mapping |
| [Qryx.SourceGenerator](https://www.nuget.org/packages/Qryx.SourceGenerator/) | [![NuGet](https://img.shields.io/nuget/v/Qryx.SourceGenerator.svg)](https://www.nuget.org/packages/Qryx.SourceGenerator/) | `ApplyGenerated(dto)`, `QRYX001`/`QRYX002` diagnostikalari |
| [Qryx.OpenApi](https://www.nuget.org/packages/Qryx.OpenApi/) | [![NuGet](https://img.shields.io/nuget/v/Qryx.OpenApi.svg)](https://www.nuget.org/packages/Qryx.OpenApi/) | .NET 10 `Microsoft.AspNetCore.OpenApi` transformerlari (`AddQryxTransformers()`) |
| [Qryx.Swashbuckle](https://www.nuget.org/packages/Qryx.Swashbuckle/) | [![NuGet](https://img.shields.io/nuget/v/Qryx.Swashbuckle.svg)](https://www.nuget.org/packages/Qryx.Swashbuckle/) | Classic Swashbuckle (SwaggerGen) filtrlari (`AddQryxFilters()`) |

Odatiy Web API uchun o'rnatish:
```bash
dotnet add package Qryx.AspNetCore
dotnet add package Qryx.EntityFramework
# Ixtiyoriy: Swagger hujjatlari uchun Qryx.Swashbuckle (yoki Qryx.OpenApi), kompilyatsiya diagnostikalari uchun Qryx.SourceGenerator
```
`Qryx.AspNetCore` va `Qryx.EntityFramework` o'zlari `Qryx.Core` va `Qryx.Expressions` ni bog'liqlik sifatida olib keladi. DTO'lar alohida (domain/application) loyihada joylashgan bo'lsa, u yerga faqat `Qryx.Core` yetarli. `Qryx.SourceGenerator` esa bog'liqlik olib kelmaydi (analyzer), shuning uchun uni `Qryx.EntityFramework` bilan birga o'rnating.

### Qo'llab-quvvatlanadigan platformalar

| Paket | .NET 8 (EF Core 8) | .NET 10 (EF Core 10) |
| :--- | :---: | :---: |
| `Qryx.Core`, `Qryx.Expressions`, `Qryx.EntityFramework`, `Qryx.AspNetCore`, `Qryx.Swashbuckle` | ✅ | ✅ |
| `Qryx.SourceGenerator` (`netstandard2.0` analyzer) | ✅ | ✅ |
| `Qryx.OpenApi` | ❌ `Qryx.Swashbuckle` dan foydalaning | ✅ |

`Qryx.OpenApi` `Microsoft.AspNetCore.OpenApi` ning document transformer'lariga tayanadi, ular .NET 9 dan boshlab mavjud, shuning uchun bu paket faqat .NET 10 uchun. Ikkala platformada xatti-harakat bir xil va barcha testlar har birida ishga tushiriladi. Microsoft'ning .NET 8 va EF Core 8 ni qo'llab-quvvatlashi **2026-yil 10-noyabrda** tugaydi, xavfsizlik yangilanishlarini olishda davom etish uchun .NET 10 ga o'tishni rejalashtiring.

---

## 🚀 Asosiy Xususiyatlar

1. **Parser-free & Type-safe**: Satrli so'rovlar (OData/GraphQL) parserlarisiz, to'liq C# kuchli tiplashtirilgan DTO shartnomalari.
2. **Database-Provider Independence**: Hech qanday ma'lumotlar bazasiga qattiq bog'lanmagan: standart LINQ ifodalarini hosil qiladi, shuning uchun istalgan EF Core provayderi (PostgreSQL, SQL Server, SQLite, MySQL, Oracle, InMemory) bilan ishlaydi. PostgreSQL (Npgsql) avtomatik aniqlanadi va native `ILIKE`, FTS, jsonb tarjimalari ishlatiladi; boshqa provayderlarda standart fallback ishlaydi. Testlar va benchmarklar PostgreSQL va EF InMemory'da o'tkazilgan.
3. **Eng toza URL (Prefiks yondashuvi)**: `minAge=20&maxAge=35`, `fromDate=...&toDate=...` konvensiyasi bo'yicha oraliq filtrlar avtomatik moslashtiriladi.
4. **Multi-Target & Indeks Do'stligi**: Hisoblangan yoki birlashgan maydonlar (masalan, `FullName`) bir nechta ustunga (`FirstName`, `LastName`) `Expression.OrElse` orqali tarjima qilinadi va B-Tree indekslarini buzmaydi.
5. **Minimal Heap Allocation**: Reflection faqat birinchi so'rovda ishlaydi, qolgan so'rovlar kompilyatsiya qilingan delegatlar va keshdagi metadata orqali bajariladi.
6. **DoS Guard**: `PageSize` maksimal 100 gacha jimgina cheklanadi (`QryxOptions.MaxPageSize`). OFFSET maksimal 10,000 (`QryxOptions.MaxOffset`, chuqurroq sahifalar uchun keyset) va `In` ro'yxati 1,000 qiymat bilan cheklangan, ortig'i 400 bilan rad etiladi. `sortBy` 256 belgi / 5 ustun bilan cheklangan: ortig'i e'tiborsiz qoldiriladi (`ThrowOnInvalidSortProperty = true` bo'lsa 400).
7. **SQL Injection Free & Plan Cache Friendly**: Barcha filtr va cursor qiymatlari SQL parametrlariga aylanadi (`@p`), literal emas. EF Core so'rovni qayta kompilyatsiya qilmaydi, baza bitta plan saqlaydi.
8. **Keyset / Cursor Pagination**: har bir sahifa (offset ham) `nextCursor` qaytaradi; uni `?afterCursor=` ga berib, chuqurlikdan qat'i nazar $O(\log N)$ tezlikda davom etiladi. Cursor `sortBy` (+ PK tie-breaker) dan hosil bo'ladi, aralash yo'nalishlar qo'llab-quvvatlanadi, bir xil qiymatli qatorlar tushib qolmaydi.
9. **Ixcham JSON envelope**: `{ "items", "hasNext", "nextCursor"?, "total"? }`. `nextCursor` keyingi sahifa bo'lganda, `total` esa faqat `?includeTotal=true` bo'lganda qaytadi.
10. **Classic Swashbuckle & Modern OpenAPI**: Classic Swagger UI (`Swashbuckle.AspNetCore`) va .NET 10 ning yangi OpenAPI kutubxonalari bilan to'liq integratsiya.
11. **Database-Aware Invariant String Matching**: `StartsWith`, `EndsWith`, `Contains` biznes operatorlari uchun `Invariant = true` bo'lganda (standart: `false`, ya'ni to'g'ridan-to'g'ri `LIKE`), PostgreSQL provayderida native `ILIKE ... ESCAPE '\'` ga kompilyatsiya qilinadi (foydalanuvchi kiritgan `%`, `_` escape qilinadi). Boshqa ma'lumotlar bazalarida `UPPER()` fallback ishlatiladi.
12. **Bitta so'rovli sahifalash (standart)**: `Take(pageSize + 1)` bilan `items`, `hasNext` va `nextCursor` bitta SQL so'rovda olinadi, `COUNT(*)` faqat `?includeTotal=true` bo'lganda bajariladi (katta jadvallarda `[NoTotal]` uni butunlay o'chiradi). Count + Data rejimidan 2.3x tezroq.
13. **Zero-Reflection Hot-Path**: `PropertyChainCache`, `ClauseCache` va `SortStepCache` keshlari tufayli takroriy so'rovlarda reflection lookup'lar, generik metod yasash va `sortBy` ni qayta parsing qilish bajarilmaydi (ular faqat birinchi uchrashuvda ishlaydi).
14. **DB Observability (`TagWith`, ixtiyoriy)**: `QryxOptions.EnableQueryTagging = true` (statik, standart `false`) bo'lsa, har bir SQL ga `-- Qryx: DtoName` izohini qo'shadi, bu esa DBA larga sekin so'rovlarni tahlil qilishni osonlashtiradi.
15. **Zero-Copy Streaming (`ToPagedAsyncEnumerable`)**: Bitta sahifani RAM ga `List<T>` qilib yig'masdan to'g'ridan-to'g'ri `IAsyncEnumerable` sifatida HTTP response streamiga oqizish imkoniyati (`pageSize` baribir `MaxPageSize` bilan cheklanadi; sahifasiz eksport uchun `ApplyQryx(query).AsAsyncEnumerable()`).
16. **ModelBinding Fast Compiled Setters**: HTTP parametrlarini DTO ga bog'lashda `prop.SetValue` reflection o'rniga kompilyatsiya qilingan delegatlar ishlatiladi.
17. **Roslyn Source Generator (`Qryx.SourceGenerator`)**: DTO larni kompilyatsiya vaqtida tahlil qilib, har bir DTO uchun oldindan qurilgan, parametrli typed so'rov rejasini (`query.ApplyGenerated(dto)`) generatsiya qiladi; noto'g'ri konfiguratsiya `QRYX001`/`QRYX002` diagnostikalari bilan kompilyatsiyada aniqlanadi.
18. **Ilg'or Operatorlar**: Full-Text Search (`Op.Fts` -> PostgreSQL `websearch_to_tsquery`), JSONB qidiruv (`Op.JsonContains` -> `@>` / `?`) va oraliq filtri (`Op.Between` + `Range<T>`, `?age=20..35`).
19. **Klient xatolari = HTTP 400**: noto'g'ri qiymat, buzilgan yoki boshqa `sortBy` uchun yaratilgan cursor, `MaxOffset` dan chuqur `page`, 1,000 dan ortiq `In` qiymati va (`ThrowOnInvalidSortProperty = true` bo'lsa) ruxsatsiz sort ustuni `QryxRequestException` -> `ValidationProblemDetails` (Minimal API: `.WithQryx()`, MVC: `AddQryx()`).

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
    // Invariant = true: PostgreSQL'da to'g'ridan-to'g'ri ILIKE ga aylanadi (lower()/upper() chaqirmaydi),
    // boshqa ma'lumotlar bazalarida esa UPPER(ustun) LIKE @p fallback ishlatiladi.
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
`sortBy=CreatedAt` ishlashi uchun `User.CreatedAt` (yoki DTO xossasi) `[Sortable]` bilan belgilangan bo'lishi kerak, aks holda u e'tiborsiz qoldiriladi.

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
  "hasNext": false
}
```
`total` faqat `?includeTotal=true` bo'lganda qaytadi. Keyingi sahifa mavjud bo'lsa, javobda `"nextCursor"` ham bo'ladi: uni xuddi o'sha `sortBy` bilan `?afterCursor=` ga bering. Enum'lar javobda nom sifatida chiqishi uchun JSON'da `JsonStringEnumConverter` sozlangan deb hisoblangan (aks holda `"status": 0`).

---

## 🛠 Loyihani Qurish va Ishga Tushirish

```bash
# Barcha loyihalarni kompilyatsiya qilish
dotnet build Qryx.slnx

# Sample API'ni ishga tushirish
dotnet run --project samples/Qryx.SampleApi/Qryx.SampleApi.csproj
```

Swagger UI brauzerda: `http://localhost:5052/swagger`
API Endpoint: `http://localhost:5052/api/users`
(port `samples/Qryx.SampleApi/Properties/launchSettings.json` dagi `http` profilidan olinadi)

---

## ⚡ Performance Benchmark (Qryx vs Gridify vs Sieve vs OData)

To'liq benchmark loyihasi [Qryx.Benchmarks](benchmarks/Qryx.Benchmarks) manzilida joylashgan. Natijalar `--fast` diagnostik runner'idan olingan (.NET 10, Release, PostgreSQL 17 Docker, 10,000 qator). Barcha frameworklar bir xil filtrni bajaradi: runner har bir framework tanlagan ID to'plamlarini solishtirib, buni tekshiradi. Ko'rsatilgan qiymat — navbatma-navbat o'tkazilgan 5–7 raundning medianasi; raundlar tartibi har safar almashtiriladi.

| Ko'rsatkich / Sharoit | Qryx (Source Gen) | Qryx (Dynamic) | Gridify | Sieve | OData |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **So'rov tuzish (Construction)** | **2.71 μs / 3.1 KB** | **2.80 μs / 3.1 KB** | 13.47 μs / 25.3 KB (5x sekin) | 18.84 μs / 23.8 KB (7x) | 29.95 μs / 44.3 KB (11x) |
| **Parallel yuklama (Concurrency)** | **613 ns (1.63M ops/s)** | **628 ns (1.59M ops/s)** | 3.13 μs (5x sekin) | 7.72 μs (12x) | 6.94 μs (11x) |
| **Real PostgreSQL, bir xil qiymatlar** | 774 μs / 29 KB | **747 μs / 29 KB** | 762 μs / 45 KB | 845 μs / 50 KB | 838 μs / 70 KB |
| **Real PostgreSQL, har so'rovda yangi qiymat** | — | **760 μs** | 3.19 ms (**4.2x sekin**) | 914 μs | 878 μs |
| **EF InMemory, bir xil qiymatlar** ¹ | 539 μs | 531 μs | **360 μs** | 834 μs | 707 μs |
| **EF InMemory, har so'rovda yangi qiymat** | — | **581 μs** | 3.78 ms (6.5x sekin) | 802 μs | 821 μs |

**Sahifalash strategiyalari (PostgreSQL):** Count + Data 1.16 ms · Single-Query 514 μs (2.3x tezroq) · Keyset Cursor 488 μs (2.4x tezroq) · Zero-Copy Stream 511 μs.

**Npgsql Auto Prepare** (`Max Auto Prepare=50;Auto Prepare Min Usages=2` ulanish satrida, kod o'zgarmaydi): Qryx **10–15% tezlashadi**, chunki uning parametrli SQL matni o'zgarmaydi va PostgreSQL tayyor planni qayta ishlatadi. Gridify'da natija −2–3%, qiymatlar o'zgarganda esa foyda yo'q, chunki literal SQL har safar boshqacha bo'ladi. Batafsil ma'lumot [USAGE.md 10-bo'limida](docs/USAGE.md#10-malumotlar-bazasi-indekslash-retseptlari-db-recipes).

¹ EF InMemory provayderi so'rov parametrlarini har bir qator uchun qayta o'qiydi, shuning uchun SQL literal'larini so'rov ichiga yozadigan Gridify bu yerda tezroq ko'rinadi. Real bazada bu ustunlik yo'qoladi. Qiymatlar har so'rovda o'zgarganda esa Gridify har safar yangi so'rov kompilyatsiya qiladi.
Real bazada 10k qatorli jadvalda vaqtning asosiy qismi tarmoq roundtrip'iga ketadi, shuning uchun ±5% farq shovqin chegarasida.

Benchmarklarni ishga tushirish (PostgreSQL testlari `localhost:5432` dagi bazaga ulanadi, ulanish satri: `benchmarks/Qryx.Benchmarks/Services/PostgresBenchmarkHelper.cs`):
```bash
# Barcha testlarni (Construction, InMemory, PostgreSQL, Concurrency, Pagination, Unique values, Auto Prepare) ishga tushirish:
dotnet run -c Release --project benchmarks/Qryx.Benchmarks/Qryx.Benchmarks.csproj -- --fast

# Faqat "har so'rovda yangi qiymat" (real API trafigi) testi:
dotnet run -c Release --project benchmarks/Qryx.Benchmarks/Qryx.Benchmarks.csproj -- --unique

# Faqat Npgsql Auto Prepare (prepared statements) testi:
dotnet run -c Release --project benchmarks/Qryx.Benchmarks/Qryx.Benchmarks.csproj -- --prepare

# Faqat Real PostgreSQL Docker bazasini testlash:
dotnet run -c Release --project benchmarks/Qryx.Benchmarks/Qryx.Benchmarks.csproj -- --postgres

# Rasmiy BenchmarkDotNet testlari:
dotnet run -c Release --project benchmarks/Qryx.Benchmarks/Qryx.Benchmarks.csproj -- --bdn
```

---

## 🤝 Hissa qo'shish

Hissa qo'shishingizdan xursandmiz: xato haqida xabar, hujjatlar (jumladan qo'llanmani ingliz tiliga tarjima qilish), testlar va yangi imkoniyatlar. Pull request ochishdan oldin [CONTRIBUTING.md](CONTRIBUTING.md) ni o'qing va [Xulq-atvor qoidalari](CODE_OF_CONDUCT.md) ga amal qiling.

- 📝 Versiyalar bo'yicha o'zgarishlar: [CHANGELOG.md](CHANGELOG.md)
- 🔒 Xavfsizlik zaifliklarini ochiq issue'da emas, [SECURITY.md](SECURITY.md) da ko'rsatilgandek yopiq tarzda xabar qiling.

## 📄 Litsenziya

Qryx [MIT litsenziyasi](LICENSE) ostida tarqatiladi.
