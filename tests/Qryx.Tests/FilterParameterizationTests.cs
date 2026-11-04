using Microsoft.EntityFrameworkCore;
using Qryx.Core.Attributes;
using Qryx.Core.Contracts;
using Qryx.Core.Enums;
using Qryx.EntityFramework.Extensions;
using Qryx.Expressions.Builders;

namespace Qryx.Tests;

/// <summary>
/// Filter values must be sent as SQL parameters (never inlined literals), so EF Core reuses the
/// compiled query and the database reuses its plan. SQL is inspected via ToQueryString() with Npgsql
/// (no database connection required).
/// </summary>
public class FilterParameterizationTests
{
    public enum AccountStatus
    {
        Active,
        Pending,
        Blocked
    }

    public class Account
    {
        public int Id { get; set; }
        public int? Age { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
        public AccountStatus Status { get; set; }
    }

    public record AccountQuery : QryxRequest<Account>
    {
        [Filter(nameof(Account.Age), Op.GreaterThanOrEqual)]
        public int? MinAge { get; init; }

        [Filter(Op.In)]
        public AccountStatus[]? Status { get; init; }

        [Filter(nameof(Account.Status), Op.NotEquals)]
        public string? ExcludeStatus { get; init; }

        [Filter(Op.Contains, Invariant = true)]
        public string? Name { get; init; }

        [Filter(Op.StartsWith)]
        public string? Code { get; init; }
    }

    public class NpgsqlContext : DbContext
    {
        public DbSet<Account> Accounts => Set<Account>();

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder.UseNpgsql("Host=localhost;Database=qryx_tests");
    }

    public class InMemoryContext(DbContextOptions<InMemoryContext> options) : DbContext(options)
    {
        public DbSet<Account> Accounts => Set<Account>();
    }

    /// <summary>SQL text without the "-- @param='value'" header lines that ToQueryString() prepends.</summary>
    private static string SqlBody(IQueryable query) =>
        string.Join('\n', query.ToQueryString().Split('\n').Where(l => !l.StartsWith("--", StringComparison.Ordinal)));

    [Fact]
    public void ScalarAndStringFilters_ShouldBeSqlParameters_NotLiterals()
    {
        using var db = new NpgsqlContext();
        var request = new AccountQuery { MinAge = 4242, Name = "Alisher", Code = "XZ9" };

        var sql = SqlBody(db.Accounts.ApplyQryx(request));

        Assert.DoesNotContain("4242", sql);
        Assert.DoesNotContain("Alisher", sql);
        Assert.DoesNotContain("XZ9", sql);
        Assert.Matches(@"""Age"" >= @\w+", sql);
        Assert.Matches(@"""Name"" ILIKE @\w+", sql);
        Assert.Matches(@"""Code"" LIKE @\w+", sql);
    }

    [Fact]
    public void DifferentValues_ShouldProduceIdenticalSql_ForQueryCacheReuse()
    {
        using var db = new NpgsqlContext();

        var first = SqlBody(db.Accounts.ApplyQryx(new AccountQuery
        {
            MinAge = 18, Status = [AccountStatus.Active], ExcludeStatus = "Blocked", Name = "ali", Code = "A"
        }));

        var second = SqlBody(db.Accounts.ApplyQryx(new AccountQuery
        {
            MinAge = 65, Status = [AccountStatus.Pending, AccountStatus.Blocked], ExcludeStatus = "Active", Name = "vali", Code = "B"
        }));

        Assert.Equal(first, second);
    }

    [Fact]
    public void InFilter_ShouldUseSingleCollectionParameter()
    {
        using var db = new NpgsqlContext();
        var request = new AccountQuery { Status = [AccountStatus.Active, AccountStatus.Blocked] };

        var sql = SqlBody(db.Accounts.ApplyQryx(request));

        Assert.Matches(@"""Status"" = ANY \(@\w+\)", sql);
        Assert.DoesNotContain(" IN (", sql);
    }

    [Fact]
    public void InvariantILike_ShouldEscapeWildcards_WithExplicitEscapeCharacter()
    {
        using var db = new NpgsqlContext();
        var request = new AccountQuery { Name = @"50%_a\b" };

        var fullSql = db.Accounts.ApplyQryx(request).ToQueryString();

        // User supplied '%', '_' and '\' must match literally, not as wildcards
        Assert.Contains(@"'%50\%\_a\\b%'", fullSql);
        Assert.Contains(@"ESCAPE '\'", fullSql);
    }

    [Fact]
    public void KeysetCursor_ShouldBeSqlParameter()
    {
        using var db = new NpgsqlContext();

        var plan = SortBuilder.ResolveSort<Account>(sortBy: null);
        var cursor = PaginationBuilder.CreateCursor(new Account { Id = 987654 }, plan)!;

        var sql = SqlBody(PaginationBuilder.ApplyKeyset(db.Accounts, cursor, plan, pageSize: 10));

        Assert.DoesNotContain("987654", sql);
        Assert.Matches(@"""Id"" > @\w+", sql);
    }

    [Fact]
    public async Task ParameterizedFilters_ShouldReturnCorrectRows()
    {
        var options = new DbContextOptionsBuilder<InMemoryContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var db = new InMemoryContext(options);
        db.Accounts.AddRange(
            new Account { Id = 1, Age = 17, Name = "Ali Valiyev", Code = "AB-1", Status = AccountStatus.Active },
            new Account { Id = 2, Age = 25, Name = "ALISHER Navoiy", Code = "AB-2", Status = AccountStatus.Pending },
            new Account { Id = 3, Age = 40, Name = "Madina", Code = "CD-3", Status = AccountStatus.Active },
            new Account { Id = 4, Age = null, Name = "Bobur alim", Code = "AB-4", Status = AccountStatus.Blocked });
        await db.SaveChangesAsync();

        var request = new AccountQuery
        {
            MinAge = 18,
            Status = [AccountStatus.Active, AccountStatus.Pending],
            ExcludeStatus = "pending", // string -> enum conversion (case-insensitive)
            Name = "ali",              // invariant: matches "Ali", "ALISHER", "alim"
            Code = "AB"
        };

        var result = await db.Accounts.ToPagedResultAsync(request);

        // Id 1: Age 17 (< 18); Id 2: Pending (excluded); Id 3: no "ali", code CD; Id 4: Age null, Blocked
        Assert.Empty(result.Items);

        var relaxed = await db.Accounts.ToPagedResultAsync(new AccountQuery { MinAge = 18, Name = "ali" });
        Assert.Equal([2], relaxed.Items.Select(a => a.Id));

        var byStatus = await db.Accounts.ToPagedResultAsync(new AccountQuery { Status = [AccountStatus.Active], Code = "AB" });
        Assert.Equal([1], byStatus.Items.Select(a => a.Id));
    }
}
