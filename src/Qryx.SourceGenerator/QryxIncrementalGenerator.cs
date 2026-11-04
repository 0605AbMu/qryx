using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Qryx.SourceGenerator;

/// <summary>
/// Incremental Roslyn source generator for Qryx query DTOs.
/// For every DTO with [Filter] properties or implementing <c>IQryxRequest&lt;TEntity&gt;</c> it emits
/// <c>Qryx.Generated.QryxGeneratedExtensions.ApplyGenerated(this IQueryable&lt;TEntity&gt;, TDto)</c>
/// with plain typed lambdas over captured locals (SQL parameters, zero reflection), followed by sorting and pagination.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class QryxIncrementalGenerator : IIncrementalGenerator
{
    /// <summary>Tracking names of the pipeline steps (used by incremental caching tests).</summary>
    public static class TrackingNames
    {
        public const string FilterTypes = "QryxFilterTypes";
        public const string RequestTypes = "QryxRequestTypes";
        public const string Models = "QryxModels";
        public const string Features = "QryxFeatures";
        public const string Diagnostics = "QryxDiagnostics";
    }

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // 1. Types that declare [Filter] properties (including record primary-constructor [property: Filter]).
        var filterTypes = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                ModelBuilder.FilterAttributeMetadataName,
                predicate: static (node, _) => node is PropertyDeclarationSyntax or ParameterSyntax,
                transform: static (ctx, ct) =>
                {
                    var containingType = ctx.TargetSymbol switch
                    {
                        IPropertySymbol property => property.ContainingType,
                        IParameterSymbol parameter => parameter.ContainingSymbol?.ContainingType,
                        _ => null
                    };

                    return containingType is null ? null : ModelBuilder.Build(containingType, ctx.SemanticModel.Compilation, ct);
                })
            .Where(static result => result is not null)
            .Select(static (result, _) => result!)
            .WithTrackingName(TrackingNames.FilterTypes);

        // 2. Request types (QryxRequest<T> / IQryxRequest<T>) without own [Filter] properties: sorting + pagination.
        var requestTypes = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => node is TypeDeclarationSyntax { BaseList: not null } and not InterfaceDeclarationSyntax,
                transform: static (ctx, ct) =>
                {
                    if (ctx.SemanticModel.GetDeclaredSymbol(ctx.Node, ct) is not INamedTypeSymbol type ||
                        !ModelBuilder.ImplementsRequest(type))
                    {
                        return null;
                    }

                    return ModelBuilder.Build(type, ctx.SemanticModel.Compilation, ct);
                })
            .Where(static result => result is not null)
            .Select(static (result, _) => result!)
            .WithTrackingName(TrackingNames.RequestTypes);

        // 3. One model per DTO (partial declarations and both sources are de-duplicated by metadata name).
        var results = filterTypes.Collect()
            .Combine(requestTypes.Collect())
            .SelectMany(static (pair, _) => Distinct(pair.Left, pair.Right));

        // Code generation depends only on the location-free model; diagnostics are a separate branch.
        var models = results
            .Select(static (result, _) => result.Model)
            .WithTrackingName(TrackingNames.Models);

        var diagnostics = results
            .Select(static (result, _) => result.Diagnostics)
            .Where(static d => !d.IsEmpty)
            .WithTrackingName(TrackingNames.Diagnostics);

        var features = context.CompilationProvider
            .Select(static (compilation, _) => DetectFeatures(compilation))
            .WithTrackingName(TrackingNames.Features);

        context.RegisterSourceOutput(models.Combine(features), static (spc, source) => Execute(spc, source.Left, source.Right));

        context.RegisterSourceOutput(diagnostics, static (spc, items) =>
        {
            foreach (var diagnostic in items)
                spc.ReportDiagnostic(diagnostic);
        });
    }

    private static ImmutableArray<DtoResult> Distinct(ImmutableArray<DtoResult> first, ImmutableArray<DtoResult> second)
    {
        var seen = new HashSet<string>();
        var builder = ImmutableArray.CreateBuilder<DtoResult>(first.Length + second.Length);

        foreach (var result in first.Concat(second).OrderBy(static r => r.Model.Key, System.StringComparer.Ordinal))
        {
            if (seen.Add(result.Model.Key))
                builder.Add(result);
        }

        return builder.ToImmutable();
    }

    private static CompilationFeatures DetectFeatures(Compilation compilation)
    {
        var npgsql = compilation.GetTypeByMetadataName("Microsoft.EntityFrameworkCore.NpgsqlDbFunctionsExtensions");
        var hasILike = npgsql is not null && npgsql.GetMembers("ILike")
            .OfType<IMethodSymbol>()
            .Any(static m => m.Parameters.Length == 4);

        var efQueryable = compilation.GetTypeByMetadataName("Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions");
        var hasTagWith = efQueryable is not null && !efQueryable.GetMembers("TagWith").IsEmpty;

        return new CompilationFeatures(hasILike, hasTagWith);
    }

    private static void Execute(SourceProductionContext context, DtoModel model, CompilationFeatures features)
    {
        if (model.EmitKind == DtoEmitKind.None)
            return;

        context.AddSource(model.HintName, SourceText.From(SourceEmitter.Emit(model, features), Encoding.UTF8));
    }
}
