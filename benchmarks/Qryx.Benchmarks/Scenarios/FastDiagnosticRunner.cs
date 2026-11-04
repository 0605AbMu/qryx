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
        RunUniqueValuesSuite();
        RunPreparedStatementsSuite();
    }

    public static void RunConstructionSuite()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("\n[1/7] BENCHMARK: Query Construction & AST/Expression Tree Building (7 rounds x 10,000 iterations)");
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
            Filter = "Age >= 25, Age <= 40, Status=Active, (FirstName ^ Ali|LastName ^ Ali)",
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

        // 7 rounds x 10,000 iterations per framework (round order rotates)
        PrintResultsTable(MeasureInterleaved(10_000, rounds: 7, warmup: 2_000,
            // Qryx Source Generator (Roslyn compile-time)
            ("Qryx (Roslyn Source Generator)", () => dummyQuery.ApplyGenerated(qryxComplex)),
            ("Qryx (DTO Compiled Delegates)", () =>
            {
                var q = FilterBuilder.Apply(dummyQuery, qryxComplex);
                q = SortBuilder.Apply(q, qryxComplex);
                return PaginationBuilder.ApplyOffset(q, qryxComplex);
            }),
            ("Gridify (String Lexer/Parser)", () =>
            {
                var q = dummyQuery.ApplyFiltering(gridifyQuery.Filter);
                q = q.ApplyOrdering(gridifyQuery.OrderBy);
                return q.ApplyPaging(gridifyQuery.Page, gridifyQuery.PageSize);
            }),
            ("Sieve (String Tokenizer/Mapper)", () => sieveProcessor.Apply(sieveModel, dummyQuery)),
            ("OData (EDM Model + URI Parser)", () =>
            {
                var opt = BenchmarkDataHelper.CreateODataOptions(context, sp, odataComplex);
                return opt.ApplyTo(dummyQuery);
            })));
    }

    public static void RunExecutionSuite()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("\n[2/7] BENCHMARK: End-to-End Query Execution against EF Core InMemory (10,000 Entities, 7 rounds x 700 iters)");
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
            Filter = "Age >= 25, Age <= 40, Status=Active, (FirstName ^ Ali|LastName ^ Ali)",
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

        VerifyFilterParity(db.Users.AsNoTracking(), qryxComplex, gridifyQuery.Filter!, sieveModel, sieveProcessor, context, sp, odataComplex);

        // 7 rounds x 700 iterations per framework (round order rotates)
        PrintResultsTable(MeasureInterleaved(700, rounds: 7, warmup: 300,
            ("Qryx (Execute & Materialize)", () =>
            {
                var q = FilterBuilder.Apply(db.Users.AsNoTracking(), qryxComplex);
                q = SortBuilder.Apply(q, qryxComplex);
                return PaginationBuilder.ApplyOffset(q, qryxComplex).ToList();
            }),
            ("Qryx (Source Generator LINQ)", () => db.Users.AsNoTracking().ApplyGenerated(qryxComplex).ToList()),
            ("Gridify (Execute & Materialize)", () =>
            {
                var q = db.Users.AsNoTracking().ApplyFiltering(gridifyQuery.Filter);
                q = q.ApplyOrdering(gridifyQuery.OrderBy);
                return q.ApplyPaging(gridifyQuery.Page, gridifyQuery.PageSize).ToList();
            }),
            ("Sieve (Execute & Materialize)", () => sieveProcessor.Apply(sieveModel, db.Users.AsNoTracking()).ToList()),
            ("OData (Execute & Materialize)", () =>
            {
                var opt = BenchmarkDataHelper.CreateODataOptions(context, sp, odataComplex);
                return ((IQueryable<BenchmarkUser>)opt.ApplyTo(db.Users.AsNoTracking())).ToList();
            })));

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  Note: EF InMemory evaluates every query parameter per row, so a library that inlines SQL literals");
        Console.WriteLine("  (Gridify) looks faster here than on a real database. See [3/7], [6/7] and [7/7].");
        Console.ResetColor();
    }

    public static void RunPostgresExecutionSuite()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("\n[3/7] BENCHMARK: Real PostgreSQL Database (Docker localhost:5432, 10,000 Entities, 7 rounds x 70 iters)");
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
            Filter = "Age >= 25, Age <= 40, Status=Active, (FirstName ^ Ali|LastName ^ Ali)",
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
            AfterCursor = BenchmarkDataHelper.CursorAfterId(2000)
        };

        VerifyFilterParity(db.Users.AsNoTracking(), qryxComplex, gridifyQuery.Filter!, sieveModel, sieveProcessor, context, sp, odataComplex);

        // 7 rounds x 70 iterations per framework (round order rotates). Baseline = same work as competitors (offset page).
        PrintResultsTable(MeasureInterleaved(70, rounds: 7, warmup: 100,
            ("Qryx (Standard: Dynamic AST)", () =>
            {
                var q = FilterBuilder.Apply(db.Users.AsNoTracking(), qryxComplex);
                q = SortBuilder.Apply(q, qryxComplex);
                return PaginationBuilder.ApplyOffset(q, qryxComplex).ToList();
            }),
            ("Gridify (Postgres Real DB)", () =>
            {
                var q = db.Users.AsNoTracking().ApplyFiltering(gridifyQuery.Filter);
                q = q.ApplyOrdering(gridifyQuery.OrderBy!);
                return q.ApplyPaging(gridifyQuery.Page, gridifyQuery.PageSize).ToList();
            }),
            ("Sieve (Postgres Real DB)", () => sieveProcessor.Apply(sieveModel, db.Users.AsNoTracking()).ToList()),
            ("OData (Postgres Real DB)", () =>
            {
                var opt = BenchmarkDataHelper.CreateODataOptions(context, sp, odataComplex);
                return ((IQueryable<BenchmarkUser>)opt.ApplyTo(db.Users.AsNoTracking())).ToList();
            }),
            ("Qryx (Source Generator LINQ)", () => db.Users.AsNoTracking().ApplyGenerated(qryxComplex).ToList()),
            // Different (cheaper) query shape: seek after Id 2000 ordered by Id, not "ORDER BY CreatedAt OFFSET 20"
            ("Qryx (Keyset Index Seek)*", () => db.Users.AsNoTracking().ToPagedResultAsync(qryxCursorDto).GetAwaiter().GetResult()),
            ("Qryx (Keyset + Projection)*", () => db.Users.AsNoTracking()
                .ToPagedResultAsync(qryxCursorDto, x => new UserSummaryDto(x.Id, x.FirstName, x.Age, x.Status))
                .GetAwaiter().GetResult())));

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  * Keyset rows run a different query (index seek by Id, no OFFSET); compare them with each other,");
        Console.WriteLine("    not with the offset-paging rows. On 10k rows every query is dominated by the network roundtrip.");
        Console.ResetColor();
    }

    public static void RunConcurrencyStressSuite()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("\n[4/7] BENCHMARK: Multi-Threaded Stress Test (Parallel.For, 5 rounds x 20,000 Operations)");
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
            Filter = "Age >= 25, Age <= 40, Status=Active, (FirstName ^ Ali|LastName ^ Ali)",
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

        // 5 rounds x 20,000 parallel operations per framework (round order rotates)
        PrintResultsTable(MeasureParallelInterleaved(20_000, rounds: 5,
            ("Qryx (Parallel)", () =>
            {
                var q = FilterBuilder.Apply(dummyQuery, qryxComplex);
                q = SortBuilder.Apply(q, qryxComplex);
                PaginationBuilder.ApplyOffset(q, qryxComplex);
            }),
            ("Qryx (Source Gen Parallel)", () => dummyQuery.ApplyGenerated(qryxComplex)),
            ("Gridify (Parallel)", () =>
            {
                var q = dummyQuery.ApplyFiltering(gridifyQuery.Filter);
                q = q.ApplyOrdering(gridifyQuery.OrderBy);
                q.ApplyPaging(gridifyQuery.Page, gridifyQuery.PageSize);
            }),
            ("Sieve (Parallel)", () => sieveProcessor.Apply(sieveModel, dummyQuery)),
            ("OData (Parallel)", () =>
            {
                var opt = BenchmarkDataHelper.CreateODataOptions(context, sp, odataComplex);
                opt.ApplyTo(dummyQuery);
            })));
    }

    public static void RunPaginationStrategiesSuite()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("\n[5/7] BENCHMARK: PostgreSQL Pagination Strategies (Docker localhost:5432, 7 rounds x 70 iters)");
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
            IncludeTotal = true
        };

        var singleQueryDto = new UserBenchmarkDto
        {
            Status = "Active",
            MinAge = 25,
            MaxAge = 40,
            Page = 2,
            PageSize = 20,
            IncludeTotal = false
        };

        var cursorDto = new UserBenchmarkDto
        {
            Status = "Active",
            MinAge = 25,
            MaxAge = 40,
            PageSize = 20,
            AfterCursor = BenchmarkDataHelper.CursorAfterId(2000)
        };

        // 7 rounds x 70 iterations per strategy (round order rotates)
        PrintResultsTable(MeasureInterleaved(70, rounds: 7, warmup: 100,
            ("Standard (Count + Data, 2 DB queries)", () => db.Users.AsNoTracking().ToPagedResultAsync(standardDto).GetAwaiter().GetResult()),
            ("Single-Query (No Count, 1 DB query)", () => db.Users.AsNoTracking().ToPagedResultAsync(singleQueryDto).GetAwaiter().GetResult()),
            ("Keyset Cursor (Index Seek, 1 DB query)", () => db.Users.AsNoTracking().ToPagedResultAsync(cursorDto).GetAwaiter().GetResult()),
            ("Zero-Copy Stream (IAsyncEnumerable)", () =>
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
            })));
    }

    /// <summary>
    /// Real API traffic: almost every request carries a different filter-value combination.
    /// Libraries that inline values as SQL literals miss the EF Core compiled-query cache on every such request
    /// (full LINQ-to-SQL recompilation); Qryx sends parameters and reuses one compiled query.
    /// </summary>
    public static void RunUniqueValuesSuite()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("\n[6/7] BENCHMARK: Unique Filter Values per Request (realistic API traffic, EF query cache behaviour)");
        Console.WriteLine("      (MinAge/MaxAge/Name combinations repeat only every 33,263 requests -> literal-inlining = cache miss)");
        Console.ResetColor();

        var sieveProcessor = new BenchmarkSieveProcessor(Options.Create(new SieveOptions()), new BenchmarkSieveCustomFilterMethods());
        var (context, sp) = BenchmarkDataHelper.CreateODataContext();
        string[] names = ["Ali", "Vali", "Bob", "Jo", "Mal", "Sar", "Dil", "Zil"];

        // Each framework gets its own monotonically increasing counter (see MeasureInterleaved), so values never repeat
        // within a framework during the run: LCM(29, 31, 37) = 33,263 distinct combinations.
        static (int MinAge, int MaxAge, string Name) Values(int i, string[] names)
        {
            var n = names[i % 37 % names.Length] + (i % 37 >= names.Length ? "x" + (i % 37) : "");
            return (18 + i % 29, 40 + i % 31, n);
        }

        void RunOn(string title, Func<IQueryable<BenchmarkUser>> source, int iterationsPerRound)
        {
            Console.WriteLine($"      -- {title}");
            PrintResultsTable(MeasureInterleaved(iterationsPerRound, rounds: 7, warmup: 50,
                ("Qryx (parameterized SQL)", i =>
                {
                    var (min, max, name) = Values(i, names);
                    var dto = new UserBenchmarkDto { MinAge = min, MaxAge = max, Status = "Active", FullName = name, SortBy = nameof(BenchmarkUser.CreatedAt), IsDescending = true, Page = 2, PageSize = 20 };
                    var q = FilterBuilder.Apply(source(), dto);
                    q = SortBuilder.Apply(q, dto);
                    return PaginationBuilder.ApplyOffset(q, dto).ToList();
                }),
                ("Gridify (inlines SQL literals)", i =>
                {
                    var (min, max, name) = Values(i, names);
                    return source()
                        .ApplyFiltering($"Age >= {min}, Age <= {max}, Status=Active, (FirstName ^ {name}|LastName ^ {name})")
                        .ApplyOrdering("CreatedAt desc")
                        .ApplyPaging(2, 20)
                        .ToList();
                }),
                ("Sieve", i =>
                {
                    var (min, max, name) = Values(i, names);
                    var model = new SieveModel { Filters = $"Age>={min},Age<={max},Status==Active,FullName=={name}", Sorts = "-CreatedAt", Page = 2, PageSize = 20 };
                    return sieveProcessor.Apply(model, source()).ToList();
                }),
                ("OData", i =>
                {
                    var (min, max, name) = Values(i, names);
                    var url = $"?$filter=Age ge {min} and Age le {max} and Status eq 'Active' and (startswith(FirstName, '{name}') or startswith(LastName, '{name}'))&$orderby=CreatedAt desc&$skip=20&$top=20";
                    var opt = BenchmarkDataHelper.CreateODataOptions(context, sp, url);
                    return ((IQueryable<BenchmarkUser>)opt.ApplyTo(source())).ToList();
                })));
        }

        using (var memDb = BenchmarkDataHelper.CreateInMemoryDbContext(BenchmarkDataHelper.GenerateUsers(10_000)))
        {
            RunOn("EF Core InMemory (10,000 entities)", () => memDb.Users.AsNoTracking(), 150);
        }

        try
        {
            PostgresBenchmarkHelper.EnsureDatabaseAndDataAsync(10_000).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"      [Postgres] Could not connect to PostgreSQL: {ex.Message}. Skipping Postgres part.");
            Console.ResetColor();
            return;
        }

        using var pgDb = new PostgresBenchmarkDbContext();
        RunOn("Real PostgreSQL (Docker localhost:5432, 10,000 rows)", () => pgDb.Users.AsNoTracking(), 70);
    }

    /// <summary>
    /// Where the remaining time goes on a real database, and what an application can still remove:
    /// Npgsql automatic prepared statements (skip PostgreSQL parse/plan) need a STABLE SQL text, which Qryx's
    /// parameterized SQL provides and literal-inlining (Gridify) does not when values change. A hand-written
    /// EF.CompileQuery is shown as the EF Core ceiling (skips EF's per-query expression processing); it is a
    /// reference row, not a Qryx feature.
    /// </summary>
    public static void RunPreparedStatementsSuite()
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("\n[7/7] BENCHMARK: PostgreSQL Prepared Statements (Npgsql Max Auto Prepare) & EF Core Ceiling");
        Console.WriteLine("      (Connection string: \"Max Auto Prepare=50;Auto Prepare Min Usages=2\" vs default; 7 rounds x 70 iters)");
        Console.ResetColor();

        try
        {
            PostgresBenchmarkHelper.EnsureDatabaseAndDataAsync(10_000).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"      [Postgres] Could not connect to PostgreSQL: {ex.Message}. Skipping prepared statements suite.");
            Console.ResetColor();
            return;
        }

        using var plainDb = new PostgresBenchmarkDbContext();
        using var preparedDb = new PostgresBenchmarkDbContext(PostgresBenchmarkDbContext.AutoPrepareConnectionString);
        string[] names = ["Ali", "Vali", "Bob", "Jo", "Mal", "Sar", "Dil", "Zil"];

        static UserBenchmarkDto Request(int i, bool unique, string[] names)
        {
            if (!unique)
            {
                return new UserBenchmarkDto { MinAge = 25, MaxAge = 40, Status = "Active", FullName = "Ali", SortBy = nameof(BenchmarkUser.CreatedAt), IsDescending = true, Page = 2, PageSize = 20 };
            }

            var name = names[i % 37 % names.Length] + (i % 37 >= names.Length ? "x" + (i % 37) : "");
            return new UserBenchmarkDto { MinAge = 18 + i % 29, MaxAge = 40 + i % 31, Status = "Active", FullName = name, SortBy = nameof(BenchmarkUser.CreatedAt), IsDescending = true, Page = 2, PageSize = 20 };
        }

        static List<BenchmarkUser> Qryx(PostgresBenchmarkDbContext db, UserBenchmarkDto dto)
        {
            var q = FilterBuilder.Apply(db.Users.AsNoTracking(), dto);
            q = SortBuilder.Apply(q, dto);
            return PaginationBuilder.ApplyOffset(q, dto).ToList();
        }

        static List<BenchmarkUser> Gridify(PostgresBenchmarkDbContext db, UserBenchmarkDto dto) =>
            db.Users.AsNoTracking()
                .ApplyFiltering($"Age >= {dto.MinAge}, Age <= {dto.MaxAge}, Status=Active, (FirstName ^ {dto.FullName}|LastName ^ {dto.FullName})")
                .ApplyOrdering("CreatedAt desc")
                .ApplyPaging(2, 20)
                .ToList();

        // Hand-written, fixed-shape compiled query: the lower bound EF Core itself allows (reference only)
        var compiled = EF.CompileQuery((PostgresBenchmarkDbContext db, int min, int max, string status, string name) =>
            db.Users.AsNoTracking()
                .Where(u => u.Status == status && u.Age >= min && u.Age <= max && (u.FirstName.StartsWith(name) || u.LastName.StartsWith(name)))
                .OrderByDescending(u => u.CreatedAt)
                .Skip(20)
                .Take(20));

        foreach (var unique in new[] { false, true })
        {
            Console.WriteLine(unique
                ? "      -- Unique filter values per request (realistic API traffic)"
                : "      -- Fixed filter values (same request repeated)");

            // Both contexts share one EF model and therefore ONE compiled-query cache. Every case walks its own,
            // disjoint value range (offset k * 5,000 inside the 33,263-combination cycle; a case uses < 600 values),
            // otherwise "Gridify + Auto Prepare" would reuse queries "Gridify" had already compiled.
            PrintResultsTable(MeasureInterleaved(70, rounds: 7, warmup: 100,
                ("Qryx", i => Qryx(plainDb, Request(i, unique, names))),
                ("Qryx + Auto Prepare", i => Qryx(preparedDb, Request(i + 5_000, unique, names))),
                ("Gridify", i => Gridify(plainDb, Request(i + 10_000, unique, names))),
                ("Gridify + Auto Prepare", i => Gridify(preparedDb, Request(i + 15_000, unique, names))),
                ("EF.CompileQuery + Auto Prepare (ref)", i =>
                {
                    var r = Request(i + 20_000, unique, names);
                    return compiled(preparedDb, r.MinAge!.Value, r.MaxAge!.Value, r.Status!, r.FullName!).ToList();
                })));
        }

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  (ref) = hand-written fixed-shape LINQ compiled with EF.CompileQuery: the EF Core ceiling, not a Qryx feature.");
        Console.WriteLine("  Auto Prepare reuses PostgreSQL's parsed/planned statement only for an identical SQL text: Qryx's parameterized");
        Console.WriteLine("  SQL stays identical across values, Gridify's inlined literals change the SQL text on every new value.");
        Console.ResetColor();
    }

    /// <summary>
    /// Fair sequential measurement: every case is warmed up past tiered JIT first, then measured in
    /// <paramref name="rounds"/> rounds whose order rotates each round (no framework always runs first/last
    /// and inherits shared EF Core JIT/GC state). Reports the median round per case.
    /// </summary>
    private static List<BenchmarkMetric> MeasureInterleaved(
        int iterationsPerRound,
        int rounds,
        int warmup,
        params (string Name, Func<int, object> Action)[] cases)
    {
        var counters = new int[cases.Length];
        for (int c = 0; c < cases.Length; c++)
        {
            for (int i = 0; i < warmup; i++)
                cases[c].Action(counters[c]++);
        }

        var times = cases.Select(_ => new List<double>(rounds)).ToArray();
        var allocs = cases.Select(_ => new List<long>(rounds)).ToArray();

        for (int r = 0; r < rounds; r++)
        {
            for (int k = 0; k < cases.Length; k++)
            {
                int c = (k + r) % cases.Length; // rotate start position every round
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                long startBytes = GC.GetAllocatedBytesForCurrentThread();
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < iterationsPerRound; i++)
                    cases[c].Action(counters[c]++);
                sw.Stop();

                times[c].Add(sw.Elapsed.TotalNanoseconds / iterationsPerRound);
                allocs[c].Add((GC.GetAllocatedBytesForCurrentThread() - startBytes) / iterationsPerRound);
            }
        }

        var metrics = new List<BenchmarkMetric>(cases.Length);
        for (int c = 0; c < cases.Length; c++)
        {
            times[c].Sort();
            allocs[c].Sort();
            var medianNs = times[c][rounds / 2];
            metrics.Add(new BenchmarkMetric(cases[c].Name, medianNs, 1_000_000_000.0 / medianNs, allocs[c][rounds / 2], 0, 0, 0)
            {
                MinNsPerOp = times[c][0],
                MaxNsPerOp = times[c][^1]
            });
        }

        return metrics;
    }

    private static List<BenchmarkMetric> MeasureInterleaved(
        int iterationsPerRound,
        int rounds,
        int warmup,
        params (string Name, Func<object> Action)[] cases)
    {
        return MeasureInterleaved(iterationsPerRound, rounds, warmup,
            cases.Select(c => (c.Name, (Func<int, object>)(_ => c.Action()))).ToArray());
    }

    /// <summary>Parallel variant of <see cref="MeasureInterleaved(int,int,int,ValueTuple{string,Func{int,object}}[])"/>.</summary>
    private static List<BenchmarkMetric> MeasureParallelInterleaved(
        int opsPerRound,
        int rounds,
        params (string Name, Action Action)[] cases)
    {
        foreach (var c in cases)
        {
            // Single-threaded first: some libraries (OData's ODataQueryContext) lazily initialize
            // shared state in a non-thread-safe way on first use.
            for (int i = 0; i < 100; i++)
                c.Action();

            Parallel.For(0, 2_000, _ => c.Action());
        }

        var times = cases.Select(_ => new List<double>(rounds)).ToArray();
        var allocs = cases.Select(_ => new List<long>(rounds)).ToArray();

        for (int r = 0; r < rounds; r++)
        {
            for (int k = 0; k < cases.Length; k++)
            {
                int c = (k + r) % cases.Length;
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                long startBytes = GC.GetTotalAllocatedBytes(precise: true);
                var sw = Stopwatch.StartNew();
                Parallel.For(0, opsPerRound, _ => cases[c].Action());
                sw.Stop();

                times[c].Add(sw.Elapsed.TotalNanoseconds / opsPerRound);
                allocs[c].Add((GC.GetTotalAllocatedBytes(precise: true) - startBytes) / opsPerRound);
            }
        }

        var metrics = new List<BenchmarkMetric>(cases.Length);
        for (int c = 0; c < cases.Length; c++)
        {
            times[c].Sort();
            allocs[c].Sort();
            var medianNs = times[c][rounds / 2];
            metrics.Add(new BenchmarkMetric(cases[c].Name, medianNs, 1_000_000_000.0 / medianNs, allocs[c][rounds / 2], 0, 0, 0)
            {
                MinNsPerOp = times[c][0],
                MaxNsPerOp = times[c][^1]
            });
        }

        return metrics;
    }

    /// <summary>
    /// Runs only the FILTER part of every framework (no sorting/paging, so CreatedAt ties cannot cause false
    /// mismatches) and checks that all of them select exactly the same rows.
    /// </summary>
    private static void VerifyFilterParity(
        IQueryable<BenchmarkUser> source,
        UserBenchmarkDto qryxDto,
        string gridifyFilter,
        SieveModel sieveModel,
        BenchmarkSieveProcessor sieveProcessor,
        ODataQueryContext odataContext,
        IServiceProvider odataServices,
        string odataQuery)
    {
        var odataFilterOnly = odataQuery.Split('&')[0]; // "?$filter=..."
        var odataOptions = BenchmarkDataHelper.CreateODataOptions(odataContext, odataServices, odataFilterOnly);

        VerifySameRows(
            ("Qryx", FilterBuilder.Apply(source, qryxDto).Select(u => u.Id).ToList()),
            ("Gridify", source.ApplyFiltering(gridifyFilter).Select(u => u.Id).ToList()),
            ("Sieve", sieveProcessor.Apply(sieveModel, source, applySorting: false, applyPagination: false).Select(u => u.Id).ToList()),
            ("OData", ((IQueryable<BenchmarkUser>)odataOptions.ApplyTo(source)).Select(u => u.Id).ToList()));
    }

    /// <summary>
    /// Semantic check: every framework's filter must match exactly the same rows (ID set), otherwise
    /// the timings compare different queries (e.g. Gridify "=*" is Contains, not StartsWith).
    /// </summary>
    private static void VerifySameRows(params (string Name, IEnumerable<int> Ids)[] results)
    {
        var baseline = results[0].Ids.Order().ToArray();
        var ok = true;
        foreach (var (name, ids) in results.Skip(1))
        {
            var actual = ids.Order().ToArray();
            if (!actual.SequenceEqual(baseline))
            {
                ok = false;
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"      [Verification FAILED] {name}: {actual.Length} rows, {results[0].Name}: {baseline.Length} rows -> different filter semantics!");
                Console.ResetColor();
            }
        }

        if (ok)
        {
            Console.WriteLine($"      [Verification] All {results.Length} frameworks match the same {baseline.Length} rows (filter semantics identical)");
        }
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

        static string FormatTime(double ns) => ns < 1000
            ? $"{ns:F1} ns"
            : ns < 1_000_000
                ? $"{ns / 1000.0:F2} μs"
                : $"{ns / 1_000_000.0:F2} ms";

        Console.WriteLine("+----------------------------------+-------------+---------------+--------------+------------+-------------------------+");
        Console.WriteLine("| Framework                        | Time/Op     | Speed Ratio   | Ops / Sec    | Allocated  | Noise (min..max round)  |");
        Console.WriteLine("+----------------------------------+-------------+---------------+--------------+------------+-------------------------+");

        foreach (var m in metrics)
        {
            string timeStr = FormatTime(m.NsPerOp);
            string noiseStr = m.MinNsPerOp.HasValue && m.MaxNsPerOp.HasValue
                ? $"{FormatTime(m.MinNsPerOp.Value)}..{FormatTime(m.MaxNsPerOp.Value)}"
                : "-";

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

            Console.WriteLine($"| {m.Name,-32} | {timeStr,11} | {ratioStr,13} | {opsStr,12} | {allocStr,10} | {noiseStr,-23} |");
            Console.ResetColor();
        }

        Console.WriteLine("+----------------------------------+-------------+---------------+--------------+------------+-------------------------+");
        Console.WriteLine("  Time/Op = median of interleaved rounds (round order rotates; every case warmed up past tiered JIT).");
    }
}

public record BenchmarkMetric(
    string Name,
    double NsPerOp,
    double OpsPerSec,
    long BytesPerOp,
    int Gen0,
    int Gen1,
    int Gen2)
{
    /// <summary>Fastest / slowest measured round (interleaved runs only), to show the noise band.</summary>
    public double? MinNsPerOp { get; init; }
    public double? MaxNsPerOp { get; init; }
}
