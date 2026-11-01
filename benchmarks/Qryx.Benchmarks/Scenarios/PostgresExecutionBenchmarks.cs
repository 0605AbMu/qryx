using BenchmarkDotNet.Attributes;
using Gridify;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Qryx.Benchmarks.Models;
using Qryx.Benchmarks.Services;
using Qryx.Expressions.Builders;
using Sieve.Models;

namespace Qryx.Benchmarks.Scenarios;

[MemoryDiagnoser]
[SimpleJob(iterationCount: 15, warmupCount: 5)]
public class PostgresExecutionBenchmarks
{
    private PostgresBenchmarkDbContext _db = null!;
    private BenchmarkSieveProcessor _sieveProcessor = null!;
    private ODataQueryContext _odataContext = null!;
    private IServiceProvider _odataServiceProvider = null!;

    // Qryx DTO
    private UserBenchmarkDto _qryxComplexDto = null!;

    // Gridify query
    private GridifyQuery _gridifyComplexQuery = null!;

    // Sieve model
    private SieveModel _sieveComplexModel = null!;

    // OData query
    private const string ODataComplexQuery = "?$filter=Age ge 25 and Age le 40 and Status eq 'Active' and (startswith(FirstName, 'Ali') or startswith(LastName, 'Ali'))&$orderby=CreatedAt desc&$skip=20&$top=20";

    [GlobalSetup]
    public void Setup()
    {
        PostgresBenchmarkHelper.EnsureDatabaseAndDataAsync(10_000).GetAwaiter().GetResult();
        _db = new PostgresBenchmarkDbContext();

        var sieveOptions = Options.Create(new SieveOptions());
        _sieveProcessor = new BenchmarkSieveProcessor(sieveOptions, new BenchmarkSieveCustomFilterMethods());

        var (context, sp) = BenchmarkDataHelper.CreateODataContext();
        _odataContext = context;
        _odataServiceProvider = sp;

        _qryxComplexDto = new UserBenchmarkDto
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

        _gridifyComplexQuery = new GridifyQuery
        {
            Filter = "Age >= 25, Age <= 40, Status=Active, (FirstName =* Ali|LastName =* Ali)",
            OrderBy = "CreatedAt desc",
            Page = 2,
            PageSize = 20
        };

        _sieveComplexModel = new SieveModel
        {
            Filters = "Age>=25,Age<=40,Status==Active,FullName==Ali",
            Sorts = "-CreatedAt",
            Page = 2,
            PageSize = 20
        };

        // Warmup connection pool
        _db.Users.AsNoTracking().Take(1).ToList();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _db.Dispose();
    }

    [Benchmark(Baseline = true, Description = "Qryx (PostgreSQL Execute)")]
    public List<BenchmarkUser> Qryx_PostgresExecute()
    {
        var query = FilterBuilder.Apply(_db.Users.AsNoTracking(), _qryxComplexDto);
        query = SortBuilder.Apply(query, _qryxComplexDto);
        return PaginationBuilder.ApplyOffset(query, _qryxComplexDto).ToList();
    }

    [Benchmark(Description = "Gridify (PostgreSQL Execute)")]
    public List<BenchmarkUser> Gridify_PostgresExecute()
    {
        var query = _db.Users.AsNoTracking().ApplyFiltering(_gridifyComplexQuery.Filter);
        query = query.ApplyOrdering(_gridifyComplexQuery.OrderBy!);
        return query.ApplyPaging(_gridifyComplexQuery.Page, _gridifyComplexQuery.PageSize).ToList();
    }

    [Benchmark(Description = "Sieve (PostgreSQL Execute)")]
    public List<BenchmarkUser> Sieve_PostgresExecute()
    {
        return _sieveProcessor.Apply(_sieveComplexModel, _db.Users.AsNoTracking()).ToList();
    }

    [Benchmark(Description = "OData (PostgreSQL Execute)")]
    public List<BenchmarkUser> OData_PostgresExecute()
    {
        var options = BenchmarkDataHelper.CreateODataOptions(_odataContext, _odataServiceProvider, ODataComplexQuery);
        return ((IQueryable<BenchmarkUser>)options.ApplyTo(_db.Users.AsNoTracking())).ToList();
    }
}
