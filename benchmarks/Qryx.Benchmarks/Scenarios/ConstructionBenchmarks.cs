using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Gridify;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.Extensions.Options;
using Qryx.Benchmarks.Models;
using Qryx.Benchmarks.Services;
using Qryx.Expressions.Builders;
using Sieve.Models;
using Sieve.Services;

namespace Qryx.Benchmarks.Scenarios;

[MemoryDiagnoser]
[SimpleJob(iterationCount: 15, warmupCount: 5)]
public class ConstructionBenchmarks
{
    private IQueryable<BenchmarkUser> _dummyQuery = null!;
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
    private const string GridifyOrFilter = "(FirstName =* Ali|LastName =* Ali)";
    private GridifyQuery _gridifyComplexQuery = null!;

    // Sieve inputs
    private SieveModel _sieveSimpleModel = null!;
    private SieveModel _sieveRangeModel = null!;
    private SieveModel _sieveOrModel = null!;
    private SieveModel _sieveComplexModel = null!;

    // OData query strings
    private const string ODataSimpleQuery = "?$filter=Status eq 'Active'";
    private const string ODataRangeQuery = "?$filter=Age ge 25 and Age le 40";
    private const string ODataOrQuery = "?$filter=startswith(FirstName, 'Ali') or startswith(LastName, 'Ali')";
    private const string ODataComplexQuery = "?$filter=Age ge 25 and Age le 40 and Status eq 'Active' and (startswith(FirstName, 'Ali') or startswith(LastName, 'Ali'))&$orderby=CreatedAt desc&$skip=20&$top=20";

    [GlobalSetup]
    public void Setup()
    {
        _dummyQuery = new List<BenchmarkUser>().AsQueryable();

        // Setup Sieve
        var sieveOptions = Options.Create(new SieveOptions());
        _sieveProcessor = new BenchmarkSieveProcessor(sieveOptions, new BenchmarkSieveCustomFilterMethods());

        // Setup OData
        var (context, sp) = BenchmarkDataHelper.CreateODataContext();
        _odataContext = context;
        _odataServiceProvider = sp;

        // Qryx
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

        // Gridify
        _gridifyComplexQuery = new GridifyQuery
        {
            Filter = "Age >= 25, Age <= 40, Status=Active, (FirstName =* Ali|LastName =* Ali)",
            OrderBy = "CreatedAt desc",
            Page = 2,
            PageSize = 20
        };

        // Sieve models
        _sieveSimpleModel = new SieveModel { Filters = "Status==Active" };
        _sieveRangeModel = new SieveModel { Filters = "Age>=25,Age<=40" };
        _sieveOrModel = new SieveModel { Filters = "FullName==Ali" };
        _sieveComplexModel = new SieveModel
        {
            Filters = "Age>=25,Age<=40,Status==Active,FullName==Ali",
            Sorts = "-CreatedAt",
            Page = 2,
            PageSize = 20
        };

        // Warmup caches
        FilterBuilder.Apply(_dummyQuery, _qryxComplexDto);
        _dummyQuery.ApplyFiltering(GridifySimpleFilter);
        _sieveProcessor.Apply(_sieveSimpleModel, _dummyQuery);
        var warmOdata = BenchmarkDataHelper.CreateODataOptions(_odataContext, _odataServiceProvider, ODataSimpleQuery);
        warmOdata.ApplyTo(_dummyQuery);
    }

    // ==========================================
    // 1. SIMPLE FILTER BENCHMARK
    // ==========================================

    [Benchmark(Baseline = true, Description = "Qryx (Simple Filter)")]
    public IQueryable<BenchmarkUser> Qryx_SimpleFilter()
    {
        return FilterBuilder.Apply(_dummyQuery, _qryxSimpleDto);
    }

    [Benchmark(Description = "Gridify (Simple Filter)")]
    public IQueryable<BenchmarkUser> Gridify_SimpleFilter()
    {
        return _dummyQuery.ApplyFiltering(GridifySimpleFilter);
    }

    [Benchmark(Description = "Sieve (Simple Filter)")]
    public IQueryable<BenchmarkUser> Sieve_SimpleFilter()
    {
        return _sieveProcessor.Apply(_sieveSimpleModel, _dummyQuery);
    }

    [Benchmark(Description = "OData (Simple Filter)")]
    public IQueryable<BenchmarkUser> OData_SimpleFilter()
    {
        var options = BenchmarkDataHelper.CreateODataOptions(_odataContext, _odataServiceProvider, ODataSimpleQuery);
        return (IQueryable<BenchmarkUser>)options.ApplyTo(_dummyQuery);
    }

    // ==========================================
    // 2. RANGE FILTER BENCHMARK
    // ==========================================

    [Benchmark(Description = "Qryx (Range Filter)")]
    public IQueryable<BenchmarkUser> Qryx_RangeFilter()
    {
        return FilterBuilder.Apply(_dummyQuery, _qryxRangeDto);
    }

    [Benchmark(Description = "Gridify (Range Filter)")]
    public IQueryable<BenchmarkUser> Gridify_RangeFilter()
    {
        return _dummyQuery.ApplyFiltering(GridifyRangeFilter);
    }

    [Benchmark(Description = "Sieve (Range Filter)")]
    public IQueryable<BenchmarkUser> Sieve_RangeFilter()
    {
        return _sieveProcessor.Apply(_sieveRangeModel, _dummyQuery);
    }

    [Benchmark(Description = "OData (Range Filter)")]
    public IQueryable<BenchmarkUser> OData_RangeFilter()
    {
        var options = BenchmarkDataHelper.CreateODataOptions(_odataContext, _odataServiceProvider, ODataRangeQuery);
        return (IQueryable<BenchmarkUser>)options.ApplyTo(_dummyQuery);
    }

    // ==========================================
    // 3. MULTI-COLUMN OR / COMPUTED FILTER
    // ==========================================

    [Benchmark(Description = "Qryx (Multi-Target OR)")]
    public IQueryable<BenchmarkUser> Qryx_MultiTargetOr()
    {
        return FilterBuilder.Apply(_dummyQuery, _qryxComplexDto);
    }

    [Benchmark(Description = "Gridify (Multi-Target OR)")]
    public IQueryable<BenchmarkUser> Gridify_MultiTargetOr()
    {
        return _dummyQuery.ApplyFiltering(GridifyOrFilter);
    }

    [Benchmark(Description = "Sieve (Multi-Target OR)")]
    public IQueryable<BenchmarkUser> Sieve_MultiTargetOr()
    {
        return _sieveProcessor.Apply(_sieveOrModel, _dummyQuery);
    }

    [Benchmark(Description = "OData (Multi-Target OR)")]
    public IQueryable<BenchmarkUser> OData_MultiTargetOr()
    {
        var options = BenchmarkDataHelper.CreateODataOptions(_odataContext, _odataServiceProvider, ODataOrQuery);
        return (IQueryable<BenchmarkUser>)options.ApplyTo(_dummyQuery);
    }

    // ==========================================
    // 4. COMPLEX QUERY (FILTER + SORT + PAGING)
    // ==========================================

    [Benchmark(Description = "Qryx (Full Complex Query)")]
    public IQueryable<BenchmarkUser> Qryx_ComplexQuery()
    {
        var query = FilterBuilder.Apply(_dummyQuery, _qryxComplexDto);
        query = SortBuilder.Apply(query, _qryxComplexDto);
        return PaginationBuilder.ApplyOffset(query, _qryxComplexDto);
    }

    [Benchmark(Description = "Gridify (Full Complex Query)")]
    public IQueryable<BenchmarkUser> Gridify_ComplexQuery()
    {
        var query = _dummyQuery.ApplyFiltering(_gridifyComplexQuery.Filter);
        query = query.ApplyOrdering(_gridifyComplexQuery.OrderBy!);
        return query.ApplyPaging(_gridifyComplexQuery.Page, _gridifyComplexQuery.PageSize);
    }

    [Benchmark(Description = "Sieve (Full Complex Query)")]
    public IQueryable<BenchmarkUser> Sieve_ComplexQuery()
    {
        return _sieveProcessor.Apply(_sieveComplexModel, _dummyQuery);
    }

    [Benchmark(Description = "OData (Full Complex Query)")]
    public IQueryable<BenchmarkUser> OData_ComplexQuery()
    {
        var options = BenchmarkDataHelper.CreateODataOptions(_odataContext, _odataServiceProvider, ODataComplexQuery);
        return (IQueryable<BenchmarkUser>)options.ApplyTo(_dummyQuery);
    }
}
