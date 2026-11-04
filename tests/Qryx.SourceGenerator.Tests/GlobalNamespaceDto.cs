using Qryx.Core.Attributes;
using Qryx.Core.Contracts;
using Qryx.Core.Enums;
using Qryx.SourceGenerator.Tests;

// Deliberately declared in the global namespace: the generator must emit a valid file and type reference for it.

/// <summary>DTO in the global namespace.</summary>
public record GlobalNamespacePersonQuery : QryxRequest<Person>
{
    [Filter(Op.GreaterThanOrEqual)]
    public int? MinAge { get; init; }
}
