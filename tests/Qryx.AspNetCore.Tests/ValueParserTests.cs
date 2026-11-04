using Qryx.AspNetCore.Internal;
using Qryx.AspNetCore.Tests.Infrastructure;
using Qryx.Core.Types;

namespace Qryx.AspNetCore.Tests;

public sealed class ValueParserTests
{
    [Flags]
    public enum Permissions
    {
        None = 0,
        Read = 1,
        Write = 2
    }

    public static TheoryData<Type, string, object?> ValidValues => new()
    {
        { typeof(string), " raw ", " raw " },
        { typeof(int), " 42 ", 42 },
        { typeof(int?), "-7", -7 },
        { typeof(long), "9000000000", 9_000_000_000L },
        { typeof(decimal), "1.5", 1.5m },
        { typeof(double?), "2.25", 2.25d },
        { typeof(bool), "TRUE", true },
        { typeof(bool?), "false", false },
        { typeof(ItemStatus), "archived", ItemStatus.Archived },
        { typeof(ItemStatus?), "1", ItemStatus.Pending },
        { typeof(Permissions), "Read, Write", Permissions.Read | Permissions.Write },
        { typeof(Permissions), "3", Permissions.Read | Permissions.Write },
        { typeof(Guid), "6f9619ff-8b86-d011-b42d-00cf4fc964ff", Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff") },
        { typeof(DateOnly), "2024-02-29", new DateOnly(2024, 2, 29) },
        { typeof(DateOnly?), "2024-02-29", new DateOnly(2024, 2, 29) },
        { typeof(TimeOnly), "13:45", new TimeOnly(13, 45) },
        { typeof(TimeSpan), "01:30:00", TimeSpan.FromMinutes(90) },
        { typeof(DateTimeOffset), "2024-01-01T10:00:00+05:00", new DateTimeOffset(2024, 1, 1, 10, 0, 0, TimeSpan.FromHours(5)) },
        { typeof(Range<int>), "20..35", new Range<int> { From = 20, To = 35 } },
        { typeof(Range<int>?), "..35", new Range<int> { To = 35 } },
        { typeof(Range<decimal>), "1.5..", new Range<decimal> { From = 1.5m } },
        { typeof(Range<DateOnly>), "2024-01-01..2024-12-31", new Range<DateOnly> { From = new DateOnly(2024, 1, 1), To = new DateOnly(2024, 12, 31) } },
        { typeof(Uri), "https://example.com/a", new Uri("https://example.com/a") },
    };

    [Theory]
    [MemberData(nameof(ValidValues))]
    public void Parses_SupportedTypes_WithInvariantCulture(Type type, string input, object? expected)
    {
        var parser = QryxValueParser.GetParser(type);

        Assert.NotNull(parser);
        Assert.True(parser(input, out var result), $"'{input}' should parse as {type}.");
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(typeof(int), "abc")]
    [InlineData(typeof(int), "1.5")]
    [InlineData(typeof(decimal), "1,5x")]
    [InlineData(typeof(bool), "yes")]
    [InlineData(typeof(ItemStatus), "Bogus")]
    [InlineData(typeof(ItemStatus), "99")]
    [InlineData(typeof(Guid), "not-a-guid")]
    [InlineData(typeof(DateOnly), "2024-13-01")]
    [InlineData(typeof(Range<int>), "35..20")]
    [InlineData(typeof(Range<int>), "20")]
    [InlineData(typeof(Range<int>), "a..b")]
    public void Rejects_InvalidValues(Type type, string input)
    {
        var parser = QryxValueParser.GetParser(type)!;

        Assert.False(parser(input, out _));
    }

    [Fact]
    public void Parser_IsCachedPerType()
    {
        Assert.Same(QryxValueParser.GetParser(typeof(Range<int>)), QryxValueParser.GetParser(typeof(Range<int>)));
    }

    [Fact]
    public void Parsing_IsCultureIndependent()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.True(QryxValueParser.GetParser(typeof(decimal))!("1.5", out var value));
            Assert.Equal(1.5m, value);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void ErrorMessage_DescribesExpectedFormat()
    {
        Assert.Contains("one of: Active, Pending, Archived", QryxValueParser.FormatInvalidValue("status", "x", typeof(ItemStatus?)));
        Assert.Contains("'from..to'", QryxValueParser.FormatInvalidValue("age", "x", typeof(Range<int>?)));
        Assert.Contains("...", QryxValueParser.FormatInvalidValue("q", new string('a', 200), typeof(int)));
    }
}
