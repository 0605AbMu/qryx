using Microsoft.EntityFrameworkCore;
using Qryx.Core.Types;
using Qryx.Generated;
using Xunit.Abstractions;

namespace Qryx.SourceGenerator.Tests;

/// <summary>
/// Generated filters translated by Npgsql (<c>ToQueryString()</c>, no database needed): every value must be a SQL
/// parameter, never an inlined literal, and invariant string operators must use native ILIKE with ESCAPE '\'.
/// </summary>
public class SqlTranslationTests(ITestOutputHelper output)
{
    [Fact]
    public void Values_Are_Sent_As_Parameters()
    {
        using var context = new NpgsqlPeopleContext();
        var query = new PersonQuery
        {
            Status = PersonStatus.Blocked,
            MinAge = 31,
            MaxAge = 59,
            BalanceAtLeast = 77_777,
            DepartmentName = "Marketing",
            FullName = "Zed",
            NotesContains = "needle",
            Statuses = [PersonStatus.Active, PersonStatus.Pending],
            Ages = [41, 42],
            ExcludeStatuses = [PersonStatus.Blocked],
            AgeRange = new Range<int> { From = 33, To = 57 },
            ExternalId = new Guid(4242, 0, 0, new byte[8]),
            NotDeleted = true
        };

        var sql = context.People.ApplyGenerated(query).ToQueryString();
        output.WriteLine(sql);
        var body = SqlBody(sql);

        foreach (var literal in new[] { "31", "59", "77777", "Marketing", "Zed", "needle", "41", "42", "33", "57", "00001092" })
        {
            Assert.DoesNotContain(literal, body);
        }

        Assert.Contains("= ANY (@", body);
        Assert.Contains("WHERE", body);
        Assert.True(CountParameters(body) >= 10, body);
        Assert.Contains("IS NULL", body);
    }

    [Fact]
    public void Sql_Text_Does_Not_Depend_On_Values()
    {
        using var context = new NpgsqlPeopleContext();

        var first = SqlBody(context.People.ApplyGenerated(new PersonQuery { MinAge = 20, FullName = "A", Statuses = [PersonStatus.Active] }).ToQueryString());
        var second = SqlBody(context.People.ApplyGenerated(new PersonQuery { MinAge = 45, FullName = "Bob", Statuses = [PersonStatus.Blocked, PersonStatus.Pending] }).ToQueryString());

        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData("first", "ILIKE @")]
    [InlineData("last", "ILIKE @")]
    [InlineData("search", "ILIKE @")]
    public void Invariant_String_Operators_Use_ILike_With_Escape(string which, string expectedOperator)
    {
        using var context = new NpgsqlPeopleContext();
        var query = which switch
        {
            "first" => new PersonQuery { FirstNameCi = "a_b%c\\" },
            "last" => new PersonQuery { LastNameEndsCi = "a_b%c\\" },
            _ => new PersonQuery { Search = "a_b%c\\" }
        };

        var sql = context.People.ApplyGenerated(query).ToQueryString();
        output.WriteLine(sql);
        var body = SqlBody(sql);

        Assert.Contains(expectedOperator, body);
        Assert.Contains("ESCAPE '\\'", body);
        Assert.DoesNotContain("upper(", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lower(", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("a_b", body);

        // The escaped pattern travels as the parameter value: a\_b\%c\\ (+ wildcard)
        Assert.Matches(@"ILIKE @\w+ ESCAPE", body);
        Assert.Contains("a\\_b\\%c\\\\", sql);
    }

    [Fact]
    public void Case_Sensitive_String_Operators_Use_Like_Without_Upper()
    {
        using var context = new NpgsqlPeopleContext();

        var body = SqlBody(context.People.ApplyGenerated(new PersonQuery { FullName = "Al" }).ToQueryString());

        Assert.Contains("LIKE", body);
        Assert.DoesNotContain("ILIKE", body);
        Assert.DoesNotContain("upper(", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" OR ", body);
    }

    [Fact]
    public void Generated_Sql_Matches_Dynamic_Sql_Shape_For_Runtime_Fallbacks()
    {
        using var context = new NpgsqlPeopleContext();

        // string -> enum conversion and Fts are delegated to FilterBuilder.BuildPropertyPredicate.
        var body = SqlBody(context.People.ApplyGenerated(new PersonQuery { StatusText = "Blocked", FullText = "needle" }).ToQueryString());

        Assert.Contains("websearch_to_tsquery", body);
        Assert.DoesNotContain("needle", body);
        Assert.DoesNotContain("= 2", body);
    }

    private static string SqlBody(string sql) =>
        string.Join("\n", sql.Split('\n').Where(static line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal)));

    private static int CountParameters(string body) =>
        body.Split('@').Length - 1;
}
