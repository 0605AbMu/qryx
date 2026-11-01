using Microsoft.Extensions.Options;
using Qryx.Benchmarks.Models;
using Sieve.Models;
using Sieve.Services;

namespace Qryx.Benchmarks.Services;

public class BenchmarkSieveCustomFilterMethods : ISieveCustomFilterMethods
{
    public IQueryable<BenchmarkUser> FullName(IQueryable<BenchmarkUser> source, string op, string[] values)
    {
        var val = values.FirstOrDefault();
        if (string.IsNullOrEmpty(val))
        {
            return source;
        }

        return source.Where(u => u.FirstName.StartsWith(val) || u.LastName.StartsWith(val));
    }
}

public class BenchmarkSieveProcessor : SieveProcessor
{
    public BenchmarkSieveProcessor(
        IOptions<SieveOptions> options,
        ISieveCustomFilterMethods customFilterMethods)
        : base(options, customFilterMethods)
    {
    }

    protected override SievePropertyMapper MapProperties(SievePropertyMapper mapper)
    {
        mapper.Property<BenchmarkUser>(p => p.Id).CanFilter().CanSort();
        mapper.Property<BenchmarkUser>(p => p.FirstName).CanFilter().CanSort();
        mapper.Property<BenchmarkUser>(p => p.LastName).CanFilter().CanSort();
        mapper.Property<BenchmarkUser>(p => p.Age).CanFilter().CanSort();
        mapper.Property<BenchmarkUser>(p => p.Status).CanFilter().CanSort();
        mapper.Property<BenchmarkUser>(p => p.CreatedAt).CanFilter().CanSort();
        return mapper;
    }
}
