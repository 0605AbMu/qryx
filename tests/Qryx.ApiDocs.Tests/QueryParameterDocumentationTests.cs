using Qryx.ApiDocs.Tests.Infrastructure;
using static Qryx.ApiDocs.Tests.Infrastructure.OpenApiJson;

namespace Qryx.ApiDocs.Tests;

/// <summary>
/// Asserts the query parameters that Swashbuckle (<c>AddQryxFilters</c>) and Microsoft.AspNetCore.OpenApi
/// (<c>AddQryxTransformers</c>) document for Qryx DTOs bound by Minimal APIs and MVC controllers.
/// </summary>
public sealed class QueryParameterDocumentationTests(ApiDocsHost host) : IClassFixture<ApiDocsHost>
{
    private static readonly string[] StandardNames = ["page", "pageSize", "sortBy", "isDescending", "afterCursor", "includeTotal"];

    private static readonly string[] PersonFilterNames =
    [
        "minAge", "firstNameStarts", "fullName", "lastName", "statusName", "ageRange", "salaryRange", "createdRange",
        "statusList", "ids", "excludedIds", "status", "statuses", "createdOn", "code", "fromCreatedAt", "toUpdatedAt",
        "emailMissing"
    ];

    private static readonly string[] PersonStatusNames = ["Active", "Pending", "Blocked"];

    /// <summary>(document, endpoint) for the <see cref="PersonQuery"/> endpoints.</summary>
    public static TheoryData<string, string> PersonEndpoints => new()
    {
        { ApiDocsHost.SwaggerPath, "/minimal/people" },
        { ApiDocsHost.SwaggerPath, "/mvc/people" },
#if NET10_0_OR_GREATER // Qryx.OpenApi (Microsoft.AspNetCore.OpenApi transformers) targets net10.0 only
        { ApiDocsHost.OpenApiPath, "/minimal/people" },
        { ApiDocsHost.OpenApiPath, "/mvc/people" }
#endif
    };

    /// <summary>(document, endpoint, isMvc) for the <see cref="PersonQuery"/> endpoints.</summary>
    public static TheoryData<string, string, bool> PersonEndpointsWithKind => new()
    {
        { ApiDocsHost.SwaggerPath, "/minimal/people", false },
        { ApiDocsHost.SwaggerPath, "/mvc/people", true },
#if NET10_0_OR_GREATER // Qryx.OpenApi (Microsoft.AspNetCore.OpenApi transformers) targets net10.0 only
        { ApiDocsHost.OpenApiPath, "/minimal/people", false },
        { ApiDocsHost.OpenApiPath, "/mvc/people", true }
#endif
    };

    /// <summary>(document, endpoint) for the <c>[NoTotal]</c> <see cref="PersonFastQuery"/> endpoints.</summary>
    public static TheoryData<string, string> NoTotalEndpoints => new()
    {
        { ApiDocsHost.SwaggerPath, "/minimal/people/fast" },
        { ApiDocsHost.SwaggerPath, "/mvc/people/fast" },
#if NET10_0_OR_GREATER // Qryx.OpenApi (Microsoft.AspNetCore.OpenApi transformers) targets net10.0 only
        { ApiDocsHost.OpenApiPath, "/minimal/people/fast" },
        { ApiDocsHost.OpenApiPath, "/mvc/people/fast" }
#endif
    };

    public static TheoryData<string> Documents => new()
    {
        ApiDocsHost.SwaggerPath,
#if NET10_0_OR_GREATER
        ApiDocsHost.OpenApiPath
#endif
    };

    [Theory]
    [MemberData(nameof(PersonEndpoints))]
    public async Task AllQryxParameters_ArePresent_CamelCase_Optional_AndDescribed(string documentPath, string endpoint)
    {
        var parameters = GetParametersByName(await host.GetDocumentAsync(documentPath), endpoint);

        var expected = StandardNames.Concat(PersonFilterNames).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, parameters.Keys.Order(StringComparer.Ordinal).ToArray());

        foreach (var (name, parameter) in parameters)
        {
            Assert.Equal("query", (string?)parameter["in"]);
            Assert.False(IsRequired(parameter), $"'{name}' must be optional.");
            Assert.False(string.IsNullOrWhiteSpace(Description(parameter)), $"'{name}' has no description.");
            Assert.False(name.Contains('.'), $"'{name}' is an expanded sub-parameter.");
            Assert.True(char.IsLower(name[0]), $"'{name}' is not camelCase.");
            Schema(parameter);
        }
    }

    [Theory]
    [MemberData(nameof(PersonEndpoints))]
    public async Task RemovedLegacyParameters_AreNotDocumented(string documentPath, string endpoint)
    {
        var names = GetParametersByName(await host.GetDocumentAsync(documentPath), endpoint).Keys;

        Assert.DoesNotContain(names, n => n.Equals("cursorProperty", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Equals("includeTotalCount", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [MemberData(nameof(PersonEndpoints))]
    public async Task StandardParameters_HaveSchemasDefaultsAndDescriptions(string documentPath, string endpoint)
    {
        var parameters = GetParametersByName(await host.GetDocumentAsync(documentPath), endpoint);

        var page = Schema(parameters["page"]);
        Assert.Equal("integer", Type(page));
        Assert.Equal("1", page["minimum"]!.ToJsonString());
        Assert.Equal("1", page["default"]!.ToJsonString());

        var pageSize = Schema(parameters["pageSize"]);
        Assert.Equal("integer", Type(pageSize));
        Assert.Equal(ApiDocsHost.MaxPageSize.ToString(), pageSize["maximum"]!.ToJsonString());
        Assert.Equal("20", pageSize["default"]!.ToJsonString());
        Assert.Contains($"clamped to {ApiDocsHost.MaxPageSize}", Description(parameters["pageSize"]));

        Assert.Equal("boolean", Type(Schema(parameters["isDescending"])));
        Assert.Equal("boolean", Type(Schema(parameters["includeTotal"])));
        Assert.Equal("string", Type(Schema(parameters["afterCursor"])));
        Assert.Contains("nextCursor", Description(parameters["afterCursor"]));
        Assert.Contains("total", Description(parameters["includeTotal"]));
    }

    [Theory]
    [MemberData(nameof(PersonEndpoints))]
    public async Task SortBy_ListsSortableFields(string documentPath, string endpoint)
    {
        var sortBy = GetParameter(await host.GetDocumentAsync(documentPath), endpoint, "sortBy");

        Assert.Equal("string", Type(Schema(sortBy)));
        var description = Description(sortBy)!;
        // Entity [Sortable] (Age, CreatedAt) + DTO [Sortable] (LastName), camelCase, sorted.
        Assert.Contains("Sortable fields: age, createdAt, lastName.", description);
        Assert.Contains("desc", description);
        Assert.DoesNotContain("firstName", description);
    }

    [Theory]
    [MemberData(nameof(PersonEndpoints))]
    public async Task RangeParameters_AreSingleStrings_WithExampleAndBoundType(string documentPath, string endpoint)
    {
        var document = await host.GetDocumentAsync(documentPath);

        foreach (var (name, boundType, example) in new[]
                 {
                     ("ageRange", "int", "20..35"),
                     ("salaryRange", "decimal", "10.5..99.9"),
                     ("createdRange", "date", "2024-01-01..2024-12-31")
                 })
        {
            var parameter = GetParameter(document, endpoint, name);
            var schema = Schema(parameter);
            Assert.Equal("string", Type(schema));
            Assert.Null(schema["properties"]);
            Assert.Equal(example, (string?)parameter["example"]);

            var description = Description(parameter)!;
            Assert.Contains("Inclusive range from..to, from.. or ..to", description);
            Assert.Contains($"of {boundType} ", description);
        }
    }

    [Theory]
    [MemberData(nameof(PersonEndpoints))]
    public async Task QryxList_IsCommaSeparatedArray(string documentPath, string endpoint)
    {
        var parameter = GetParameter(await host.GetDocumentAsync(documentPath), endpoint, "statusList");

        var schema = Schema(parameter);
        Assert.Equal("array", Type(schema));
        var items = schema["items"]!.AsObject();
        Assert.Equal("string", Type(items));
        Assert.Equal(PersonStatusNames, EnumValues(items));
        Assert.Equal("form", Style(parameter));
        Assert.False(Explode(parameter), "QryxList must be documented with explode=false (statusList=Active,Pending).");
        Assert.Contains("Comma-separated", Description(parameter));
    }

    [Theory]
    [MemberData(nameof(PersonEndpoints))]
    public async Task Arrays_AreExplodedFormArrays_WithElementSchemas(string documentPath, string endpoint)
    {
        var document = await host.GetDocumentAsync(documentPath);

        foreach (var (name, itemType, itemFormat) in new[] { ("ids", "integer", "int32"), ("excludedIds", "integer", "int64") })
        {
            var parameter = GetParameter(document, endpoint, name);
            var schema = Schema(parameter);
            Assert.Equal("array", Type(schema));
            Assert.Equal(itemType, Type(schema["items"]!.AsObject()));
            Assert.Equal(itemFormat, (string?)schema["items"]!["format"]);
            Assert.Equal("form", Style(parameter));
            Assert.True(Explode(parameter));
            Assert.Contains($"{name}=1&{name}=2", Description(parameter));
        }

        var statuses = GetParameter(document, endpoint, "statuses");
        var statusItems = Schema(statuses)["items"]!.AsObject();
        Assert.Equal("string", Type(statusItems));
        Assert.Equal(PersonStatusNames, EnumValues(statusItems));
        Assert.True(Explode(statuses));
    }

    [Theory]
    [MemberData(nameof(PersonEndpointsWithKind))]
    public async Task Enums_AreStringEnums_WithBindingNote(string documentPath, string endpoint, bool isMvc)
    {
        var parameter = GetParameter(await host.GetDocumentAsync(documentPath), endpoint, "status");

        var schema = Schema(parameter);
        Assert.Equal("string", Type(schema));
        Assert.Equal(PersonStatusNames, EnumValues(schema));

        var description = Description(parameter)!;
        Assert.Contains(isMvc ? "case-insensitive" : "case-sensitive", description);
        Assert.Contains("numeric values are also accepted", description);
    }

    [Theory]
    [MemberData(nameof(PersonEndpoints))]
    public async Task ScalarValueTypes_HaveCorrectFormats(string documentPath, string endpoint)
    {
        var parameters = GetParametersByName(await host.GetDocumentAsync(documentPath), endpoint);

        foreach (var (name, type, format) in new[]
                 {
                     ("createdOn", "string", "date"),
                     ("code", "string", "uuid"),
                     ("fromCreatedAt", "string", "date-time"),
                     ("toUpdatedAt", "string", "date-time"),
                     ("minAge", "integer", "int32"),
                     ("emailMissing", "boolean", null),
                     ("firstNameStarts", "string", null)
                 })
        {
            var schema = Schema(parameters[name]);
            Assert.Equal(type, Type(schema));
            Assert.Equal(format, (string?)schema["format"]);
        }
    }

    [Theory]
    [MemberData(nameof(PersonEndpoints))]
    public async Task FilterDescriptions_StateTheOperator(string documentPath, string endpoint)
    {
        var parameters = GetParametersByName(await host.GetDocumentAsync(documentPath), endpoint);

        Assert.StartsWith("Filter: Age >= value.", Description(parameters["minAge"]));
        Assert.StartsWith("Filter: FirstName starts with value (case-insensitive).", Description(parameters["firstNameStarts"]));
        Assert.StartsWith("Filter: FirstName or LastName starts with value (case-sensitive).", Description(parameters["fullName"]));
        Assert.StartsWith("Filter: LastName = value.", Description(parameters["lastName"]));
        Assert.StartsWith("Filter: Status in values.", Description(parameters["statusList"]));
        Assert.StartsWith("Filter: Id not in values.", Description(parameters["excludedIds"]));
        Assert.StartsWith("Filter: Age between from and to (inclusive).", Description(parameters["ageRange"]));
        Assert.StartsWith("Filter: CreatedAt >= value.", Description(parameters["fromCreatedAt"]));
        Assert.StartsWith("Filter: true: Email is null, false: Email is not null.", Description(parameters["emailMissing"]));
        Assert.Contains("One of: Active, Pending, Blocked (case-insensitive)", Description(parameters["statusName"]));
    }

    [Theory]
    [MemberData(nameof(NoTotalEndpoints))]
    public async Task NoTotalDto_DoesNotDocumentIncludeTotal(string documentPath, string endpoint)
    {
        var parameters = GetParametersByName(await host.GetDocumentAsync(documentPath), endpoint);

        Assert.Equal(
            new[] { "afterCursor", "ageRange", "isDescending", "page", "pageSize", "sortBy" },
            parameters.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.All(parameters.Values, p => Assert.False(IsRequired(p)));
        Assert.Equal("string", Type(Schema(parameters["ageRange"])));
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public async Task MvcListProperties_AreExplodedArrays(string documentPath)
    {
        var parameters = GetParametersByName(await host.GetDocumentAsync(documentPath), "/mvc/people/list");

        var idList = parameters["idList"];
        Assert.Equal("array", Type(Schema(idList)));
        Assert.Equal("integer", Type(Schema(idList)["items"]!.AsObject()));
        Assert.True(Explode(idList));
        Assert.False(IsRequired(idList));

        var excluded = parameters["excludedStatuses"];
        Assert.Equal(PersonStatusNames, EnumValues(Schema(excluded)["items"]!.AsObject()));
        Assert.StartsWith("Filter: Status not in values.", Description(excluded));
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public async Task ComponentSchemas_UsedOnlyByRewrittenParameters_AreRemoved(string documentPath)
    {
        var document = await host.GetDocumentAsync(documentPath);
        var components = ComponentSchemaNames(document).ToHashSet(StringComparer.Ordinal);

        // Range<T> object schemas (Swashbuckle) and an enum used only by a query parameter are gone ...
        Assert.DoesNotContain(components, c => c.EndsWith("Range", StringComparison.Ordinal));
        Assert.DoesNotContain("TicketPriority", components);
        // ... while schemas still used by response bodies are kept.
        Assert.Contains("Person", components);
        Assert.Contains("PersonStatus", components);

        // No dangling references.
        foreach (var reference in References(document).Distinct())
        {
            const string prefix = "#/components/schemas/";
            Assert.StartsWith(prefix, reference);
            Assert.Contains(reference[prefix.Length..], components);
        }
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public async Task TicketEnumFilter_IsInlineStringEnum(string documentPath)
    {
        var parameter = GetParameter(await host.GetDocumentAsync(documentPath), "/minimal/tickets/count", "priority");

        Assert.Equal(["Low", "High"], EnumValues(Schema(parameter)));
    }
}
