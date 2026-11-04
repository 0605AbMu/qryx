using Qryx.Core.Exceptions;
using Qryx.Core.Types;
using Qryx.EntityFramework.Extensions;
using Qryx.Expressions.Builders;
using Qryx.Generated;

namespace Qryx.SourceGenerator.Tests;

/// <summary>
/// The generated <c>ApplyGenerated</c> must return exactly the rows (and order) of the dynamic engine
/// (<c>ApplyQryx</c> + offset pagination) for every operator.
/// </summary>
public class ParityTests
{
    private const int PageSize = 100;

    public static TheoryData<string, PersonQuery> Cases()
    {
        var created = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var cases = new (string Name, PersonQuery Query)[]
        {
            ("Equals enum", new() { Status = PersonStatus.Pending }),
            ("NotEquals enum", new() { ExcludeStatus = PersonStatus.Active }),
            ("GreaterThan", new() { AgeAbove = 40 }),
            ("GreaterThanOrEqual prefix", new() { MinAge = 40 }),
            ("LessThan", new() { AgeBelow = 30 }),
            ("LessThanOrEqual prefix", new() { MaxAge = 30 }),
            ("Min/Max combined", new() { MinAge = 25, MaxAge = 45 }),
            ("From/To DateTime prefix", new() { FromCreatedAt = created.AddDays(5), ToCreatedAt = created.AddDays(20) }),
            ("int value vs long column", new() { BalanceAtLeast = 25_000 }),
            ("int value vs int? column", new() { ScoreEquals = 9 }),
            ("decimal", new() { SalaryAtLeast = 5000m }),
            ("Guid", new() { ExternalId = new Guid(7, 0, 0, new byte[8]) }),
            ("bool", new() { Active = true }),
            ("nested path", new() { DepartmentName = "Sales" }),
            ("nested path, case-insensitive", new() { CompanyName = "Globex" }),
            ("StartsWith multi-target", new() { FullName = "Bo" }),
            ("StartsWith multi-target hits last name", new() { FullName = "Sm" }),
            ("StartsWith invariant", new() { FirstNameCi = "AL" }),
            ("EndsWith", new() { LastNameEnds = "son" }),
            ("EndsWith invariant", new() { LastNameEndsCi = "SMITH" }),
            ("EndsWith invariant with wildcard chars", new() { LastNameEndsCi = "%real" }),
            ("Contains", new() { NotesContains = "urgent" }),
            ("Contains invariant multi-target", new() { Search = "B" }),
            ("Contains invariant underscore", new() { Search = "o_h" }),
            ("In array", new() { Statuses = [PersonStatus.Active, PersonStatus.Blocked] }),
            ("In List", new() { Ages = [25, 32, 39, 46] }),
            ("In QryxList", new() { Ids = new QryxList<int>([1, 5, 9, 33]) }),
            ("NotIn IEnumerable", new() { ExcludeStatuses = new HashSet<PersonStatus> { PersonStatus.Pending } }),
            ("Between both bounds", new() { AgeRange = new Range<int> { From = 25, To = 45 } }),
            ("Between lower bound", new() { AgeRange = new Range<int> { From = 50 } }),
            ("Between upper bound", new() { AgeRange = new Range<int> { To = 30 } }),
            ("Between non-nullable DateTime range", new() { CreatedRange = new Range<DateTime> { From = created.AddDays(3), To = created.AddDays(9) } }),
            ("Between int range vs long column", new() { BalanceRange = new Range<int> { From = 5_000, To = 12_000 } }),
            ("IsNull true", new() { NotDeleted = true }),
            ("IsNull false", new() { NotDeleted = false }),
            ("IsNotNull true", new() { HasEmail = true }),
            ("IsNotNull false", new() { HasEmail = false }),
            ("IsNotNull non-bool value", new() { DeletedMarker = "yes" }),
            ("runtime fallback string -> enum", new() { StatusText = "blocked" }),
            ("runtime fallback Fts", new() { FullText = "URGENT" }),
            ("runtime fallback JsonContains", new() { NotesJson = "low" }),
            ("combined", new() { MinAge = 20, Statuses = [PersonStatus.Active, PersonStatus.Pending], FullName = "A", NotDeleted = true }),
            ("sorted descending", new() { MinAge = 20, SortBy = "Age", IsDescending = true }),
            ("multi-field sort", new() { SortBy = "FirstName asc, Age desc" }),
            ("paged", new() { MaxAge = 60, Page = 2, PageSize = 7 }),
        };

        var data = new TheoryData<string, PersonQuery>();
        foreach (var (name, query) in cases)
        {
            data.Add(name, query.PageSize == 20 ? query with { PageSize = PageSize } : query);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Generated_Matches_Dynamic_Engine(string name, PersonQuery query)
    {
        using var context = TestData.CreateInMemory();

        var generated = context.People.ApplyGenerated(query).Select(p => p.Id).ToList();
        var dynamic = DynamicPage(context, query);

        Assert.True(dynamic.SequenceEqual(generated), $"{name}: dynamic [{string.Join(",", dynamic)}] != generated [{string.Join(",", generated)}]");

        // Every case must actually filter (or page): neither empty nor the whole table.
        Assert.NotEmpty(generated);
        if (query.SortBy is null)
            Assert.True(generated.Count < TestData.PeopleCount, $"{name}: filter did not restrict the result");
    }

    [Theory]
    [MemberData(nameof(EmptyValueCases))]
    public void Empty_Values_Are_Skipped(string name, PersonQuery query)
    {
        using var context = TestData.CreateInMemory();

        var generated = context.People.ApplyGenerated(query).Select(p => p.Id).ToList();

        Assert.True(generated.Count == TestData.PeopleCount, name);
        Assert.Equal(DynamicPage(context, query), generated);
    }

    public static TheoryData<string, PersonQuery> EmptyValueCases() => new()
    {
        { "empty string", new() { FullName = "", DepartmentName = "", Search = "", PageSize = PageSize } },
        { "empty array", new() { Statuses = [], PageSize = PageSize } },
        { "empty list", new() { Ages = [], Ids = [], ExcludeStatuses = [], PageSize = PageSize } },
        { "range without bounds", new() { AgeRange = new Range<int>(), CreatedRange = default, PageSize = PageSize } },
        { "runtime fallback empty", new() { StatusText = "", FullText = null, PageSize = PageSize } },
    };

    [Fact]
    public void Keyset_Pagination_Matches_Dynamic_Engine()
    {
        using var context = TestData.CreateInMemory();
        // Cursor continuing after Id 10 in the default (primary-key) ordering, as a client receives it in nextCursor
        var plan = SortBuilder.ResolveSort<Person>(sortBy: null, isDescending: false, typeof(PersonQuery));
        var cursor = PaginationBuilder.CreateCursor(context.People.Single(p => p.Id == 10), plan)!;
        var query = new PersonQuery { MinAge = 20, AfterCursor = cursor, PageSize = 5 };

        var generated = context.People.ApplyGenerated(query).Select(p => p.Id).ToList();
        var dynamic = PaginationBuilder
            .ApplyKeyset(FilterBuilder.Apply(context.People, query), cursor, 5, null, false, typeof(PersonQuery))
            .Select(p => p.Id)
            .ToList();

        Assert.Equal(dynamic, generated);
        Assert.Equal(5, generated.Count);
        Assert.All(generated, id => Assert.True(id > 10));
    }

    [Fact]
    public void In_Above_Limit_Throws_Like_Dynamic_Engine()
    {
        using var context = TestData.CreateInMemory();
        var tooMany = new PersonQuery { Ages = Enumerable.Range(0, 1001).ToList() };
        var tooManyEnumerable = new PersonQuery { Ids = new QryxList<int>(Enumerable.Range(0, 1001)) };
        var atLimit = new PersonQuery { Statuses = Enumerable.Repeat(PersonStatus.Active, 1000).ToArray() };

        var generatedError = Assert.Throws<QryxRequestException>(() => context.People.ApplyGenerated(tooMany));
        var dynamicError = Assert.Throws<QryxRequestException>(() => context.People.ApplyQryx(tooMany));
        Assert.Equal(dynamicError.ParamName, generatedError.ParamName);
        Assert.Equal(dynamicError.Reason, generatedError.Reason);

        Assert.Throws<QryxRequestException>(() => context.People.ApplyGenerated(tooManyEnumerable));
        Assert.NotEmpty(context.People.ApplyGenerated(atLimit).ToList());
    }

    [Fact]
    public void Request_Without_Filters_Gets_Sorting_And_Pagination()
    {
        using var context = TestData.CreateInMemory();
        var query = new PlainPersonQuery { SortBy = "Age", IsDescending = true, Page = 2, PageSize = 5 };

        var generated = context.People.ApplyGenerated(query).Select(p => p.Id).ToList();
        var dynamic = PaginationBuilder.ApplyOffset(context.People.ApplyQryx(query), 2, 5).Select(p => p.Id).ToList();

        Assert.Equal(dynamic, generated);
        Assert.Equal(5, generated.Count);
    }

    [Fact]
    public void Derived_Dto_Without_Own_Filters_Uses_Inherited_Filters()
    {
        using var context = TestData.CreateInMemory();
        var query = new DerivedPersonQuery { MinAge = 40, Statuses = [PersonStatus.Active], PageSize = PageSize };

        var generated = context.People.ApplyGenerated(query).Select(p => p.Id).ToList();

        Assert.Equal(DynamicPage(context, query), generated);
        Assert.NotEmpty(generated);
    }

    [Fact]
    public void Null_Request_Returns_Query_Unchanged()
    {
        using var context = TestData.CreateInMemory();
        IQueryable<Person> source = context.People;

        Assert.Same(source, source.ApplyGenerated((PersonQuery)null!));
    }

    [Fact]
    public void Global_Namespace_Dto_Is_Generated()
    {
        using var context = TestData.CreateInMemory();
        var query = new GlobalNamespacePersonQuery { MinAge = 50, PageSize = PageSize };

        var generated = context.People.ApplyGenerated(query).Select(p => p.Id).ToList();

        Assert.Equal(DynamicPage(context, query), generated);
        Assert.NotEmpty(generated);
    }

    private static List<int> DynamicPage<TQuery>(PeopleContext context, TQuery query)
        where TQuery : Qryx.Core.Contracts.QryxRequest<Person>
    {
        Qryx.Core.Contracts.IQryxRequest<Person> request = query;
        return PaginationBuilder
            .ApplyOffset(context.People.ApplyQryx(request), request.Page, request.PageSize)
            .Select(p => p.Id)
            .ToList();
    }
}
