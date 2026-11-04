using System.Runtime.CompilerServices;
using Microsoft.OpenApi;

namespace Qryx.ApiDocs.Internal;

/// <summary>
/// Tracks component schemas that only the replaced Qryx parameter schemas referenced (e.g. Swashbuckle's
/// <c>Int32Range</c> object schema for <c>Range&lt;int&gt;</c>) and removes them from the finished document when
/// nothing else references them. Candidates are keyed by a per-document object (Swashbuckle's schema repository or
/// the OpenAPI document itself); schemas registered by the application for other reasons are never touched.
/// </summary>
internal static class QryxUnusedSchemas
{
    private static readonly ConditionalWeakTable<object, HashSet<string>> Candidates = new();

    /// <summary>Candidate set for one document generation, keyed by <paramref name="documentKey"/>.</summary>
    public static ISet<string> For(object documentKey) =>
        Candidates.GetValue(documentKey, static _ => new HashSet<string>(StringComparer.Ordinal));

    /// <summary>Removes the unreferenced candidates of <paramref name="documentKey"/> from <paramref name="document"/>.</summary>
    public static void RemoveUnreferenced(OpenApiDocument document, object documentKey)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (!Candidates.TryGetValue(documentKey, out var candidates))
            return;

        Candidates.Remove(documentKey);
        var schemas = document.Components?.Schemas;
        if (schemas == null || schemas.Count == 0)
            return;

        // Only plain ids: they appear verbatim (unescaped) in the serialized "$ref" values.
        var remaining = candidates.Where(id => schemas.ContainsKey(id) && id.All(IsPlainIdChar)).ToList();

        // Removing a schema can orphan another candidate it referenced, so repeat until nothing changes.
        while (remaining.Count > 0)
        {
            var json = Serialize(document);
            var removed = remaining.RemoveAll(id =>
                !json.Contains($"\"#/components/schemas/{id}\"", StringComparison.Ordinal) && schemas.Remove(id));
            if (removed == 0)
                break;
        }
    }

    private static bool IsPlainIdChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-';

    private static string Serialize(OpenApiDocument document)
    {
        using var writer = new StringWriter();
        document.SerializeAsV3(new OpenApiJsonWriter(writer));
        return writer.ToString();
    }
}
