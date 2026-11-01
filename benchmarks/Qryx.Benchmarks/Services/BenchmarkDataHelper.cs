using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OData;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OData.ModelBuilder;
using Microsoft.OData.UriParser;
using Qryx.Benchmarks.Models;

namespace Qryx.Benchmarks.Services;

public class BenchmarkDbContext : DbContext
{
    public BenchmarkDbContext(DbContextOptions<BenchmarkDbContext> options) : base(options) { }

    public DbSet<BenchmarkUser> Users => Set<BenchmarkUser>();
}

public static class BenchmarkDataHelper
{
    public static List<BenchmarkUser> GenerateUsers(int count = 10_000)
    {
        var random = new Random(42); // deterministic seed
        var firstNames = new[] { "Ali", "Vali", "Alisher", "Sardor", "Jasur", "Bekzod", "Bobur", "Malika", "Zilola", "Dilnoza", "John", "Alice", "Bob" };
        var lastNames = new[] { "Valiyev", "Aliyev", "Navoiy", "Karimov", "Toshmatov", "Sobirov", "Usmonov", "Smith", "Johnson", "Williams" };
        var statuses = new[] { "Active", "Pending", "Suspended", "Inactive" };

        var list = new List<BenchmarkUser>(count);
        var baseDate = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (int i = 1; i <= count; i++)
        {
            list.Add(new BenchmarkUser
            {
                Id = i,
                FirstName = firstNames[random.Next(firstNames.Length)],
                LastName = lastNames[random.Next(lastNames.Length)],
                Age = random.Next(18, 70),
                Status = statuses[random.Next(statuses.Length)],
                CreatedAt = baseDate.AddDays(random.Next(1, 400))
            });
        }

        return list;
    }

    public static BenchmarkDbContext CreateInMemoryDbContext(List<BenchmarkUser> users)
    {
        var options = new DbContextOptionsBuilder<BenchmarkDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var context = new BenchmarkDbContext(options);
        context.Users.AddRange(users);
        context.SaveChanges();
        return context;
    }

    public static (ODataQueryContext Context, IServiceProvider ServiceProvider) CreateODataContext()
    {
        var builder = new ODataConventionModelBuilder();
        builder.EntitySet<BenchmarkUser>("BenchmarkUsers");
        var model = builder.GetEdmModel();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers().AddOData();
        var sp = services.BuildServiceProvider();

        var context = new ODataQueryContext(model, typeof(BenchmarkUser), new ODataPath());
        return (context, sp);
    }

    public static ODataQueryOptions<BenchmarkUser> CreateODataOptions(
        ODataQueryContext context,
        IServiceProvider sp,
        string queryString)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.RequestServices = sp;
        httpContext.Request.QueryString = new QueryString(queryString);

        return new ODataQueryOptions<BenchmarkUser>(context, httpContext.Request);
    }
}
