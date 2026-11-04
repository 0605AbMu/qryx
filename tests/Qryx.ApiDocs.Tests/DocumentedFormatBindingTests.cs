using System.Net;
using System.Text.Json;
using Qryx.ApiDocs.Tests.Infrastructure;
using static Qryx.ApiDocs.Tests.Infrastructure.OpenApiJson;

namespace Qryx.ApiDocs.Tests;

/// <summary>End-to-end: the formats the documents describe are the formats the endpoints bind.</summary>
public sealed class DocumentedFormatBindingTests(ApiDocsHost host) : IClassFixture<ApiDocsHost>
{
    [Theory]
    [InlineData("/minimal/people")]
    [InlineData("/mvc/people")]
    public async Task DocumentedFormats_Bind_AndFilter(string endpoint)
    {
        // Seed: Age = 18 + 2i, Status = i % 3 (0 Active, 1 Pending, 2 Blocked), Id = i.
        // ageRange 20..35 -> i 1..8; Active/Pending -> i 1,3,4,6,7; ids 1,2 -> only i = 1 matches all three.
        var response = await host.Client.GetAsync($"{endpoint}?ageRange=20..35&statusList=Active,Pending&ids=1&ids=2");
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Expected 200, got {(int)response.StatusCode}: {body}");
        using var json = JsonDocument.Parse(body);
        var ids = json.RootElement.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("id").GetInt32()).ToArray();
        Assert.Equal([1], ids);
    }

    [Theory]
    [InlineData(ApiDocsHost.SwaggerPath, "/minimal/people")]
    [InlineData(ApiDocsHost.SwaggerPath, "/mvc/people")]
    [InlineData(ApiDocsHost.OpenApiPath, "/minimal/people")]
    [InlineData(ApiDocsHost.OpenApiPath, "/mvc/people")]
    public async Task DocumentedExamples_AreAccepted(string documentPath, string endpoint)
    {
        var document = await host.GetDocumentAsync(documentPath);
        var examples = GetParameters(document, endpoint)
            .Select(p => p!.AsObject())
            .Where(p => p["example"] != null)
            .Select(p => $"{p["name"]}={Uri.EscapeDataString((string)p["example"]!)}")
            .ToArray();
        Assert.NotEmpty(examples);

        var response = await host.Client.GetAsync($"{endpoint}?{string.Join('&', examples)}");

        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"Expected 200 for {string.Join('&', examples)}, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    [Theory]
    [InlineData("/minimal/people")]
    [InlineData("/mvc/people")]
    public async Task EnumNamesAndCamelCaseKeys_Bind(string endpoint)
    {
        var response = await host.Client.GetAsync(
            $"{endpoint}?status=Pending&statuses=Pending&statuses=Blocked&excludedIds=1&sortBy=age%20desc&includeTotal=true");
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Expected 200, got {(int)response.StatusCode}: {body}");
        using var json = JsonDocument.Parse(body);
        // Pending: i = 1, 4, 7, 10; excluding Id 1 -> 10, 7, 4 (age desc)
        var ids = json.RootElement.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("id").GetInt32()).ToArray();
        Assert.Equal([10, 7, 4], ids);
        Assert.Equal(3, json.RootElement.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task MvcListProperties_BindRepeatedKeys()
    {
        var response = await host.Client.GetAsync("/mvc/people/list?idList=1&idList=2&idList=3&excludedStatuses=Blocked");
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Expected 200, got {(int)response.StatusCode}: {body}");
        using var json = JsonDocument.Parse(body);
        var ids = json.RootElement.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("id").GetInt32()).ToArray();
        Assert.Equal([1, 3], ids);
    }
}
