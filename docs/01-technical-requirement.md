# Texnik Vazifa (Technical Specification)

**Loyiha nomi:** Qryx (`/krɪks/`)

**Turi:** Parser-free, DTO-Centric, Strongly-Typed Dynamic Query Engine

**Platforma:** .NET 10 / C# 14

**Maqsad:** REST API orqali keladigan so'rovlarni (filtr, saralash, sahifalash) matnli parserlarsiz, to'g'ridan-to'g'ri DTO meta-ma'lumotlari orqali xavfsiz va yuqori unumdorlikka ega `IQueryable` Expression Tree'larga aylantirish.

---

## 1. Tizim Arxitekturasi va Modullar Tuzilishi

Loyiha qat'iy ajratilgan 5 ta NuGet paketidan iborat:

```
src/
├── Qryx.Core/              # Interfeyslar, Range<T>, [Filter] atributi, Op enum (0 dependencies)
├── Qryx.Expressions/       # Expression Tree Builder, Compiled Delegate Cache, Reflection Inspector
├── Qryx.EntityFramework/   # EF Core uchun IQueryable extension'lar, ToPagedResultAsync
├── Qryx.AspNetCore/        # ModelBinder, Minimal API / Controller integratsiyasi
└── Qryx.OpenApi/           # Swagger / Microsoft.AspNetCore.OpenApi sxema kengaytmalari

```

---

## 2. Asosiy Shartnomalar va Tiplar (Qryx.Core)

### 2.1. Operatorlar To'plami (`Op` Enum)

```csharp
namespace Qryx.Core.Enums;

public enum Op
{
    Equals,
    NotEquals,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    StartsWith,
    EndsWith,
    Contains,
    In,
    NotIn,
    Between,
    IsNull,
    IsNotNull
}

```

### 2.2. Oraliqlar Modeli (`Range<T>`)

Bitta oraliq maydoni (sana, son, vaqt) uchun unifikatsiyalangan qiymat turi.

```csharp
namespace Qryx.Core.Types;

public readonly record struct Range<T> where T : struct, IComparable<T>
{
    public T? From { get; init; }
    public T? To { get; init; }

    public bool HasValue => From.HasValue || To.HasValue;
}

```

### 2.3. Asosiy So'rov Shartnomasi (`IQryxRequest<TEntity>`)

Har bir DTO barcha zaruriy parametrlarni (sahifalash, saralash) o'zida jamlaydi.

```csharp
namespace Qryx.Core.Contracts;

public interface IQryxRequest<TEntity>
{
    int Page { get; init; }
    int PageSize { get; init; }
    object? AfterCursor { get; init; }
    string? SortBy { get; init; }
    bool IsDescending { get; init; }
}

public abstract record QryxRequest<TEntity> : IQryxRequest<TEntity>
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
    public object? AfterCursor { get; init; }
    public string? SortBy { get; init; }
    public bool IsDescending { get; init; } = false;
}

```

### 2.4. Deklarativ Atribut (`[Filter]`)

```csharp
namespace Qryx.Core.Attributes;

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class FilterAttribute : Attribute
{
    public string TargetProperty { get; }
    public Op Operator { get; }
    public bool CaseSensitive { get; init; } = false;

    public FilterAttribute(string targetProperty, Op @operator)
    {
        TargetProperty = targetProperty;
        Operator = @operator;
    }
}

```

---

## 3. Dvigatel Ishlash Logikasi (Qryx.Expressions)

### 3.1. DTO Metadata Inspector

1. DTO tipi dastlabki murojaatda tekshiriladi (`Reflection`).
2. Har bir `[Filter]` atributli property uchun quyidagi metadata obyektga yig'iladi:
* Property nomi va tipi.
* Maqsadli Entity maydoni (shu jumladan navigation property: `Department.Company.Id`).
* Operator turi (`Op`).


3. Ushbu metadata `ConcurrentDictionary<Type, DtoMetadata>` ichida statik keshlanadi.

### 3.2. Expression Tree Qurish Algoritmi

Har bir so'rov kelganda:

1. Keshdan DTO metadatasi olinadi.
2. DTO nusxasidan faqat qiymati mavjud (`!= null`, `Range.HasValue == true`) maydonlar filtrlanadi.
3. Har bir maydon uchun `ParameterExpression (x => ...)` asosida binar solishtirish yaratiladi:
* **`Op.Equals` / `Op.NotEquals**`: `Expression.Equal` / `Expression.NotEqual`.
* **`Op.Between`**:
* Agar `From` va `To` bo'lsa: `x.Prop >= From && x.Prop <= To`
* Agar faqat `From` bo'lsa: `x.Prop >= From`
* Agar faqat `To` bo'lsa: `x.Prop <= To`


* **`Op.StartsWith` / `EndsWith` / `Contains**`: String metodlari chaqiruviga bog'lanadi (`EF.Functions.ILike` yoki `string.StartsWith`).
* **`Op.In`**: `Enumerable.Contains` ifodasi yasaladi.


4. Barcha hosil bo'lgan ifodalar `Expression.AndAlso` orqali zanjirlanadi.
5. Yakuniy predikat `query.Where(combinedLambda)` ga beriladi.

### 3.3. Saralash va Sahifalash Logikasi

* **Sorting:** DTO'dagi `SortBy` qiymati Entity'dagi mavjud public property bilan solishtiriladi (Whitelist tekshiruvi). Ruxsatsiz yoki mavjud bo'lmagan maydon kelsa, xato o'rniga default tartib saqlanadi yoki 400 qaytariladi.
* **Pagination:**
* **Offset:** `query.Skip((Page - 1) * PageSize).Take(PageSize)`.
* **Keyset:** `query.Where(x => x.Id > AfterCursor).OrderBy(x => x.Id).Take(PageSize)`.



---

## 4. Entity Framework Core Integratsiyasi (Qryx.EntityFramework)

### 4.1. Natija Modeli (`PagedResult<T>`)

```csharp
namespace Qryx.EntityFramework.Models;

public record PagedResult<T>
{
    public IReadOnlyList<T> Items { get; init; } = [];
    public int TotalCount { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling(TotalCount / (double)PageSize) : 0;
    public bool HasPrevious => Page > 1;
    public bool HasNext => Page < TotalPages;
}

```

### 4.2. Extension Metodlar

```csharp
public static class QryxExtensions
{
    public static IQueryable<TEntity> ApplyQryx<TEntity>(
        this IQueryable<TEntity> source,
        IQryxRequest<TEntity> request)
    {
        source = FilterBuilder.Apply(source, request);
        source = SortBuilder.Apply(source, request);
        return source;
    }

    public static async Task<PagedResult<TEntity>> ToPagedResultAsync<TEntity>(
        this IQueryable<TEntity> source,
        IQryxRequest<TEntity> request,
        CancellationToken ct = default)
    {
        // 1. Filtr va saralashni qo'llash
        var filteredQuery = source.ApplyQryx(request);

        // 2. Jami qatorlar sonini olish
        var totalCount = await filteredQuery.CountAsync(ct);

        // 3. Sahifalashni qo'llash va ma'lumotni yuklash
        var items = await filteredQuery
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync(ct);

        return new PagedResult<TEntity>
        {
            Items = items,
            TotalCount = totalCount,
            Page = request.Page,
            PageSize = request.PageSize
        };
    }
}

```

---

## 5. Dasturchi Tajribasi (DX Namunasi)

### 5.1. DTO E'lon Qilishi

```csharp
public record UserQueryDto : QryxRequest<User>
{
    [Filter(nameof(User.FullName), Op.StartsWith)]
    public string? Name { get; init; }

    [Filter(nameof(User.Age), Op.Between)]
    public Range<int>? Age { get; init; }

    [Filter(nameof(User.CreatedAt), Op.Between)]
    public Range<DateTime>? CreatedAt { get; init; }

    [Filter(nameof(User.Status), Op.In)]
    public List<UserStatus>? Statuses { get; init; }

    [Filter("Department.Id", Op.Equals)]
    public Guid? DepartmentId { get; init; }
}

```

### 5.2. Minimal API Integratsiyasi

```csharp
app.MapGet("/api/users", async (
    [AsParameters] UserQueryDto query,
    AppDbContext db,
    CancellationToken ct) =>
{
    var result = await db.Users
        .AsNoTracking()
        .ToPagedResultAsync(query, ct);

    return Results.Ok(result);
});

```

### 5.3. HTTP So'rov Namunasi

```http
GET /api/users?name=Ali&age.from=20&age.to=35&statuses=Active&statuses=Pending&sortBy=CreatedAt&isDescending=true&page=1&pageSize=15

```

---

## 6. Nofunksional Talablar (NFR)

| Talab | Standart / Cheklov |
| --- | --- |
| **Xotira (Allocations)** | DTO property tekshiruvi paytida heap allocation minimal bo'lishi shart. Reflection faqat birinchi so'rovda ishlaydi, qolgan so'rovlarda keshdagi ifodalar ishlatiladi. |
| **DoS Guard** | `PageSize` uchun qat'iy maksimal chegara: DTO orqali 100 dan ortiq yuborilsa ham, maksimal 100 ga qirqiladi (`Math.Min(PageSize, 100)`). |
| **SQL Injection** | Barcha filtr qiymatlari EF Core parametrlariga aylanadi (`@p0`, `@p1`). Satrlarni to'g'ridan-to'g'ri SQL ga ulash qat'iyan taqiqlanadi. |
| **Index-Friendly** | `Between` va taqqoslash amallari doimo B-Tree indekslarini qo'llab-quvvatlaydigan `>=` va `<=` shartlariga tarjima qilinadi. |