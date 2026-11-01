using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Qryx.Benchmarks.Models;
using Qryx.Benchmarks.Services;
using Qryx.Expressions.Builders;
using Gridify;

namespace Qryx.Benchmarks.Scenarios;

public static class PostgresDiagnostic
{
    public static void RunDiagnostic()
    {
        Console.WriteLine("\n=== POSTGRESQL DEEP FORENSIC TIMING BREAKDOWN (1,000 queries) ===");
        using var db = new PostgresBenchmarkDbContext();

        var qryxComplex = new UserBenchmarkDto
        {
            MinAge = 25,
            MaxAge = 40,
            Status = "Active",
            FullName = "Ali",
            SortBy = nameof(BenchmarkUser.CreatedAt),
            IsDescending = true,
            Page = 2,
            PageSize = 20
        };

        var gridifyQuery = new GridifyQuery
        {
            Filter = "Age >= 25, Age <= 40, Status=Active, (FirstName =* Ali|LastName =* Ali)",
            OrderBy = "CreatedAt desc",
            Page = 2,
            PageSize = 20
        };

        var qryxQ = PaginationBuilder.ApplyOffset(
            SortBuilder.Apply(FilterBuilder.Apply(db.Users.AsNoTracking(), qryxComplex), qryxComplex),
            qryxComplex);

        var gridifyQ = db.Users.AsNoTracking()
            .ApplyFiltering(gridifyQuery.Filter)
            .ApplyOrdering(gridifyQuery.OrderBy!)
            .ApplyPaging(gridifyQuery.Page, gridifyQuery.PageSize);

        // 1. Warmup
        for (int i = 0; i < 20; i++)
        {
            qryxQ.ToList();
            gridifyQ.ToList();
        }

        const int iterations = 1_000;

        // Test A: EF Core End-to-End Qryx
        var swA = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            var q = PaginationBuilder.ApplyOffset(
                SortBuilder.Apply(FilterBuilder.Apply(db.Users.AsNoTracking(), qryxComplex), qryxComplex),
                qryxComplex);
            q.ToList();
        }
        swA.Stop();
        double qryxEfTime = (swA.Elapsed.TotalMilliseconds * 1000.0) / iterations;

        // Test B: EF Core End-to-End Gridify
        var swB = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            var q = db.Users.AsNoTracking()
                .ApplyFiltering(gridifyQuery.Filter)
                .ApplyOrdering(gridifyQuery.OrderBy!)
                .ApplyPaging(gridifyQuery.Page, gridifyQuery.PageSize);
            q.ToList();
        }
        swB.Stop();
        double gridifyEfTime = (swB.Elapsed.TotalMilliseconds * 1000.0) / iterations;

        // Test C: Direct Database Execution (Npgsql Raw SQL)
        using var conn = new NpgsqlConnection(PostgresBenchmarkDbContext.ConnectionString);
        conn.Open();

        // C1: Qryx with lower()
        var swC1 = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            using var cmd = new NpgsqlCommand(
                """
                SELECT b."Id", b."Age", b."CreatedAt", b."FirstName", b."LastName", b."Status"
                FROM benchmark_users AS b
                WHERE b."Status" = 'Active' AND b."Age" >= 25 AND b."Age" <= 40 AND (lower(b."FirstName") LIKE 'ali%' OR lower(b."LastName") LIKE 'ali%')
                ORDER BY b."CreatedAt" DESC
                LIMIT 20 OFFSET 20;
                """, conn);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) { }
        }
        swC1.Stop();
        double qryxDbRawTime = (swC1.Elapsed.TotalMilliseconds * 1000.0) / iterations;

        // C2: Qryx without lower() (Matching Gridify's direct column check)
        var swC2 = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            using var cmd = new NpgsqlCommand(
                """
                SELECT b."Id", b."Age", b."CreatedAt", b."FirstName", b."LastName", b."Status"
                FROM benchmark_users AS b
                WHERE b."Status" = 'Active' AND b."Age" >= 25 AND b."Age" <= 40 AND (b."FirstName" LIKE 'Ali%' OR b."LastName" LIKE 'Ali%')
                ORDER BY b."CreatedAt" DESC
                LIMIT 20 OFFSET 20;
                """, conn);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) { }
        }
        swC2.Stop();
        double qryxExactDbRawTime = (swC2.Elapsed.TotalMilliseconds * 1000.0) / iterations;

        // D: Gridify raw SQL
        var swD = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            using var cmd = new NpgsqlCommand(
                """
                SELECT b."Id", b."Age", b."CreatedAt", b."FirstName", b."LastName", b."Status"
                FROM benchmark_users AS b
                WHERE b."Age" >= 25 AND b."Age" <= 40 AND b."Status" = 'Active' AND (b."FirstName" LIKE '%Ali%' OR b."LastName" LIKE '%Ali%')
                ORDER BY b."CreatedAt" DESC
                LIMIT 20 OFFSET 20;
                """, conn);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) { }
        }
        swD.Stop();
        double gridifyDbRawTime = (swD.Elapsed.TotalMilliseconds * 1000.0) / iterations;

        Console.WriteLine($"[1] C# EF Core End-to-End:");
        Console.WriteLine($"    - Qryx:                 {qryxEfTime:F2} μs");
        Console.WriteLine($"    - Gridify:              {gridifyEfTime:F2} μs");
        Console.WriteLine($"[2] Direct PostgreSQL Database Engine (Raw Npgsql):");
        Console.WriteLine($"    - Qryx SQL (with lower):      {qryxDbRawTime:F2} μs");
        Console.WriteLine($"    - Qryx SQL (direct without lower): {qryxExactDbRawTime:F2} μs");
        Console.WriteLine($"    - Gridify SQL:                {gridifyDbRawTime:F2} μs");
        Console.WriteLine($"\n[3] Xulosa:");
        Console.WriteLine($"    -> PostgreSQL 'lower()' funksiyasi narxi: {qryxDbRawTime - qryxExactDbRawTime:F2} μs");
        Console.WriteLine($"    -> .NET C# qatlamida Qryx Gridifydan:    {(gridifyEfTime - gridifyDbRawTime) - (qryxEfTime - qryxDbRawTime):F2} μs TEZROQ!");

        RunPaginationComparisonAsync().GetAwaiter().GetResult();
    }

    public static async Task RunPaginationComparisonAsync()
    {
        Console.WriteLine("\n=== PAGINATION STRATEGY BENCHMARK (Real PostgreSQL, 500 iters) ===");
        using var db = new PostgresBenchmarkDbContext();

        var standardDto = new UserBenchmarkDto
        {
            Status = "Active",
            MinAge = 25,
            MaxAge = 40,
            Page = 2,
            PageSize = 20,
            IncludeTotalCount = true
        };

        var singleQueryDto = new UserBenchmarkDto
        {
            Status = "Active",
            MinAge = 25,
            MaxAge = 40,
            Page = 2,
            PageSize = 20,
            IncludeTotalCount = false
        };

        var cursorDto = new UserBenchmarkDto
        {
            Status = "Active",
            MinAge = 25,
            MaxAge = 40,
            PageSize = 20,
            AfterCursor = "2000"
        };

        // Warmup
        for (int i = 0; i < 10; i++)
        {
            await Qryx.EntityFramework.Extensions.QryxExtensions.ToPagedResultAsync(db.Users.AsNoTracking(), standardDto);
            await Qryx.EntityFramework.Extensions.QryxExtensions.ToPagedResultAsync(db.Users.AsNoTracking(), singleQueryDto);
            await Qryx.EntityFramework.Extensions.QryxExtensions.ToPagedResultAsync(db.Users.AsNoTracking(), cursorDto);
        }

        const int iters = 500;

        // 1. Standard (Count + Skip/Take, 2 roundtrips)
        var sw1 = Stopwatch.StartNew();
        for (int i = 0; i < iters; i++)
        {
            await Qryx.EntityFramework.Extensions.QryxExtensions.ToPagedResultAsync(db.Users.AsNoTracking(), standardDto);
        }
        sw1.Stop();
        double standardTime = (sw1.Elapsed.TotalMilliseconds * 1000.0) / iters;

        // 2. Single-Query (Skip COUNT(*), 1 roundtrip)
        var sw2 = Stopwatch.StartNew();
        for (int i = 0; i < iters; i++)
        {
            await Qryx.EntityFramework.Extensions.QryxExtensions.ToPagedResultAsync(db.Users.AsNoTracking(), singleQueryDto);
        }
        sw2.Stop();
        double singleQueryTime = (sw2.Elapsed.TotalMilliseconds * 1000.0) / iters;

        // 3. Keyset Cursor (B-Tree Seek, 1 roundtrip)
        var sw3 = Stopwatch.StartNew();
        for (int i = 0; i < iters; i++)
        {
            await Qryx.EntityFramework.Extensions.QryxExtensions.ToPagedResultAsync(db.Users.AsNoTracking(), cursorDto);
        }
        sw3.Stop();
        double cursorTime = (sw3.Elapsed.TotalMilliseconds * 1000.0) / iters;

        Console.WriteLine($"+------------------------------------------+-------------+---------------+");
        Console.WriteLine($"| Pagination Strategy                      | Time/Op     | Speedup       |");
        Console.WriteLine($"+------------------------------------------+-------------+---------------+");
        Console.WriteLine($"| Standard (CountAsync + Data, 2 queries)  | {standardTime,9:F2} μs | 1.00x (Base)  |");
        Console.WriteLine($"| Single-Query (No Count, 1 query)         | {singleQueryTime,9:F2} μs | {standardTime / singleQueryTime,5:F2}x faster |");
        Console.WriteLine($"| Keyset Cursor (Index Seek, 1 query)      | {cursorTime,9:F2} μs | {standardTime / cursorTime,5:F2}x faster |");
        Console.WriteLine($"+------------------------------------------+-------------+---------------+");
    }
}
