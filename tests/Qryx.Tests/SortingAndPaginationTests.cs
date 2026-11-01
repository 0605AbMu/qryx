using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Qryx.Core.Attributes;
using Qryx.Core.Contracts;
using Qryx.Expressions.Builders;
using Xunit;

namespace Qryx.Tests;

public class SortingAndPaginationTests
{
    public class StandardUser
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Age { get; set; }
    }

    public class CustomKeyEntity
    {
        [Key]
        public string AccountCode { get; set; } = string.Empty;
        public decimal Balance { get; set; }
    }

    public class NamedIdEntity
    {
        public Guid CustomerId { get; set; }
        public string Title { get; set; } = string.Empty;
    }

    public class WhitelistedEntity
    {
        public int Id { get; set; }

        [Sortable]
        public string Name { get; set; } = string.Empty;

        public string SecretNote { get; set; } = string.Empty;
    }

    public record TestRequest : QryxRequest<StandardUser>;

    [Fact]
    public void HasOrdering_ShouldDetect_Existing_OrderBy()
    {
        var data = new List<StandardUser>().AsQueryable();
        Assert.False(SortBuilder.HasOrdering(data));

        var ordered = data.OrderBy(x => x.Age);
        Assert.True(SortBuilder.HasOrdering(ordered));

        var orderedDesc = data.OrderByDescending(x => x.Age);
        Assert.True(SortBuilder.HasOrdering(orderedDesc));

        var filteredThenOrdered = data.Where(x => x.Age > 18).OrderBy(x => x.Name);
        Assert.True(SortBuilder.HasOrdering(filteredThenOrdered));

        var orderedThenFiltered = data.OrderBy(x => x.Name).Where(x => x.Age > 18);
        Assert.True(SortBuilder.HasOrdering(orderedThenFiltered));
    }

    [Fact]
    public void EnsureOrdering_ShouldPreserve_Existing_Ordering()
    {
        var data = new List<StandardUser>
        {
            new() { Id = 3, Name = "Charlie", Age = 30 },
            new() { Id = 1, Name = "Alice", Age = 20 },
            new() { Id = 2, Name = "Bob", Age = 25 },
        }.AsQueryable();

        var preOrdered = data.OrderBy(x => x.Age);
        var result = SortBuilder.EnsureOrdering(preOrdered);

        // Pre-existing order by Age should be maintained (Alice, Bob, Charlie)
        var list = result.ToList();
        Assert.Equal("Alice", list[0].Name);
        Assert.Equal("Bob", list[1].Name);
        Assert.Equal("Charlie", list[2].Name);
    }

    [Fact]
    public void EnsureOrdering_ShouldFallbackTo_Id_WhenNoOrderingExists()
    {
        var data = new List<StandardUser>
        {
            new() { Id = 3, Name = "Charlie", Age = 30 },
            new() { Id = 1, Name = "Alice", Age = 20 },
            new() { Id = 2, Name = "Bob", Age = 25 },
        }.AsQueryable();

        var result = SortBuilder.EnsureOrdering(data);
        var list = result.ToList();

        // Should be ordered by Id ascending: 1, 2, 3
        Assert.Equal(1, list[0].Id);
        Assert.Equal(2, list[1].Id);
        Assert.Equal(3, list[2].Id);
    }

    [Fact]
    public void EnsureOrdering_ShouldDetect_KeyAttribute_OnCustomKeyEntity()
    {
        var data = new List<CustomKeyEntity>
        {
            new() { AccountCode = "Z99", Balance = 100 },
            new() { AccountCode = "A01", Balance = 200 },
            new() { AccountCode = "M50", Balance = 300 },
        }.AsQueryable();

        var result = SortBuilder.EnsureOrdering(data);
        var list = result.ToList();

        // Should be ordered by AccountCode ascending: A01, M50, Z99
        Assert.Equal("A01", list[0].AccountCode);
        Assert.Equal("M50", list[1].AccountCode);
        Assert.Equal("Z99", list[2].AccountCode);
    }

    [Fact]
    public void EnsureOrdering_ShouldDetect_NamedEntityId()
    {
        var guid1 = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var guid2 = Guid.Parse("00000000-0000-0000-0000-000000000002");

        var data = new List<NamedIdEntity>
        {
            new() { CustomerId = guid2, Title = "Beta" },
            new() { CustomerId = guid1, Title = "Alpha" },
        }.AsQueryable();

        var result = SortBuilder.EnsureOrdering(data);
        var list = result.ToList();

        Assert.Equal(guid1, list[0].CustomerId);
        Assert.Equal(guid2, list[1].CustomerId);
    }

    [Fact]
    public void ApplyOffset_ShouldEnsureOrdering_Automatically()
    {
        var data = new List<StandardUser>
        {
            new() { Id = 3, Name = "C" },
            new() { Id = 1, Name = "A" },
            new() { Id = 2, Name = "B" },
        }.AsQueryable();

        // Apply offset without pre-ordering
        var paged = PaginationBuilder.ApplyOffset(data, page: 1, pageSize: 2);
        var list = paged.ToList();

        Assert.Equal(2, list.Count);
        Assert.Equal(1, list[0].Id);
        Assert.Equal(2, list[1].Id);
    }

    [Fact]
    public void ApplyKeyset_ShouldWork_EvenIfCursorProperty_IsNotInSortableWhitelist()
    {
        var data = new List<WhitelistedEntity>
        {
            new() { Id = 1, Name = "A", SecretNote = "x" },
            new() { Id = 2, Name = "B", SecretNote = "y" },
            new() { Id = 3, Name = "C", SecretNote = "z" },
        }.AsQueryable();

        // Id is not explicitly in [Sortable(nameof(Name))], but cursor pagination must always work
        var keyset = PaginationBuilder.ApplyKeyset(data, afterCursor: 1, pageSize: 2, cursorProperty: "Id");
        var list = keyset.ToList();

        Assert.Equal(2, list.Count);
        Assert.Equal(2, list[0].Id);
        Assert.Equal(3, list[1].Id);
    }

    [Fact]
    public void OptIn_Whitelist_ShouldBlock_NonSortable_Properties()
    {
        var data = new List<WhitelistedEntity>
        {
            new() { Id = 1, Name = "B", SecretNote = "Secret2" },
            new() { Id = 2, Name = "A", SecretNote = "Secret1" },
        }.AsQueryable();

        // Sorting by "Name" is whitelisted -> Allowed
        var sortedByName = SortBuilder.Apply(data, "Name", isDescending: false, throwOnInvalidProperty: true);
        var listName = sortedByName.ToList();
        Assert.Equal("A", listName[0].Name);

        // Sorting by "SecretNote" is NOT whitelisted -> Throws if throwOnInvalidProperty: true
        Assert.Throws<ArgumentException>(() =>
            SortBuilder.Apply(data, "SecretNote", isDescending: false, throwOnInvalidProperty: true));

        // When throwOnInvalidProperty is false, it safely ignores unwhitelisted field and leaves original
        var ignored = SortBuilder.Apply(data, "SecretNote", isDescending: false, throwOnInvalidProperty: false);
        Assert.NotNull(ignored);
    }

    [Fact]
    public void Sortable_IsSingleSourceOfTruth_UnannotatedProperty_CannotBeSorted()
    {
        var data = new List<StandardUser>
        {
            new() { Id = 2, Name = "B", Age = 30 },
            new() { Id = 1, Name = "A", Age = 20 },
        }.AsQueryable();

        // StandardUser has NO [Sortable] attributes on any property.
        // Therefore, sorting by "Age" must be rejected!
        Assert.Throws<ArgumentException>(() =>
            SortBuilder.Apply(data, "Age", isDescending: false, throwOnInvalidProperty: true));
    }

    [Fact]
    public void IntegerOverflow_InPagination_ShouldNotThrow()
    {
        var data = new List<StandardUser>().AsQueryable();

        // Should not throw ArgumentOutOfRangeException
        var paged = PaginationBuilder.ApplyOffset(data, page: int.MaxValue, pageSize: 100);
        var list = paged.ToList();
        Assert.Empty(list);
    }

    public class TestDbContext : Microsoft.EntityFrameworkCore.DbContext
    {
        public TestDbContext(Microsoft.EntityFrameworkCore.DbContextOptions<TestDbContext> options) : base(options) { }
        public Microsoft.EntityFrameworkCore.DbSet<StandardUser> Users => Set<StandardUser>();
    }

    [Fact]
    public async Task ToPagedResultAsync_WithoutSortBy_ShouldOrderById_Deterministically()
    {
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        using var db = new TestDbContext(options);
        db.Users.AddRange(
            new StandardUser { Id = 3, Name = "Charlie", Age = 30 },
            new StandardUser { Id = 1, Name = "Alice", Age = 20 },
            new StandardUser { Id = 2, Name = "Bob", Age = 25 });
        await db.SaveChangesAsync();

        // Request with NO SortBy
        var request = new TestRequest { Page = 1, PageSize = 2, SortBy = null };

        var result = await Qryx.EntityFramework.Extensions.QryxExtensions.ToPagedResultAsync(db.Users, request);

        Assert.Equal(3, result.Total);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(1, result.Items[0].Id);
        Assert.Equal(2, result.Items[1].Id);
    }

    [Fact]
    public async Task ToPagedResultAsync_WithPreExistingOrderBy_ShouldPreserveDeveloperOrder()
    {
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        using var db = new TestDbContext(options);
        db.Users.AddRange(
            new StandardUser { Id = 3, Name = "Charlie", Age = 30 },
            new StandardUser { Id = 1, Name = "Alice", Age = 20 },
            new StandardUser { Id = 2, Name = "Bob", Age = 25 });
        await db.SaveChangesAsync();

        // Developer applied OrderBy(x => x.Age) explicitly
        var query = db.Users.OrderBy(x => x.Age);
        var request = new TestRequest { Page = 1, PageSize = 2, SortBy = null };

        var result = await Qryx.EntityFramework.Extensions.QryxExtensions.ToPagedResultAsync(query, request);

        Assert.Equal(2, result.Items.Count);
        Assert.Equal("Alice", result.Items[0].Name); // Age 20
        Assert.Equal("Bob", result.Items[1].Name);   // Age 25
    }

    [Fact]
    public async Task ToPagedResultAsync_KeysetPagination_ShouldSeekAndOrderDeterministically()
    {
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<TestDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        using var db = new TestDbContext(options);
        db.Users.AddRange(
            new StandardUser { Id = 1, Name = "Alice", Age = 20 },
            new StandardUser { Id = 2, Name = "Bob", Age = 25 },
            new StandardUser { Id = 3, Name = "Charlie", Age = 30 });
        await db.SaveChangesAsync();

        var request = new TestRequest { AfterCursor = "1", PageSize = 2, CursorProperty = "Id" };

        var result = await Qryx.EntityFramework.Extensions.QryxExtensions.ToPagedResultAsync(db.Users, request);

        Assert.Equal(2, result.Items.Count);
        Assert.Equal(2, result.Items[0].Id);
        Assert.Equal(3, result.Items[1].Id);
        Assert.False(result.HasNext);
    }

    public class MultiSortEntity
    {
        public int Id { get; set; }

        [Sortable]
        public string Department { get; set; } = string.Empty;

        [Sortable]
        public decimal Salary { get; set; }

        [Sortable]
        public string Name { get; set; } = string.Empty;

        public string SecretData { get; set; } = string.Empty;
    }

    [Fact]
    public void MultiSort_ShouldApply_OrderBy_ThenBy_Correctly()
    {
        var data = new List<MultiSortEntity>
        {
            new() { Id = 1, Department = "IT", Salary = 5000, Name = "Zack" },
            new() { Id = 2, Department = "IT", Salary = 5000, Name = "Alice" },
            new() { Id = 3, Department = "HR", Salary = 6000, Name = "Bob" },
            new() { Id = 4, Department = "IT", Salary = 7000, Name = "Charlie" },
        }.AsQueryable();

        // Multi-sort: Department asc, Salary desc, Name asc
        var sorted = SortBuilder.Apply(data, "Department asc, Salary desc, Name asc");
        var list = sorted.ToList();

        // 1. HR first: Bob (Salary 6000)
        Assert.Equal("Bob", list[0].Name);

        // 2. IT with highest salary first: Charlie (Salary 7000)
        Assert.Equal("Charlie", list[1].Name);

        // 3. IT with Salary 5000: Alice before Zack (Name asc)
        Assert.Equal("Alice", list[2].Name);
        Assert.Equal("Zack", list[3].Name);
    }

    [Fact]
    public void MultiSort_WithUnwhitelistedField_ShouldSafelySkipInvalidAndApplyValid()
    {
        var data = new List<MultiSortEntity>
        {
            new() { Id = 1, Department = "IT", Salary = 5000, Name = "Zack", SecretData = "1" },
            new() { Id = 2, Department = "HR", Salary = 5000, Name = "Alice", SecretData = "2" },
        }.AsQueryable();

        // SecretData is not [Sortable], Department is [Sortable]
        var sorted = SortBuilder.Apply(data, "SecretData desc, Department asc", throwOnInvalidProperty: false);
        var list = sorted.ToList();

        // SecretData is ignored, sorted by Department asc -> HR (Alice), then IT (Zack)
        Assert.Equal("Alice", list[0].Name);
        Assert.Equal("Zack", list[1].Name);
    }
}
