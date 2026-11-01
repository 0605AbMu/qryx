using System.Diagnostics;
using System.Linq.Expressions;
using Gridify;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Qryx.Benchmarks.Models;
using Qryx.Benchmarks.Services;
using Qryx.EntityFramework.Extensions;
using Qryx.Expressions.Builders;
using Qryx.Generated;
using Sieve.Models;

namespace Qryx.Benchmarks.Scenarios;

public static class FastDiagnosticRunner
{
    public static void RunAll()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("=========================================================================================");
        Console.WriteLine("        QRYX vs GRIDIFY vs SIEVE vs ODATA - IN-DEPTH PERFORMANCE COMPARISON");
        Console.WriteLine("=========================================================================================");
        Console.ResetColor();

        RunConstructionSuite();
        RunExecutionSuite();
        RunPostgresExecutionSuite();
        RunConcurrencyStressSuite();
        RunPaginationStrategiesSuite();
    }

    public static void RunConstructionSuite()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("\n[1/3] BENCHMARK: Query Construction & AST/Expression Tree Building (50,000 iterations)");
        Console.WriteLine("      (Measures library overhead: CPU time, Gen 0/1/2 pressure, and allocated heap bytes)");
        Console.ResetColor();

        var dummyQuery = new List<BenchmarkUser>().AsQueryable();

        var sieveOptions = Options.Create(new SieveOptions());
        var sieveProcessor = new BenchmarkSieveProcessor(sieveOptions, new BenchmarkSieveCustomFilterMethods());

        var (context, sp) = BenchmarkDataHelper.CreateODataContext();

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

        var sieveModel = new SieveModel
        {
            Filters = "Age>=25,Age<=40,Status==Active,FullName==Ali",
            Sorts = "-CreatedAt",
            Page = 2,
            PageSize = 20
        };

        const string odataComplex = "?$filter=Age ge 25 and Age le 40 and Status eq 'Active' and (startswith(FirstName, 'Ali') or startswith(LastName, 'Ali'))&$orderby=CreatedAt desc&$skip=20&$top=20";

        // Warmup
        for (int i = 0; i < 500; i++)
        {
            var q = FilterBuilder.Apply(dummyQuery, qryxComplex);
            q = SortBuilder.Apply(q, qryxComplex);
            PaginationBuilder.ApplyOffset(q, qryxComplex);

            var gq = dummyQuery.ApplyFiltering(gridifyQuery.Filter).ApplyOrdering(gridifyQuery.OrderBy).ApplyPaging(gridifyQuery.Page, gridifyQuery.PageSize);
            sieveProcessor.Apply(sieveModel, dummyQuery);
            BenchmarkDataHelper.CreateODataOptions(context, sp, odataComplex).ApplyTo(dummyQuery);
        }

        const int iterations = 50_000;

        // Test Qryx Source Generator (Roslyn Compile-time, 0.00 ns Reflection)
        var qryxSourceGenMetric = MeasureMetric("Qryx (Roslyn Source Generator)", iterations, () =>
        {
            return dummyQuery.ApplyGenerated(qryxComplex);
        });

        // Test Qryx
        var qryxMetric = MeasureMetric("Qryx (DTO Compiled Delegates)", iterations, () =>
        {
            var q = FilterBuilder.Apply(dummyQuery, qryxComplex);
            q = SortBuilder.Apply(q, qryxComplex);
            return PaginationBuilder.ApplyOffset(q, qryxComplex);
        });

        // Test Gridify
        var gridifyMetric = MeasureMetric("Gridify (String Lexer/Parser)", iterations, () =>
        {
            var q = dummyQuery.ApplyFiltering(gridifyQuery.Filter);
            q = q.ApplyOrdering(gridifyQuery.OrderBy);
            return q.ApplyPaging(gridifyQuery.Page, gridifyQuery.PageSize);
        });

        // Test Sieve
        var sieveMetric = MeasureMetric("Sieve (String Tokenizer/Mapper)", iterations, () =>
        {
            return sieveProcessor.Apply(sieveModel, dummyQuery);
        });

        // Test OData
        var odataMetric = MeasureMetric("OData (EDM Model + URI Parser)", iterations, () =>
        {
            var opt = BenchmarkDataHelper.CreateODataOptions(context, sp, odataComplex);
            return opt.ApplyTo(dummyQuery);
        });

        PrintResultsTable([qryxSourceGenMetric, qryxMetric, gridifyMetric, sieveMetric, odataMetric]);
    }

    public static void RunExecutionSuite()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("\n[2/3] BENCHMARK: End-to-End Query Execution against EF Core InMemory (10,000 Entities, 5,000 iters)");
        Console.WriteLine("      (Measures real query evaluation, filtering, sorting, paging and materialization .ToList())");
        Console.ResetColor();

        var users = BenchmarkDataHelper.GenerateUsers(10_000);
        using var db = BenchmarkDataHelper.CreateInMemoryDbContext(users);

        var sieveOptions = Options.Create(new SieveOptions());
        var sieveProcessor = new BenchmarkSieveProcessor(sieveOptions, new BenchmarkSieveCustomFilterMethods());

        var (context, sp) = BenchmarkDataHelper.CreateODataContext();

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

        var sieveModel = new SieveModel
        {
            Filters = "Age>=25,Age<=40,Status==Active,FullName==Ali",
            Sorts = "-CreatedAt",
            Page = 2,
            PageSize = 20
        };

        const string odataComplex = "?$filter=Age ge 25 and Age le 40 and Status eq 'Active' and (startswith(FirstName, 'Ali') or startswith(LastName, 'Ali'))&$orderby=CreatedAt desc&$skip=20&$top=20";

        var qryxTest = PaginationBuilder.ApplyOffset(SortBuilder.Apply(FilterBuilder.Apply(db.Users.AsNoTracking(), qryxComplex), qryxComplex), qryxComplex).ToList();
        var gridifyTest = db.Users.AsNoTracking().ApplyFiltering(gridifyQuery.Filter).ApplyOrdering(gridifyQuery.OrderBy!).ApplyPaging(gridifyQuery.Page, gridifyQuery.PageSize).ToList();
        var sieveTest = sieveProcessor.Apply(sieveModel, db.Users.AsNoTracking()).ToList();
        var optTest = BenchmarkDataHelper.CreateODataOptions(context, sp, odataComplex);
        var odataTest = ((IQueryable<BenchmarkUser>)optTest.ApplyTo(db.Users.AsNoTracking())).ToList();
        Console.WriteLine($"      [Verification] Items returned: Qryx={qryxTest.Count}, Gridify={gridifyTest.Count}, Sieve={sieveTest.Count}, OData={odataTest.Count}");

        // Warmup all frameworks equally to ensure JIT & EF query compiler cache readiness
        for (int i = 0; i < 20; i++)
        {
            db.Users.AsNoTracking().ApplyGenerated(qryxComplex).ToList();
            var q = FilterBuilder.Apply(db.Users.AsNoTracking(), qryxComplex);
            q = SortBuilder.Apply(q, qryxComplex);
            PaginationBuilder.ApplyOffset(q, qryxComplex).ToList();
            db.Users.AsNoTracking().ApplyFiltering(gridifyQuery.Filter).ApplyOrdering(gridifyQuery.OrderBy).ApplyPaging(gridifyQuery.Page, gridifyQuery.PageSize).ToList();
            sieveProcessor.Apply(sieveModel, db.Users.AsNoTracking()).ToList();
            var opt = BenchmarkDataHelper.CreateODataOptions(context, sp, odataComplex);
            ((IQueryable<BenchmarkUser>)opt.ApplyTo(db.Users.AsNoTracking())).ToList();
        }

        const int iterations = 5_000;

        var qryxSourceGenMetric = MeasureMetric("Qryx (Source Generator LINQ)", iterations, () =>
        {
            return db.Users.AsNoTracking().ApplyGenerated(qryxComplex).ToList();
        });

        var qryxMetric = MeasureMetric("Qryx (Execute & Materialize)", iterations, () =>
        {
            var q = FilterBuilder.Apply(db.Users.AsNoTracking(), qryxComplex);
            q = SortBuilder.Apply(q, qryxComplex);
            return PaginationBuilder.ApplyOffset(q, qryxComplex).ToList();
        });

        var gridifyMetric = MeasureMetric("Gridify (Execute & Materialize)", iterations, () =>
        {
            var q = db.Users.AsNoTracking().ApplyFiltering(gridifyQuery.Filter);
            q = q.ApplyOrdering(gridifyQuery.OrderBy);
            return q.ApplyPaging(gridifyQuery.Page, gridifyQuery.PageSize).ToList();
        });

        var sieveMetric = MeasureMetric("Sieve (Execute & Materialize)", iterations, () =>
        {
            return sieveProcessor.Apply(sieveModel, db.Users.AsNoTracking()).ToList();
        });

        var odataMetric = MeasureMetric("OData (Execute & Materialize)", iterations, () =>
        {
            var opt = BenchmarkDataHelper.CreateODataOptions(context, sp, odataComplex);
            return ((IQueryable<BenchmarkUser>)opt.ApplyTo(db.Users.AsNoTracking())).ToList();
        });

        PrintResultsTable([qryxSourceGenMetric, qryxMetric, gridifyMetric, sieveMetric, odataMetric]);
    }

    public static void RunPostgresExecutionSuite()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("\n[3/4] BENCHMARK: Real PostgreSQL Database (Docker localhost:5432, 10,000 Entities, 500 iters)");
        Console.WriteLine("      (Measures actual database roundtrip, SQL translation, B-Tree index utilization, and deserialization)");
        Console.ResetColor();

        try
        {
            PostgresBenchmarkHelper.EnsureDatabaseAndDataAsync(10_000).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"      [Postgres] Could not connect to PostgreSQL: {ex.Message}. Skipping Postgres suite.");
            Console.ResetColor();
            return;
        }

        using var db = new PostgresBenchmarkDbContext();

        var sieveOptions = Options.Create(new SieveOptions());
        var sieveProcessor = new BenchmarkSieveProcessor(sieveOptions, new BenchmarkSieveCustomFilterMethods());

        var (context, sp) = BenchmarkDataHelper.CreateODataContext();

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

        var sieveModel = new SieveModel
        {
            Filters = "Age>=25,Age<=40,Status==Active,FullName==Ali",
            Sorts = "-CreatedAt",
            Page = 2,
            PageSize = 20
        };

        const string odataComplex = "?$filter=Age ge 25 and Age le 40 and Status eq 'Active' and (startswith(FirstName, 'Ali') or startswith(LastName, 'Ali'))&$orderby=CreatedAt desc&$skip=20&$top=20";

        var qryxCursorDto = new UserBenchmarkDto
        {
            MinAge = 25,
            MaxAge = 40,
            Status = "Active",
            FullName = "Ali",
            PageSize = 20,
            AfterCursor = "2000"
        };

        var qryxTest = PaginationBuilder.ApplyOffset(SortBuilder.Apply(FilterBuilder.Apply(db.Users.AsNoTracking(), qryxComplex), qryxComplex), qryxComplex).ToList();
        var gridifyTest = db.Users.AsNoTracking().ApplyFiltering(gridifyQuery.Filter).ApplyOrdering(gridifyQuery.OrderBy!).ApplyPaging(gridifyQuery.Page, gridifyQuery.PageSize).ToList();
        var sieveTest = sieveProcessor.Apply(sieveModel, db.Users.AsNoTracking()).ToList();
        var optTest = BenchmarkDataHelper.CreateODataOptions(context, sp, odataComplex);
        var odataTest = ((IQueryable<BenchmarkUser>)optTest.ApplyTo(db.Users.AsNoTracking())).ToList();
        Console.WriteLine($"      [Postgres Verification] Items returned: Qryx={qryxTest.Count}, Gridify={gridifyTest.Count}, Sieve={sieveTest.Count}, OData={odataTest.Count}");

        // Warmup connection pool
        for (int i = 0; i < 20; i++)
        {
            PaginationBuilder.ApplyOffset(SortBuilder.Apply(FilterBuilder.Apply(db.Users.AsNoTracking(), qryxComplex), qryxComplex), qryxComplex).ToList();
            db.Users.AsNoTracking().ToPagedResultAsync(qryxCursorDto, x => new UserSummaryDto(x.Id, x.FirstName, x.Age, x.Status)).GetAwaiter().GetResult();
        }

        const int iterations = 500;

        // 1. Qryx Max: Keyset Cursor Seek + Projection Pushdown
        var qryxPeakMetric = MeasureMetric("Qryx (Max: Keyset + Projection)", iterations, () =>
        {
            return db.Users.AsNoTracking().ToPagedResultAsync(qryxCursorDto, x => new UserSummaryDto(x.Id, x.FirstName, x.Age, x.Status)).GetAwaiter().GetResult();
        });

        // 2. Qryx Max: Keyset Cursor Seek (Full Entity)
        var qryxKeysetMetric = MeasureMetric("Qryx (Max: Keyset Index Seek)", iterations, () =>
        {
            return db.Users.AsNoTracking().ToPagedResultAsync(qryxCursorDto).GetAwaiter().GetResult();
        });

        // 3. Qryx Source Generator (Compiled LINQ)
        var qryxSourceGenMetric = MeasureMetric("Qryx (Source Generator LINQ)", iterations, () =>
        {
            return db.Users.AsNoTracking().ApplyGenerated(qryxComplex).ToList();
        });

        // 4. Qryx Standard: Offset Pagination
        var qryxMetric = MeasureMetric("Qryx (Standard: Dynamic AST)", iterations, () =>
        {
            var q = FilterBuilder.Apply(db.Users.AsNoTracking(), qryxComplex);
            q = SortBuilder.Apply(q, qryxComplex);
            return PaginationBuilder.ApplyOffset(q, qryxComplex).ToList();
        });

        // 5. Gridify
        var gridifyMetric = MeasureMetric("Gridify (Postgres Real DB)", iterations, () =>
        {
            var q = db.Users.AsNoTracking().ApplyFiltering(gridifyQuery.Filter);
            q = q.ApplyOrdering(gridifyQuery.OrderBy!);
            return q.ApplyPaging(gridifyQuery.Page, gridifyQuery.PageSize).ToList();
        });

        // 6. Sieve
        var sieveMetric = MeasureMetric("Sieve (Postgres Real DB)", iterations, () =>
        {
            return sieveProcessor.Apply(sieveModel, db.Users.AsNoTracking()).ToList();
        });

        // 7. OData
        var odataMetric = MeasureMetric("OData (Postgres Real DB)", iterations, () =>
        {
            var opt = BenchmarkDataHelper.CreateODataOptions(context, sp, odataComplex);
            return ((IQueryable<BenchmarkUser>)opt.ApplyTo(db.Users.AsNoTracking())).ToList();
        });

        PrintResultsTable([qryxPeakMetric, qryxKeysetMetric, qryxSourceGenMetric, qryxMetric, gridifyMetric, sieveMetric, odataMetric]);
    }

    public static void RunConcurrencyStressSuite()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("\n[4/4] BENCHMARK: Multi-Threaded Stress Test (Parallel.For, 100,000 Operations)");
        Console.WriteLine("      (Measures thread contention, metadata cache efficiency and lock-free concurrency)");
        Console.ResetColor();

        var dummyQuery = new List<BenchmarkUser>().AsQueryable();
        var sieveOptions = Options.Create(new SieveOptions());
        var sieveProcessor = new BenchmarkSieveProcessor(sieveOptions, new BenchmarkSieveCustomFilterMethods());
        var (context, sp) = BenchmarkDataHelper.CreateODataContext();

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

        var sieveModel = new SieveModel
        {
            Filters = "Age>=25,Age<=40,Status==Active,FullName==Ali",
            Sorts = "-CreatedAt",
            Page = 2,
            PageSize = 20
        };

        const string odataComplex = "?$filter=Age ge 25 and Age le 40 and Status eq 'Active' and (startswith(FirstName, 'Ali') or startswith(LastName, 'Ali'))&$orderby=CreatedAt desc&$skip=20&$top=20";

        const int totalParallelOps = 100_000;

        var qryxSourceGenMetric = MeasureParallel("Qryx (Source Gen Parallel)", totalParallelOps, () =>
        {
            dummyQuery.ApplyGenerated(qryxComplex);
        });

        var qryxMetric = MeasureParallel("Qryx (Parallel)", totalParallelOps, () =>
        {
            var q = FilterBuilder.Apply(dummyQuery, qryxComplex);
            q = SortBuilder.Apply(q, qryxComplex);
            PaginationBuilder.ApplyOffset(q, qryxComplex);
        });

        var gridifyMetric = MeasureParallel("Gridify (Parallel)", totalParallelOps, () =>
        {
            var q = dummyQuery.ApplyFiltering(gridifyQuery.Filter);
            q = q.ApplyOrdering(gridifyQuery.OrderBy);
            q.ApplyPaging(gridifyQuery.Page, gridifyQuery.PageSize);
        });

        var sieveMetric = MeasureParallel("Sieve (Parallel)", totalParallelOps, () =>
        {
            sieveProcessor.Apply(sieveModel, dummyQuery);
        });

        var odataMetric = MeasureParallel("OData (Parallel)", totalParallelOps, () =>
        {
            var opt = BenchmarkDataHelper.CreateODataOptions(context, sp, odataComplex);
            opt.ApplyTo(dummyQuery);
        });

        PrintResultsTable([qryxSourceGenMetric, qryxMetric, gridifyMetric, sieveMetric, odataMetric]);
    }

    public static void RunPaginationStrategiesSuite()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("\n[5/5] BENCHMARK: PostgreSQL Pagination Strategies (Docker localhost:5432, 500 iters)");
        Console.WriteLine("      (Measures impact of Single-Query, Keyset Cursor, and Zero-Copy Streaming vs Standard)");
        Console.ResetColor();

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
            db.Users.AsNoTracking().ToPagedResultAsync(standardDto).GetAwaiter().GetResult();
            db.Users.AsNoTracking().ToPagedResultAsync(singleQueryDto).GetAwaiter().GetResult();
            db.Users.AsNoTracking().ToPagedResultAsync(cursorDto).GetAwaiter().GetResult();
        }

        const int iterations = 500;

        var standardMetric = MeasureMetric("Standard (Count + Data, 2 DB queries)", iterations, () =>
        {
            return db.Users.AsNoTracking().ToPagedResultAsync(standardDto).GetAwaiter().GetResult();
        });

        var singleQueryMetric = MeasureMetric("Single-Query (No Count, 1 DB query)", iterations, () =>
        {
            return db.Users.AsNoTracking().ToPagedResultAsync(singleQueryDto).GetAwaiter().GetResult();
        });

        var cursorMetric = MeasureMetric("Keyset Cursor (Index Seek, 1 DB query)", iterations, () =>
        {
            return db.Users.AsNoTracking().ToPagedResultAsync(cursorDto).GetAwaiter().GetResult();
        });

        var streamMetric = MeasureMetric("Zero-Copy Stream (IAsyncEnumerable, 1 query)", iterations, () =>
        {
            var stream = db.Users.AsNoTracking().ToPagedAsyncEnumerable(singleQueryDto);
            var count = 0;
            var enumerator = stream.GetAsyncEnumerator();
            try
            {
                while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
                {
                    count++;
                }
            }
            finally
            {
                enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            return count;
        });

        PrintResultsTable([standardMetric, singleQueryMetric, cursorMetric, streamMetric]);
    }

    private static BenchmarkMetric MeasureMetric(string name, int iterations, Func<object> action)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long startBytes = GC.GetAllocatedBytesForCurrentThread();
        int g0Start = GC.CollectionCount(0);
        int g1Start = GC.CollectionCount(1);
        int g2Start = GC.CollectionCount(2);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            action();
        }
        sw.Stop();

        long totalAlloc = GC.GetAllocatedBytesForCurrentThread() - startBytes;
        int g0 = GC.CollectionCount(0) - g0Start;
        int g1 = GC.CollectionCount(1) - g1Start;
        int g2 = GC.CollectionCount(2) - g2Start;

        double totalMs = sw.Elapsed.TotalMilliseconds;
        double nsPerOp = (totalMs * 1_000_000.0) / iterations;
        double opsPerSec = (iterations / sw.Elapsed.TotalSeconds);
        long bytesPerOp = totalAlloc / iterations;

        return new BenchmarkMetric(name, nsPerOp, opsPerSec, bytesPerOp, g0, g1, g2);
    }

    private static BenchmarkMetric MeasureParallel(string name, int totalOps, Action action)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long startMemory = GC.GetTotalMemory(true);
        int g0Start = GC.CollectionCount(0);
        int g1Start = GC.CollectionCount(1);
        int g2Start = GC.CollectionCount(2);

        var sw = Stopwatch.StartNew();
        Parallel.For(0, totalOps, _ =>
        {
            action();
        });
        sw.Stop();

        long endMemory = GC.GetTotalMemory(false);
        long allocatedApprox = Math.Max(0, endMemory - startMemory);

        int g0 = GC.CollectionCount(0) - g0Start;
        int g1 = GC.CollectionCount(1) - g1Start;
        int g2 = GC.CollectionCount(2) - g2Start;

        double totalMs = sw.Elapsed.TotalMilliseconds;
        double nsPerOp = (totalMs * 1_000_000.0) / totalOps;
        double opsPerSec = (totalOps / sw.Elapsed.TotalSeconds);
        long bytesPerOp = allocatedApprox / totalOps;

        return new BenchmarkMetric(name, nsPerOp, opsPerSec, bytesPerOp, g0, g1, g2);
    }

    private static void PrintResultsTable(List<BenchmarkMetric> metrics)
    {
        var baseline = metrics.First();

        Console.WriteLine("+----------------------------------+-------------+---------------+--------------+------------+");
        Console.WriteLine("| Framework                        | Time/Op     | Speed Ratio   | Ops / Sec    | Allocated  |");
        Console.WriteLine("+----------------------------------+-------------+---------------+--------------+------------+");

        foreach (var m in metrics)
        {
            string timeStr = m.NsPerOp < 1000
                ? $"{m.NsPerOp:F1} ns"
                : m.NsPerOp < 1_000_000
                    ? $"{m.NsPerOp / 1000.0:F2} μs"
                    : $"{m.NsPerOp / 1_000_000.0:F2} ms";

            double ratio = m.NsPerOp / baseline.NsPerOp;
            string ratioStr = Math.Abs(ratio - 1.0) < 0.05 ? "1.00x (Base)" : $"{ratio:F2}x slower";
            if (ratio < 0.95) ratioStr = $"{1.0 / ratio:F2}x faster";

            string opsStr = $"{m.OpsPerSec:N0}/s";
            string allocStr = m.BytesPerOp < 1024 ? $"{m.BytesPerOp} B/op" : $"{m.BytesPerOp / 1024.0:F1} KB/op";

            if (m == baseline)
            {
                Console.ForegroundColor = ConsoleColor.Green;
            }
            else if (ratio > 2.0)
            {
                Console.ForegroundColor = ConsoleColor.White;
            }
            else
            {
                Console.ResetColor();
            }

            Console.WriteLine($"| {m.Name,-32} | {timeStr,11} | {ratioStr,13} | {opsStr,12} | {allocStr,10} |");
            Console.ResetColor();
        }

        Console.WriteLine("+----------------------------------+-------------+---------------+--------------+------------+");
    }
}

public record BenchmarkMetric(
    string Name,
    double NsPerOp,
    double OpsPerSec,
    long BytesPerOp,
    int Gen0,
    int Gen1,
    int Gen2);
