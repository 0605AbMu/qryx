using Microsoft.CodeAnalysis;

namespace Qryx.SourceGenerator;

/// <summary>Diagnostics reported by the Qryx source generator.</summary>
internal static class DiagnosticDescriptors
{
    private const string Category = "Qryx";

    /// <summary>QRYX001: a [Filter] target property path does not exist on the entity.</summary>
    public static readonly DiagnosticDescriptor TargetPropertyNotFound = new(
        id: "QRYX001",
        title: "Filter target property not found",
        messageFormat: "[Filter] on '{0}.{1}' targets '{2}', but '{3}' is not a public instance property of '{4}'",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Every segment of a [Filter] target property path must be a readable public instance property of the entity (case-insensitive).");

    /// <summary>QRYX002: the operator cannot be applied to the DTO or entity property type.</summary>
    public static readonly DiagnosticDescriptor IncompatibleOperator = new(
        id: "QRYX002",
        title: "Filter operator incompatible with property type",
        messageFormat: "Op.{0} on '{1}.{2}' is not compatible with {3}; the filter is delegated to the runtime engine and may fail when used",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "In/NotIn require a collection value, Between requires Range<T>, string operators require string target properties, comparison operators require comparable types and IsNull/IsNotNull require nullable target properties.");

    public static DiagnosticDescriptor? Find(string id) => id switch
    {
        "QRYX001" => TargetPropertyNotFound,
        "QRYX002" => IncompatibleOperator,
        _ => null
    };
}
