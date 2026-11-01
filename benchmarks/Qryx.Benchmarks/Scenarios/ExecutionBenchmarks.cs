using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
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
public class ExecutionBenchmarks
{
    private BenchmarkDbContext _dbContext = null!;
    private BenchmarkSieveProcessor _sieveProcessor = null!;
    private ODataQueryContext _odataContext = null!;
    private IServiceProvider _odataServiceProvider = null!;

    // Qryx DTOs
    private SimpleUserBenchmarkDto _qryxSimpleDto = null!;
    private RangeUserBenchmarkDto _qryxRangeDto = null!;
    private UserBenchmarkDto _qryxComplexDto = null!;

    // Gridify inputs
    private const string GridifySimpleFilter = "Status=Active";
    private const string GridifyRangeFilter = "Age >= 25, Age <= 40";
    private GridifyQuery _gridifyComplexQuery = null!;

    // Sieve inputs
    private SieveModel _sieveSimpleModel = null!;
    private SieveModel _sieveRangeModel = null!;
    private SieveModel _sieveComplexModel = null!;

    // OData query strings
    private const string ODataSimpleQuery = "?$filter=Status eq 'Active'";
    private const string ODataRangeQuery = "?$filter=Age ge 25 and Age le 40";
    private const string ODataComplexQuery = "?$filter=Age ge 25 and Age le 40 and Status eq 'Active' and (startswith(FirstName, 'Ali') or startswith(LastName, 'Ali'))&$orderby=CreatedAt desc&$skip=20&$top=20";

    [GlobalSetup]
    public void Setup()
    {
        var users = BenchmarkDataHelper.GenerateUsers(10_000);
        _dbContext = BenchmarkDataHelper.CreateInMemoryDbContext(users);

        var sieveOptions = Options.Create(new SieveOptions());
        _sieveProcessor = new BenchmarkSieveProcessor(sieveOptions, new BenchmarkSieveCustomFilterMethods());

        var (context, sp) = BenchmarkDataHelper.CreateODataContext();
        _odataContext = context;
        _odataServiceProvider = sp;

        _qryxSimpleDto = new SimpleUserBenchmarkDto { Status = "Active" };
        _qryxRangeDto = new RangeUserBenchmarkDto { MinAge = 25, MaxAge = 40 };
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

        _sieveSimpleModel = new SieveModel { Filters = "Status==Active" };
        _sieveRangeModel = new SieveModel { Filters = "Age>=25,Age<=40" };
        _sieveComplexModel = new SieveModel
        {
            Filters = "Age>=25,Age<=40,Status==Active,FullName==Ali",
            Sorts = "-CreatedAt",
            Page = 2,
            PageSize = 20
        };
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _dbContext.Dispose();
    }

    // ==========================================
    // 1. END-TO-END SIMPLE FILTER EXECUTION (10k items)
    // ==========================================

    [Benchmark(Baseline = true, Description = "Qryx (Execute Simple Filter)")]
    public List<BenchmarkUser> Qryx_ExecuteSimple()
    {
        return FilterBuilder.Apply(_dbContext.Users.AsNoTracking(), _qryxSimpleDto).ToList();
    }

    [Benchmark(Description = "Gridify (Execute Simple Filter)")]
    public List<BenchmarkUser> Gridify_ExecuteSimple()
    {
        return _dbContext.Users.AsNoTracking().ApplyFiltering(GridifySimpleFilter).ToList();
    }

    [Benchmark(Description = "Sieve (Execute Simple Filter)")]
    public List<BenchmarkUser> Sieve_ExecuteSimple()
    {
        return _sieveProcessor.Apply(_sieveSimpleModel, _dbContext.Users.AsNoTracking()).ToList();
    }

    [Benchmark(Description = "OData (Execute Simple Filter)")]
    public List<BenchmarkUser> OData_ExecuteSimple()
    {
        var options = BenchmarkDataHelper.CreateODataOptions(_odataContext, _odataServiceProvider, ODataSimpleQuery);
        return ((IQueryable<BenchmarkUser>)options.ApplyTo(_dbContext.Users.AsNoTracking())).ToList();
    }

    // ==========================================
    // 2. END-TO-END RANGE FILTER EXECUTION (10k items)
    // ==========================================

    [Benchmark(Description = "Qryx (Execute Range Filter)")]
    public List<BenchmarkUser> Qryx_ExecuteRange()
    {
        return FilterBuilder.Apply(_dbContext.Users.AsNoTracking(), _qryxRangeDto).ToList();
    }

    [Benchmark(Description = "Gridify (Execute Range Filter)")]
    public List<BenchmarkUser> Gridify_ExecuteRange()
    {
        return _dbContext.Users.AsNoTracking().ApplyFiltering(GridifyRangeFilter).ToList();
    }

    [Benchmark(Description = "Sieve (Execute Range Filter)")]
    public List<BenchmarkUser> Sieve_ExecuteRange()
    {
        return _sieveProcessor.Apply(_sieveRangeModel, _dbContext.Users.AsNoTracking()).ToList();
    }

    [Benchmark(Description = "OData (Execute Range Filter)")]
    public List<BenchmarkUser> OData_ExecuteRange()
    {
        var options = BenchmarkDataHelper.CreateODataOptions(_odataContext, _odataServiceProvider, ODataRangeQuery);
        return ((IQueryable<BenchmarkUser>)options.ApplyTo(_dbContext.Users.AsNoTracking())).ToList();
    }

    // ==========================================
    // 3. END-TO-END COMPLEX QUERY EXECUTION (Filter + Sort + Page on 10k items)
    // ==========================================

    [Benchmark(Description = "Qryx (Execute Complex Query)")]
    public List<BenchmarkUser> Qryx_ExecuteComplex()
    {
        var query = FilterBuilder.Apply(_dbContext.Users.AsNoTracking(), _qryxComplexDto);
        query = SortBuilder.Apply(query, _qryxComplexDto);
        return PaginationBuilder.ApplyOffset(query, _qryxComplexDto).ToList();
    }

    [Benchmark(Description = "Gridify (Execute Complex Query)")]
    public List<BenchmarkUser> Gridify_ExecuteComplex()
    {
        var query = _dbContext.Users.AsNoTracking().ApplyFiltering(_gridifyComplexQuery.Filter);
        query = query.ApplyOrdering(_gridifyComplexQuery.OrderBy!);
        return query.ApplyPaging(_gridifyComplexQuery.Page, _gridifyComplexQuery.PageSize).ToList();
    }

    [Benchmark(Description = "Sieve (Execute Complex Query)")]
    public List<BenchmarkUser> Sieve_ExecuteComplex()
    {
        return _sieveProcessor.Apply(_sieveComplexModel, _dbContext.Users.AsNoTracking()).ToList();
    }

    [Benchmark(Description = "OData (Execute Complex Query)")]
    public List<BenchmarkUser> OData_ExecuteComplex()
    {
        var options = BenchmarkDataHelper.CreateODataOptions(_odataContext, _odataServiceProvider, ODataComplexQuery);
        return ((IQueryable<BenchmarkUser>)options.ApplyTo(_dbContext.Users.AsNoTracking())).ToList();
    }
}
