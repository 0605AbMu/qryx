using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.EntityFrameworkCore;

namespace Qryx.SourceGenerator.Tests;

/// <summary>Runs <see cref="QryxIncrementalGenerator"/> through <see cref="CSharpGeneratorDriver"/>.</summary>
public class GeneratorDriverTests
{
    private const string Entities = """
        namespace Shop
        {
            public enum State { Open, Closed }

            public class Customer
            {
                public int Id { get; set; }
                public string Name { get; set; } = "";
            }

            public class Order
            {
                public int Id { get; set; }
                public int Total { get; set; }
                public string Code { get; set; } = "";
                public State State { get; set; }
                public Customer Customer { get; set; } = null!;
            }
        }
        """;

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Latest);

    [Fact]
    public void Global_Namespace_Dto_Compiles()
    {
        const string source = """
            using Qryx.Core.Attributes;
            using Qryx.Core.Contracts;
            using Qryx.Core.Enums;
            using Qryx.Generated;
            using System.Linq;

            public record GlobalOrderQuery : QryxRequest<Shop.Order>
            {
                [Filter(Op.GreaterThanOrEqual, TargetProperty = "Total")]
                public int? MinTotal { get; init; }

                [Filter(Op.StartsWith, Invariant = true, TargetProperty = "Customer.Name")]
                public string? Customer { get; init; }
            }

            public static class Usage
            {
                public static IQueryable<Shop.Order> Run(IQueryable<Shop.Order> q) => q.ApplyGenerated(new GlobalOrderQuery { MinTotal = 1 });
            }
            """;

        var result = Run(source);

        Assert.Empty(result.GeneratorDiagnostics);
        Assert.Empty(result.CompilationErrors);
        var generated = Assert.Single(result.GeneratedSources);
        Assert.Equal("GlobalOrderQuery.g.cs", generated.HintName);
        Assert.Contains("global::GlobalOrderQuery request", generated.SourceText.ToString());
        Assert.DoesNotContain("<global namespace>", generated.SourceText.ToString());
    }

    [Fact]
    public void Nested_Generic_And_Internal_Dtos_Compile_With_Distinct_Hint_Names()
    {
        const string source = """
            using Qryx.Core.Attributes;
            using Qryx.Core.Contracts;
            using Qryx.Core.Enums;
            using Qryx.Generated;
            using System.Linq;

            namespace Shop.Api
            {
                public static class Outer
                {
                    public record NestedQuery : QryxRequest<Shop.Order>
                    {
                        [Filter(Op.In, TargetProperty = "State")]
                        public Shop.State[]? States { get; init; }
                    }
                }

                public record PagedQuery<T> : QryxRequest<T> where T : class;

                public record CodeQuery<TMarker> : QryxRequest<Shop.Order>
                {
                    [Filter(Op.Equals)]
                    public string? Code { get; init; }
                }

                internal record InternalQuery : QryxRequest<Shop.Order>
                {
                    [Filter(Op.Equals)]
                    public Shop.State? State { get; init; }
                }

                public static class Usage
                {
                    public static void Run(IQueryable<Shop.Order> q)
                    {
                        q.ApplyGenerated(new Outer.NestedQuery());
                        q.ApplyGenerated(new PagedQuery<Shop.Order>());
                        q.ApplyGenerated(new CodeQuery<int> { Code = "x" });
                        q.ApplyGenerated(new InternalQuery());
                    }
                }
            }
            """;

        var result = Run(source);

        Assert.Empty(result.GeneratorDiagnostics);
        Assert.Empty(result.CompilationErrors);
        Assert.Equal(
            ["Shop.Api.CodeQuery-1.g.cs", "Shop.Api.InternalQuery.g.cs", "Shop.Api.Outer.NestedQuery.g.cs", "Shop.Api.PagedQuery-1.g.cs"],
            result.GeneratedSources.Select(s => s.HintName).OrderBy(n => n, StringComparer.Ordinal));

        var internalSource = result.GeneratedSources.Single(s => s.HintName == "Shop.Api.InternalQuery.g.cs").SourceText.ToString();
        Assert.Contains("internal static global::System.Linq.IQueryable<global::Shop.Order> ApplyGenerated(", internalSource);

        var genericSource = result.GeneratedSources.Single(s => s.HintName == "Shop.Api.PagedQuery-1.g.cs").SourceText.ToString();
        Assert.Contains("ApplyGenerated<T>(this global::System.Linq.IQueryable<T> query, global::Shop.Api.PagedQuery<T> request)", genericSource);
        Assert.Contains("where T : class", genericSource);
    }

    [Fact]
    public void Positional_Record_Properties_And_Private_Nested_Dtos_Are_Handled()
    {
        const string source = """
            using Qryx.Core.Attributes;
            using Qryx.Core.Contracts;
            using Qryx.Core.Enums;
            using Qryx.Generated;
            using System.Linq;

            namespace Shop.Api;

            public record PositionalQuery([property: Filter(Op.GreaterThan, TargetProperty = "Total")] int? MinTotal) : QryxRequest<Shop.Order>;

            public class Host
            {
                // Not accessible from Qryx.Generated: no method is generated (and nothing breaks).
                private record HiddenQuery : QryxRequest<Shop.Order>
                {
                    [Filter(Op.Equals)]
                    public string? Code { get; init; }
                }
            }

            public static class Usage
            {
                public static IQueryable<Shop.Order> Run(IQueryable<Shop.Order> q) => q.ApplyGenerated(new PositionalQuery(5));
            }
            """;

        var result = Run(source);

        Assert.Empty(result.GeneratorDiagnostics);
        Assert.Empty(result.CompilationErrors);
        var generated = Assert.Single(result.GeneratedSources);
        Assert.Equal("Shop.Api.PositionalQuery.g.cs", generated.HintName);
        var positionalText = generated.SourceText.ToString();
        Assert.Contains("BuildComparison(global::System.Linq.Expressions.ExpressionType.GreaterThan", positionalText);
        Assert.Contains("(global::Shop.Order x) => x.Total);", positionalText);
    }

    [Fact]
    public void Operator_Member_Names_Come_From_The_Attribute()
    {
        const string source = """
            using Qryx.Core.Attributes;
            using Qryx.Core.Contracts;
            using Qryx.Core.Enums;

            namespace Shop.Api;

            public record OrderQuery : QryxRequest<Shop.Order>
            {
                [Filter("Total", Op.LessThanOrEqual)]
                public int? MaxTotal { get; init; }

                [Filter(Op.NotIn, "State")]
                public System.Collections.Generic.List<Shop.State>? NotStates { get; init; }
            }
            """;

        var result = Run(source);

        Assert.Empty(result.CompilationErrors);
        var text = Assert.Single(result.GeneratedSources).SourceText.ToString();
        Assert.Contains("// MaxTotal: Op.LessThanOrEqual on Total", text);
        Assert.Contains("BuildComparison(global::System.Linq.Expressions.ExpressionType.LessThanOrEqual", text);
        Assert.Contains("// NotStates: Op.NotIn on State", text);
        Assert.Contains("global::System.Linq.Expressions.Expression.Not(global::System.Linq.Expressions.Expression.Call(", text);
        Assert.Contains("(global::Shop.Order x) => x.State);", text);

        // All filters are combined into a single Where over pre-built plan nodes
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, @"Queryable\.Where\("));
    }

    [Fact]
    public void Missing_Target_Property_Reports_QRYX001()
    {
        const string source = """
            using Qryx.Core.Attributes;
            using Qryx.Core.Contracts;
            using Qryx.Core.Enums;

            namespace Shop.Api;

            public record OrderQuery : QryxRequest<Shop.Order>
            {
                [Filter(Op.Equals, TargetProperty = "Customer.Missing")]
                public string? CustomerMissing { get; init; }

                [Filter(Op.Equals)]
                public string? Nope { get; init; }
            }
            """;

        var result = Run(source);

        var diagnostics = result.GeneratorDiagnostics.Where(d => d.Id == "QRYX001").ToList();
        Assert.Equal(2, diagnostics.Count);
        Assert.All(diagnostics, d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
        Assert.Contains(diagnostics, d => d.GetMessage().Contains("'Missing'") && d.GetMessage().Contains("Customer"));
        Assert.All(diagnostics, d => Assert.True(d.Location.IsInSource));
    }

    [Fact]
    public void Incompatible_Operators_Report_QRYX002_And_Fall_Back_To_Runtime()
    {
        const string source = """
            using Qryx.Core.Attributes;
            using Qryx.Core.Contracts;
            using Qryx.Core.Enums;

            namespace Shop.Api;

            public record OrderQuery : QryxRequest<Shop.Order>
            {
                [Filter(Op.In, TargetProperty = "Total")]
                public int? TotalIn { get; init; }

                [Filter(Op.Between, TargetProperty = "Total")]
                public int? TotalBetween { get; init; }

                [Filter(Op.StartsWith, TargetProperty = "Total")]
                public string? TotalStarts { get; init; }

                [Filter(Op.GreaterThan, TargetProperty = "Code")]
                public string? CodeAbove { get; init; }

                [Filter(Op.IsNull, TargetProperty = "Total")]
                public bool? TotalMissing { get; init; }
            }
            """;

        var result = Run(source);

        var diagnostics = result.GeneratorDiagnostics.Where(d => d.Id == "QRYX002").ToList();
        Assert.Equal(5, diagnostics.Count);
        Assert.All(diagnostics, d => Assert.Equal(DiagnosticSeverity.Warning, d.Severity));
        Assert.Empty(result.CompilationErrors);

        var text = Assert.Single(result.GeneratedSources).SourceText.ToString();
        foreach (var property in new[] { "TotalIn", "TotalBetween", "TotalStarts", "CodeAbove", "TotalMissing" })
        {
            Assert.Contains($"BuildPropertyPredicate<global::Shop.Order>(request, \"{property}\", isPostgreSql)", text);
        }
    }

    [Fact]
    public void Value_Conversions_Fall_Back_To_Runtime_Without_Diagnostics()
    {
        const string source = """
            using Qryx.Core.Attributes;
            using Qryx.Core.Contracts;
            using Qryx.Core.Enums;

            namespace Shop.Api;

            public record OrderQuery : QryxRequest<Shop.Order>
            {
                [Filter(Op.Equals, TargetProperty = "State")]
                public string? StateText { get; init; }

                [Filter(Op.In, TargetProperty = "State")]
                public string[]? StateTexts { get; init; }

                [Filter(Op.Equals, TargetProperty = "Total")]
                public long? TotalLong { get; init; }
            }
            """;

        var result = Run(source);

        Assert.Empty(result.GeneratorDiagnostics);
        Assert.Empty(result.CompilationErrors);
        var text = Assert.Single(result.GeneratedSources).SourceText.ToString();
        Assert.Contains("(request, \"StateText\", isPostgreSql)", text);
        Assert.Contains("(request, \"StateTexts\", isPostgreSql)", text);
        Assert.Contains("(request, \"TotalLong\", isPostgreSql)", text);
    }

    [Fact]
    public void Invariant_String_Ops_Use_Runtime_Provider_Aware_Builder_With_Or_Without_Npgsql_Reference()
    {
        const string source = """
            using Qryx.Core.Attributes;
            using Qryx.Core.Contracts;
            using Qryx.Core.Enums;

            namespace Shop.Api;

            public record OrderQuery : QryxRequest<Shop.Order>
            {
                [Filter(Op.Contains, Invariant = true)]
                public string? Code { get; init; }
            }
            """;

        var withNpgsql = Run(source);
        var withoutNpgsql = Run(source, includeNpgsql: false);

        Assert.Empty(withNpgsql.CompilationErrors);
        Assert.Empty(withoutNpgsql.CompilationErrors);

        // ILIKE vs UPPER() is decided at runtime by ExpressionHelper (same as the dynamic engine), so the generated
        // code never references Npgsql directly and is identical with or without the package reference.
        var withText = Assert.Single(withNpgsql.GeneratedSources).SourceText.ToString();
        var withoutText = Assert.Single(withoutNpgsql.GeneratedSources).SourceText.ToString();
        Assert.Equal(withText, withoutText);
        Assert.DoesNotContain("NpgsqlDbFunctionsExtensions", withoutText);
        Assert.Contains("ExpressionHelper.BuildContains(", withoutText);
        Assert.Contains(", true, isPostgreSql)", withoutText);
    }

    [Fact]
    public void Unrelated_Edit_Keeps_Pipeline_Cached()
    {
        const string dto = """
            using Qryx.Core.Attributes;
            using Qryx.Core.Contracts;
            using Qryx.Core.Enums;

            namespace Shop.Api;

            public record OrderQuery : QryxRequest<Shop.Order>
            {
                [Filter(Op.GreaterThanOrEqual, TargetProperty = "Total")]
                public int? MinTotal { get; init; }
            }

            public record PlainQuery : QryxRequest<Shop.Order>;
            """;

        var compilation = CreateCompilation(includeNpgsql: true, Entities, dto, "namespace Shop.Other; public class Unrelated { public int A { get; set; } }");
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new QryxIncrementalGenerator().AsSourceGenerator()],
            parseOptions: ParseOptions,
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

        driver = driver.RunGenerators(compilation);
        Assert.Equal(2, driver.GetRunResult().GeneratedTrees.Length);

        // Edit only the unrelated file.
        var unrelatedTree = compilation.SyntaxTrees.Last();
        var edited = compilation.ReplaceSyntaxTree(
            unrelatedTree,
            CSharpSyntaxTree.ParseText("namespace Shop.Other; public class Unrelated { public int A { get; set; } public string B => \"b\"; }", ParseOptions));

        driver = driver.RunGenerators(edited);
        var run = driver.GetRunResult().Results.Single();

        foreach (var stepName in new[]
                 {
                     QryxIncrementalGenerator.TrackingNames.FilterTypes,
                     QryxIncrementalGenerator.TrackingNames.RequestTypes,
                     QryxIncrementalGenerator.TrackingNames.Models,
                     QryxIncrementalGenerator.TrackingNames.Features
                 })
        {
            Assert.True(run.TrackedSteps.TryGetValue(stepName, out var steps), $"step {stepName} was not tracked");
            var reasons = steps.SelectMany(s => s.Outputs).Select(o => o.Reason).ToList();
            Assert.NotEmpty(reasons);
            Assert.All(reasons, reason => Assert.True(
                reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged,
                $"{stepName}: {reason}"));
        }

        var outputReasons = run.TrackedOutputSteps.SelectMany(s => s.Value).SelectMany(s => s.Outputs).Select(o => o.Reason).ToList();
        Assert.NotEmpty(outputReasons);
        Assert.All(outputReasons, reason => Assert.Equal(IncrementalStepRunReason.Cached, reason));
    }

    [Fact]
    public void Editing_A_Dto_Regenerates_Only_That_Dto()
    {
        const string first = """
            using Qryx.Core.Attributes;
            using Qryx.Core.Contracts;
            using Qryx.Core.Enums;
            namespace Shop.Api;
            public record FirstQuery : QryxRequest<Shop.Order>
            {
                [Filter(Op.GreaterThanOrEqual, TargetProperty = "Total")]
                public int? MinTotal { get; init; }
            }
            """;
        const string second = """
            using Qryx.Core.Attributes;
            using Qryx.Core.Contracts;
            using Qryx.Core.Enums;
            namespace Shop.Api;
            public record SecondQuery : QryxRequest<Shop.Order>
            {
                [Filter(Op.Equals)]
                public string? Code { get; init; }
            }
            """;

        var compilation = CreateCompilation(includeNpgsql: true, Entities, first, second);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new QryxIncrementalGenerator().AsSourceGenerator()],
            parseOptions: ParseOptions,
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
        driver = driver.RunGenerators(compilation);

        var secondTree = compilation.SyntaxTrees.Last();
        var edited = compilation.ReplaceSyntaxTree(
            secondTree,
            CSharpSyntaxTree.ParseText(second.Replace("Op.Equals", "Op.StartsWith"), ParseOptions));

        driver = driver.RunGenerators(edited);
        var run = driver.GetRunResult().Results.Single();

        var modelReasons = run.TrackedSteps[QryxIncrementalGenerator.TrackingNames.Models]
            .SelectMany(s => s.Outputs)
            .Select(o => o.Reason)
            .ToList();

        Assert.Equal(1, modelReasons.Count(r => r == IncrementalStepRunReason.Modified));
        Assert.Equal(1, modelReasons.Count(r => r is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged));
        Assert.Contains("ExpressionHelper.BuildStartsWith(", run.GeneratedSources.Single(s => s.HintName == "Shop.Api.SecondQuery.g.cs").SourceText.ToString());
    }

    private sealed record RunResult(
        ImmutableArray<Diagnostic> GeneratorDiagnostics,
        IReadOnlyList<Diagnostic> CompilationErrors,
        ImmutableArray<GeneratedSourceResult> GeneratedSources);

    private static RunResult Run(string source, bool includeNpgsql = true)
    {
        var compilation = CreateCompilation(includeNpgsql, Entities, source);
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new QryxIncrementalGenerator().AsSourceGenerator()], parseOptions: ParseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);

        var errors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        return new RunResult(diagnostics, errors, driver.GetRunResult().Results.Single().GeneratedSources);
    }

    private static CSharpCompilation CreateCompilation(bool includeNpgsql, params string[] sources)
    {
        // Framework assemblies only, then exactly the Qryx/EF assemblies the scenario needs.
        var frameworkDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(path => string.Equals(Path.GetDirectoryName(path), frameworkDirectory, StringComparison.OrdinalIgnoreCase))
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToList<MetadataReference>();

        var qryxAssemblies = new[]
        {
            typeof(Qryx.Core.Attributes.FilterAttribute).Assembly,
            typeof(Qryx.Expressions.Builders.FilterBuilder).Assembly,
            typeof(DbContext).Assembly
        }.ToList();

        if (includeNpgsql)
            qryxAssemblies.Add(typeof(NpgsqlDbFunctionsExtensions).Assembly);

        references.AddRange(qryxAssemblies.Select(a => MetadataReference.CreateFromFile(a.Location)));

        return CSharpCompilation.Create(
            "GeneratorTests",
            sources.Select(s => CSharpSyntaxTree.ParseText(s, ParseOptions)),
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    }
}
