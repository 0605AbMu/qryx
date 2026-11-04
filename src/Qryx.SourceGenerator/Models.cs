using Microsoft.CodeAnalysis;

namespace Qryx.SourceGenerator;

// All pipeline models are plain equatable values (strings, enums, EquatableArray): no ISymbol, SyntaxNode or
// Compilation is carried through the pipeline, so unchanged DTOs are served from the incremental cache.

/// <summary>Filter operator, mirrored by member name from <c>Qryx.Core.Enums.Op</c>.</summary>
internal enum FilterOp
{
    Unknown,
    Equals,
    NotEquals,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    StartsWith,
    EndsWith,
    Contains,
    In,
    NotIn,
    Between,
    IsNull,
    IsNotNull,
    Fts,
    JsonContains
}

/// <summary>How the DTO property value is read and when the filter counts as "empty" (skipped).</summary>
internal enum ValueKind
{
    /// <summary><c>T?</c> value type: skipped when null; the local is the underlying value.</summary>
    NullableValue,

    /// <summary>Non-nullable value type: always applied (mirrors the boxed getter of the dynamic engine).</summary>
    Value,

    /// <summary>string: skipped when null or empty.</summary>
    String,

    /// <summary><c>Range&lt;T&gt;</c> or <c>Range&lt;T&gt;?</c>: skipped when null or without bounds.</summary>
    Range,

    /// <summary>Non-string IEnumerable: skipped when null or empty.</summary>
    Collection,

    /// <summary>Any other reference type: skipped when null.</summary>
    Reference
}

/// <summary>How an In/NotIn collection value is turned into the captured list.</summary>
internal enum CollectionKind
{
    None,

    /// <summary><c>T[]</c>: captured directly.</summary>
    Array,

    /// <summary><c>List&lt;T&gt;</c>: captured directly.</summary>
    List,

    /// <summary>Any other <c>IEnumerable&lt;T&gt;</c>: copied into a <c>List&lt;T&gt;</c> (with the 1000-item cap).</summary>
    Enumerable
}

/// <summary>Whether the filter is emitted as a typed lambda or delegated to the runtime engine.</summary>
internal enum FilterStrategy
{
    Native,
    Runtime
}

/// <summary>One entity column of a filter, e.g. <c>Department.Name</c>.</summary>
/// <param name="Access">Escaped member access after the lambda parameter, e.g. <c>Department.Name</c>.</param>
/// <param name="ValueCast">Fully-qualified type to cast the filter value to (numeric widening), or null.</param>
internal sealed record TargetModel(string Access, string? ValueCast);

/// <summary>Emission plan of one [Filter] DTO property.</summary>
internal sealed record FilterModel(
    string PropertyName,
    string PropertyAccess,
    string LocalName,
    FilterOp Operator,
    FilterStrategy Strategy,
    ValueKind ValueKind,
    bool IsNullableRange,
    bool IsBooleanValue,
    CollectionKind CollectionKind,
    string? ElementTypeName,
    bool Invariant,
    int Weight,
    int Order,
    EquatableArray<TargetModel> Targets);

/// <summary>Kind of extension method emitted for a DTO.</summary>
internal enum DtoEmitKind
{
    /// <summary>Do not emit any method (e.g. the DTO is not accessible from the generated class).</summary>
    None,

    /// <summary>Typed lambdas per [Filter] property (entity type known, DTO not generic).</summary>
    Native,

    /// <summary>Generic method delegating all filters to the runtime engine (unknown entity or generic DTO).</summary>
    Runtime
}

/// <summary>Everything needed to emit the generated file of one DTO.</summary>
internal sealed record DtoModel(
    string Key,
    string HintName,
    string DtoTypeName,
    string DtoDisplayName,
    string? EntityTypeName,
    bool IsValueType,
    bool ImplementsRequest,
    DtoEmitKind EmitKind,
    string Accessibility,
    EquatableArray<string> TypeParameters,
    EquatableArray<string> ConstraintClauses,
    EquatableArray<FilterModel> Filters);

/// <summary>
/// Result of analysing one DTO. The code model never contains locations (moving an attribute does not regenerate
/// code). Diagnostics are real <see cref="Diagnostic"/> values (value equality, source locations, so
/// <c>#pragma warning disable</c> works) and are reported from a separate pipeline branch.
/// </summary>
internal sealed record DtoResult(DtoModel Model, EquatableArray<Diagnostic> Diagnostics);

/// <summary>Compilation-wide facts that affect emitted code (kept tiny so it is almost always cached).</summary>
/// <param name="HasNpgsqlILike">Npgsql's <c>ILike(DbFunctions, string, string, string)</c> is referenced.</param>
/// <param name="HasEfCoreTagWith">EF Core's <c>TagWith</c> is referenced.</param>
internal sealed record CompilationFeatures(bool HasNpgsqlILike, bool HasEfCoreTagWith);
