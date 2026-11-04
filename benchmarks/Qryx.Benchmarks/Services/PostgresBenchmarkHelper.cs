using Microsoft.EntityFrameworkCore;
using Npgsql;
using Qryx.Benchmarks.Models;

namespace Qryx.Benchmarks.Services;

public class PostgresBenchmarkDbContext : DbContext
{
    public const string ConnectionString = "Host=localhost;Port=5432;Database=qryx_benchmark;Username=taxi;Password=taxi;Pooling=true;Minimum Pool Size=5;Maximum Pool Size=50;";

    /// <summary>
    /// Same database with Npgsql automatic prepared statements: after 2 executions of an identical SQL text,
    /// PostgreSQL reuses the parsed/planned statement. Only helps when the SQL text is stable (parameterized).
    /// </summary>
    public const string AutoPrepareConnectionString = ConnectionString + "Max Auto Prepare=50;Auto Prepare Min Usages=2;";

    private readonly string _connectionString;

    public PostgresBenchmarkDbContext() : this(ConnectionString)
    {
    }

    public PostgresBenchmarkDbContext(string connectionString)
    {
        _connectionString = connectionString;
    }

    public DbSet<BenchmarkUser> Users => Set<BenchmarkUser>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            optionsBuilder.UseNpgsql(_connectionString);
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<BenchmarkUser>();

        entity.ToTable("benchmark_users");
        entity.HasKey(u => u.Id);

        // Map PostgreSQL B-Tree Indexes
        entity.HasIndex(u => u.Age).HasDatabaseName("idx_users_age");
        entity.HasIndex(u => u.Status).HasDatabaseName("idx_users_status");
        entity.HasIndex(u => u.CreatedAt).HasDatabaseName("idx_users_created_at");
        entity.HasIndex(u => u.FirstName).HasDatabaseName("idx_users_first_name");
        entity.HasIndex(u => u.LastName).HasDatabaseName("idx_users_last_name");
    }
}

public static class PostgresBenchmarkHelper
{
    public static async Task EnsureDatabaseAndDataAsync(int count = 10_000)
    {
        using var db = new PostgresBenchmarkDbContext();
        await db.Database.EnsureCreatedAsync();

        var existingCount = await db.Users.CountAsync();
        if (existingCount >= count)
        {
            Console.WriteLine($"      [Postgres] Database already has {existingCount} rows. Ready.");
            return;
        }

        Console.WriteLine($"      [Postgres] Seeding {count} rows into PostgreSQL (localhost:5432)...");

        // Clear existing if any
        if (existingCount > 0)
        {
            await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE benchmark_users RESTART IDENTITY;");
        }

        var users = BenchmarkDataHelper.GenerateUsers(count);

        // Batch insert in chunks of 2,000 for high speed
        const int batchSize = 2_000;
        for (int i = 0; i < users.Count; i += batchSize)
        {
            var chunk = users.Skip(i).Take(batchSize).ToList();
            await db.Users.AddRangeAsync(chunk);
            await db.SaveChangesAsync();
        }

        // Run ANALYZE to update PostgreSQL optimizer statistics
        await db.Database.ExecuteSqlRawAsync("ANALYZE benchmark_users;");
        Console.WriteLine($"      [Postgres] Successfully seeded and analyzed {count} rows.");
    }
}
