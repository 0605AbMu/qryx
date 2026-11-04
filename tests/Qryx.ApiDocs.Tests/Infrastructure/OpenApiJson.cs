using System.Text.Json.Nodes;

namespace Qryx.ApiDocs.Tests.Infrastructure;

/// <summary>Small helpers for reading OpenAPI JSON documents (3.0 from Swashbuckle, 3.1 from Microsoft.AspNetCore.OpenApi).</summary>
internal static class OpenApiJson
{
    public static JsonArray GetParameters(JsonObject document, string path)
    {
        var operation = document["paths"]?[path]?["get"]?.AsObject();
        Assert.True(operation != null, $"GET {path} is missing from the document.");
        return operation["parameters"]?.AsArray() ?? [];
    }

    public static Dictionary<string, JsonObject> GetParametersByName(JsonObject document, string path) =>
        GetParameters(document, path).Select(p => p!.AsObject()).ToDictionary(p => (string)p["name"]!, StringComparer.Ordinal);

    public static JsonObject GetParameter(JsonObject document, string path, string name)
    {
        var parameters = GetParametersByName(document, path);
        Assert.True(parameters.TryGetValue(name, out var parameter),
            $"Parameter '{name}' is missing on GET {path}. Present: {string.Join(", ", parameters.Keys)}");
        return parameter;
    }

    public static JsonObject Schema(JsonObject parameter)
    {
        var schema = parameter["schema"]?.AsObject();
        Assert.True(schema != null, $"Parameter '{parameter["name"]}' has no schema.");
        Assert.False(schema.ContainsKey("$ref"), $"Parameter '{parameter["name"]}' schema must be inline, got {schema.ToJsonString()}.");
        return schema;
    }

    /// <summary>Schema type as a single string ("string", "integer" ...); OpenAPI 3.1 may also write it as an array.</summary>
    public static string? Type(JsonObject schema) => schema["type"] switch
    {
        JsonArray types => string.Join(",", types.Select(t => (string)t!).Where(t => t != "null")),
        JsonNode node => (string?)node,
        null => null
    };

    public static string? Description(JsonObject parameter) => (string?)parameter["description"];

    public static bool IsRequired(JsonObject parameter) => parameter["required"] is JsonNode node && (bool)node;

    /// <summary>OpenAPI default for query parameters is style=form; explode defaults to true for form.</summary>
    public static string Style(JsonObject parameter) => (string?)parameter["style"] ?? "form";

    public static bool Explode(JsonObject parameter) => parameter["explode"] is not JsonNode node || (bool)node;

    public static string[] EnumValues(JsonObject schema) =>
        schema["enum"]?.AsArray().Select(v => (string)v!).ToArray() ?? [];

    public static IEnumerable<string> ComponentSchemaNames(JsonObject document) =>
        document["components"]?["schemas"]?.AsObject().Select(p => p.Key) ?? [];

    /// <summary>All "$ref" values anywhere in the document.</summary>
    public static IEnumerable<string> References(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    if (key == "$ref" && value is JsonValue refValue)
                        yield return (string)refValue!;
                    foreach (var nested in References(value))
                        yield return nested;
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    foreach (var nested in References(item))
                        yield return nested;
                }

                break;
        }
    }
}
