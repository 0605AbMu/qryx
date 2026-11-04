using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Qryx.SourceGenerator;

/// <summary>
/// Turns a DTO type symbol into an equatable <see cref="DtoModel"/>. All semantic analysis (target path resolution,
/// type compatibility, diagnostics) happens here, so the emitter only formats strings.
/// </summary>
internal static class ModelBuilder
{
    public const string FilterAttributeMetadataName = "Qryx.Core.Attributes.FilterAttribute";

    private static readonly string[] RangePrefixes = ["Min", "Max", "From", "To"];

    private static readonly SymbolDisplayFormat FullyQualified = SymbolDisplayFormat.FullyQualifiedFormat
        .WithMiscellaneousOptions(
            SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
            SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private static readonly SymbolDisplayFormat Minimal = SymbolDisplayFormat.MinimallyQualifiedFormat;

    /// <summary>Returns true when the type implements <c>Qryx.Core.Contracts.IQryxRequest&lt;T&gt;</c>.</summary>
    public static bool ImplementsRequest(INamedTypeSymbol type) => FindRequestInterface(type) is not null;

    /// <summary>Builds the model of a DTO, or null when the type cannot host a generated method.</summary>
    public static DtoResult? Build(INamedTypeSymbol type, Compilation compilation, CancellationToken ct)
    {
        if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct) || type.IsStatic)
            return null;

        var filterAttribute = compilation.GetTypeByMetadataName(FilterAttributeMetadataName);
        var requestInterface = FindRequestInterface(type);
        var entity = requestInterface?.TypeArguments[0];

        var typeParameters = CollectTypeParameters(type);
        var isGeneric = typeParameters.Count > 0;
        var entityIsOpen = entity is not null && ContainsTypeParameter(entity);

        var accessibility = MinAccessibility(EffectiveAccessibility(type), entity is null ? Access.Public : EffectiveAccessibility(entity));

        var emitKind = accessibility == Access.None
            ? DtoEmitKind.None
            : entity is null || isGeneric || entityIsOpen ? DtoEmitKind.Runtime : DtoEmitKind.Native;

        var diagnostics = new List<Diagnostic>();
        var filters = new List<FilterModel>();

        // Typed per-property plans (and their diagnostics) need a closed entity type.
        if (entity is not null && !entityIsOpen && filterAttribute is not null)
        {
            CollectFilters(type, entity, filterAttribute, compilation, filters, diagnostics, ct);
        }

        filters.Sort(static (a, b) =>
        {
            var byWeight = a.Weight.CompareTo(b.Weight);
            return byWeight != 0 ? byWeight : a.Order.CompareTo(b.Order);
        });

        var hintName = BuildHintName(type);

        var model = new DtoModel(
            Key: hintName,
            HintName: hintName + ".g.cs",
            DtoTypeName: type.ToDisplayString(FullyQualified),
            DtoDisplayName: type.ToDisplayString(Minimal),
            EntityTypeName: entity?.ToDisplayString(FullyQualified),
            IsValueType: type.IsValueType,
            ImplementsRequest: requestInterface is not null,
            EmitKind: emitKind,
            Accessibility: accessibility == Access.Public ? "public" : "internal",
            TypeParameters: typeParameters.Select(static t => t.Name).ToEquatableArray(),
            ConstraintClauses: BuildConstraintClauses(typeParameters).ToEquatableArray(),
            Filters: filters.ToEquatableArray());

        return new DtoResult(model, diagnostics.ToEquatableArray());
    }

    private static INamedTypeSymbol? FindRequestInterface(INamedTypeSymbol type)
    {
        foreach (var iface in type.AllInterfaces)
        {
            if (iface.Arity == 1 &&
                iface.Name == "IQryxRequest" &&
                iface.ContainingNamespace?.ToDisplayString() == "Qryx.Core.Contracts")
            {
                return iface;
            }
        }

        return null;
    }

    private static void CollectFilters(
        INamedTypeSymbol type,
        ITypeSymbol entity,
        INamedTypeSymbol filterAttribute,
        Compilation compilation,
        List<FilterModel> filters,
        List<Diagnostic> diagnostics,
        CancellationToken ct)
    {
        // Mirrors Type.GetProperties(Public | Instance): most derived type first, then base types.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int order = 0;

        for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            foreach (var member in current.GetMembers())
            {
                ct.ThrowIfCancellationRequested();

                if (member is not IPropertySymbol { IsStatic: false, IsIndexer: false, DeclaredAccessibility: Accessibility.Public } property)
                    continue;

                if (property.GetMethod is null || !seen.Add(property.Name))
                    continue;

                var (attribute, owner) = FindFilterAttribute(property, filterAttribute);
                if (attribute is null || owner is null || attribute.AttributeConstructor is null)
                    continue;

                // Diagnostics of inherited properties are reported once, on the declaring DTO.
                var report = SymbolEqualityComparer.Default.Equals(owner.ContainingType, type);
                var location = report
                    ? attribute.ApplicationSyntaxReference?.GetSyntax(ct).GetLocation() ?? owner.Locations.FirstOrDefault()
                    : null;

                var filter = BuildFilter(type, entity, property, attribute, compilation, order++, report ? diagnostics : null, location);
                filters.Add(filter);
            }
        }
    }

    private static (AttributeData? Attribute, IPropertySymbol? Owner) FindFilterAttribute(IPropertySymbol property, INamedTypeSymbol filterAttribute)
    {
        // [Filter] is Inherited = true: an override without the attribute inherits it from the overridden property.
        for (var current = property; current is not null; current = current.OverriddenProperty)
        {
            foreach (var attribute in current.GetAttributes())
            {
                if (SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, filterAttribute))
                    return (attribute, current);
            }
        }

        return (null, null);
    }

    private static FilterModel BuildFilter(
        INamedTypeSymbol dto,
        ITypeSymbol entity,
        IPropertySymbol property,
        AttributeData attribute,
        Compilation compilation,
        int order,
        List<Diagnostic>? diagnostics,
        Location? location)
    {
        var op = FilterOp.Equals;
        string? targetProperty = null;
        string?[]? targetProperties = null;
        bool invariant = false;

        foreach (var argument in attribute.ConstructorArguments)
        {
            if (argument.Kind == TypedConstantKind.Enum)
                op = ResolveOperator(argument);
            else if (argument.Kind == TypedConstantKind.Primitive && argument.Value is string text)
                targetProperty = text;
        }

        foreach (var named in attribute.NamedArguments)
        {
            switch (named.Key)
            {
                case "TargetProperty":
                    targetProperty = named.Value.Value as string;
                    break;
                case "TargetProperties" when named.Value.Kind == TypedConstantKind.Array && !named.Value.IsNull:
                    targetProperties = named.Value.Values.Select(static v => v.Value as string).ToArray();
                    break;
                case "Invariant" when named.Value.Value is bool flag:
                    invariant = flag;
                    break;
            }
        }

        // Same precedence as MetadataCache.InspectDto: TargetProperties > TargetProperty > prefix-stripped DTO name.
        IReadOnlyList<string> paths = targetProperties is { Length: > 0 }
            ? targetProperties.Select(static p => p ?? string.Empty).ToArray()
            : !string.IsNullOrWhiteSpace(targetProperty)
                ? [targetProperty!]
                : [ResolvePrefixProperty(property.Name)];

        var propertyType = property.Type;
        var valueKind = ClassifyValue(propertyType, out var valueType, out var isNullableRange);
        var weight = GetFilterWeight(op, paths.Count, propertyType);
        var localName = ToCamelCase(property.Name);

        FilterModel Create(FilterStrategy strategy, EquatableArray<TargetModel> targets, CollectionKind collectionKind = CollectionKind.None, string? elementType = null) =>
            new(
                PropertyName: property.Name,
                PropertyAccess: EscapeIdentifier(property.Name),
                LocalName: localName,
                Operator: op,
                Strategy: strategy,
                ValueKind: valueKind,
                IsNullableRange: isNullableRange,
                IsBooleanValue: valueType?.SpecialType == SpecialType.System_Boolean,
                CollectionKind: collectionKind,
                ElementTypeName: elementType,
                Invariant: invariant,
                Weight: weight,
                Order: order,
                Targets: targets);

        FilterModel Runtime() => Create(FilterStrategy.Runtime, EquatableArray<TargetModel>.Empty);

        FilterModel Incompatible(string reason)
        {
            diagnostics?.Add(Diagnostic.Create(
                DiagnosticDescriptors.IncompatibleOperator,
                location,
                op.ToString(), dto.Name, property.Name, reason));
            return Runtime();
        }

        // 1. Resolve every target path on the entity (QRYX001 when a segment is missing).
        var resolved = new List<(string Access, ITypeSymbol Type, bool Accessible)>(paths.Count);
        foreach (var path in paths)
        {
            if (!TryResolvePath(entity, path, compilation, out var access, out var memberType, out var accessible, out var missingSegment, out var missingOwner))
            {
                diagnostics?.Add(Diagnostic.Create(
                    DiagnosticDescriptors.TargetPropertyNotFound,
                    location,
                    dto.Name, property.Name, path, missingSegment, missingOwner));
                return Runtime();
            }

            resolved.Add((access, memberType, accessible));
        }

        // 2. Operators that are always delegated to the runtime engine.
        if (op is FilterOp.Fts or FilterOp.JsonContains or FilterOp.Unknown)
            return Runtime();

        // The generated code reads the DTO property and every entity member directly.
        var directAccess = property.GetMethod is not null &&
                           compilation.IsSymbolAccessibleWithin(property.GetMethod, compilation.Assembly) &&
                           resolved.All(static r => r.Accessible);

        switch (op)
        {
            case FilterOp.Equals:
            case FilterOp.NotEquals:
            case FilterOp.GreaterThan:
            case FilterOp.GreaterThanOrEqual:
            case FilterOp.LessThan:
            case FilterOp.LessThanOrEqual:
            {
                if (valueKind is ValueKind.Range or ValueKind.Collection)
                    return Incompatible($"DTO property type '{Display(propertyType)}' (expected a scalar value)");

                var targets = new List<TargetModel>(resolved.Count);
                foreach (var (access, memberType, _) in resolved)
                {
                    if (!SupportsOperator(op, memberType))
                        return Incompatible($"entity property type '{Display(memberType)}' of '{access}'");

                    if (!TryGetValueConversion(compilation, valueType!, memberType, out var cast))
                        return Runtime();

                    targets.Add(new TargetModel(access, cast));
                }

                return directAccess ? Create(FilterStrategy.Native, targets.ToEquatableArray()) : Runtime();
            }

            case FilterOp.StartsWith:
            case FilterOp.EndsWith:
            case FilterOp.Contains:
            {
                if (valueKind is ValueKind.Range or ValueKind.Collection)
                    return Incompatible($"DTO property type '{Display(propertyType)}' (expected string)");

                foreach (var (access, memberType, _) in resolved)
                {
                    if (memberType.SpecialType != SpecialType.System_String)
                        return Incompatible($"entity property type '{Display(memberType)}' of '{access}' (expected string)");
                }

                // Non-string DTO values are converted with ToString() by the runtime engine.
                if (valueKind != ValueKind.String || !directAccess)
                    return Runtime();

                return Create(FilterStrategy.Native, resolved.Select(static r => new TargetModel(r.Access, null)).ToEquatableArray());
            }

            case FilterOp.In:
            case FilterOp.NotIn:
            {
                if (valueKind != ValueKind.Collection)
                    return Incompatible($"DTO property type '{Display(propertyType)}' (expected a collection)");

                var collectionKind = ClassifyCollection(propertyType, out var elementType);
                if (collectionKind == CollectionKind.None || elementType is null || !directAccess)
                    return Runtime();

                foreach (var (_, memberType, _) in resolved)
                {
                    // Element conversions (e.g. string -> enum, int -> int?) are handled by the runtime engine.
                    if (!SymbolEqualityComparer.Default.Equals(elementType, memberType))
                        return Runtime();
                }

                return Create(
                    FilterStrategy.Native,
                    resolved.Select(static r => new TargetModel(r.Access, null)).ToEquatableArray(),
                    collectionKind,
                    elementType.ToDisplayString(FullyQualified));
            }

            case FilterOp.Between:
            {
                if (valueKind != ValueKind.Range)
                    return Incompatible($"DTO property type '{Display(propertyType)}' (expected Qryx.Core.Types.Range<T>)");

                var targets = new List<TargetModel>(resolved.Count);
                foreach (var (access, memberType, _) in resolved)
                {
                    if (!SupportsOperator(FilterOp.GreaterThanOrEqual, memberType))
                        return Incompatible($"entity property type '{Display(memberType)}' of '{access}'");

                    if (!TryGetValueConversion(compilation, valueType!, memberType, out var cast))
                        return Runtime();

                    targets.Add(new TargetModel(access, cast));
                }

                return directAccess ? Create(FilterStrategy.Native, targets.ToEquatableArray()) : Runtime();
            }

            case FilterOp.IsNull:
            case FilterOp.IsNotNull:
            {
                foreach (var (access, memberType, _) in resolved)
                {
                    if (memberType.IsValueType && memberType.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T)
                        return Incompatible($"entity property type '{Display(memberType)}' of '{access}' (not nullable)");
                }

                if (valueKind is ValueKind.Range or ValueKind.Collection || !directAccess)
                    return Runtime();

                return Create(FilterStrategy.Native, resolved.Select(static r => new TargetModel(r.Access, null)).ToEquatableArray());
            }

            default:
                return Runtime();
        }
    }

    private static FilterOp ResolveOperator(TypedConstant argument)
    {
        // Read the real Op member name (no hard-coded ordinal values).
        if (argument.Type is INamedTypeSymbol enumType)
        {
            foreach (var member in enumType.GetMembers())
            {
                if (member is IFieldSymbol { HasConstantValue: true } field && Equals(field.ConstantValue, argument.Value))
                {
                    return field.Name switch
                    {
                        "Equals" => FilterOp.Equals,
                        "NotEquals" => FilterOp.NotEquals,
                        "GreaterThan" => FilterOp.GreaterThan,
                        "GreaterThanOrEqual" => FilterOp.GreaterThanOrEqual,
                        "LessThan" => FilterOp.LessThan,
                        "LessThanOrEqual" => FilterOp.LessThanOrEqual,
                        "StartsWith" => FilterOp.StartsWith,
                        "EndsWith" => FilterOp.EndsWith,
                        "Contains" => FilterOp.Contains,
                        "In" => FilterOp.In,
                        "NotIn" => FilterOp.NotIn,
                        "Between" => FilterOp.Between,
                        "IsNull" => FilterOp.IsNull,
                        "IsNotNull" => FilterOp.IsNotNull,
                        "Fts" => FilterOp.Fts,
                        "JsonContains" => FilterOp.JsonContains,
                        _ => FilterOp.Unknown
                    };
                }
            }
        }

        return FilterOp.Unknown;
    }

    /// <summary>Identical to <c>MetadataCache.ResolvePrefixProperty</c> (MinAge -> Age).</summary>
    internal static string ResolvePrefixProperty(string propertyName)
    {
        foreach (var prefix in RangePrefixes)
        {
            if (propertyName.StartsWith(prefix, StringComparison.Ordinal) &&
                propertyName.Length > prefix.Length &&
                char.IsUpper(propertyName[prefix.Length]))
            {
                return propertyName.Substring(prefix.Length);
            }
        }

        return propertyName;
    }

    /// <summary>Identical to <c>MetadataCache.GetFilterWeight</c>: cheap scalar filters first, text/compound last.</summary>
    private static int GetFilterWeight(FilterOp op, int targetCount, ITypeSymbol dtoPropertyType)
    {
        if (targetCount > 1)
            return 100;

        return op switch
        {
            FilterOp.Equals => dtoPropertyType.SpecialType == SpecialType.System_String ? 2 : 1,
            FilterOp.NotEquals => 3,
            FilterOp.GreaterThan or FilterOp.GreaterThanOrEqual or FilterOp.LessThan or FilterOp.LessThanOrEqual => 4,
            FilterOp.Between => 5,
            FilterOp.In => 10,
            FilterOp.NotIn => 11,
            FilterOp.StartsWith => 20,
            FilterOp.EndsWith => 21,
            FilterOp.Contains => 22,
            _ => 50
        };
    }

    private static ValueKind ClassifyValue(ITypeSymbol type, out ITypeSymbol? valueType, out bool isNullableRange)
    {
        isNullableRange = false;

        if (TryGetNullableUnderlying(type, out var underlying))
        {
            if (TryGetRangeArgument(underlying, out var nullableRangeArgument))
            {
                isNullableRange = true;
                valueType = nullableRangeArgument;
                return ValueKind.Range;
            }

            valueType = underlying;
            return ValueKind.NullableValue;
        }

        if (TryGetRangeArgument(type, out var rangeArgument))
        {
            valueType = rangeArgument;
            return ValueKind.Range;
        }

        valueType = type;

        if (type.SpecialType == SpecialType.System_String)
            return ValueKind.String;

        if (type is IArrayTypeSymbol || IsEnumerable(type))
            return ValueKind.Collection;

        return type.IsValueType ? ValueKind.Value : ValueKind.Reference;
    }

    private static bool IsEnumerable(ITypeSymbol type)
    {
        if (type.SpecialType == SpecialType.System_Collections_IEnumerable)
            return true;

        foreach (var iface in type.AllInterfaces)
        {
            if (iface.SpecialType == SpecialType.System_Collections_IEnumerable)
                return true;
        }

        return false;
    }

    private static CollectionKind ClassifyCollection(ITypeSymbol type, out ITypeSymbol? elementType)
    {
        elementType = null;

        if (type is IArrayTypeSymbol array)
        {
            if (!array.IsSZArray)
                return CollectionKind.None;

            elementType = array.ElementType;
            return CollectionKind.Array;
        }

        if (type is not INamedTypeSymbol named)
            return CollectionKind.None;

        // The runtime engine takes the single generic argument as the element type (MetadataCache.AnalyzeCollectionType).
        ITypeSymbol? enumerableElement = null;
        if (named.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)
        {
            enumerableElement = named.TypeArguments[0];
        }
        else
        {
            foreach (var iface in named.AllInterfaces)
            {
                if (iface.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)
                {
                    enumerableElement = iface.TypeArguments[0];
                    break;
                }
            }
        }

        if (enumerableElement is null)
            return CollectionKind.None;

        if (named.IsGenericType && named.TypeArguments.Length == 1 &&
            !SymbolEqualityComparer.Default.Equals(named.TypeArguments[0], enumerableElement))
        {
            return CollectionKind.None;
        }

        elementType = enumerableElement;

        var definition = named.OriginalDefinition;
        if (definition.Name == "List" && definition.Arity == 1 &&
            definition.ContainingNamespace?.ToDisplayString() == "System.Collections.Generic")
        {
            return CollectionKind.List;
        }

        return CollectionKind.Enumerable;
    }

    private static bool TryGetNullableUnderlying(ITypeSymbol type, out ITypeSymbol underlying)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
        {
            underlying = nullable.TypeArguments[0];
            return true;
        }

        underlying = type;
        return false;
    }

    private static bool TryGetRangeArgument(ITypeSymbol type, out ITypeSymbol argument)
    {
        if (type is INamedTypeSymbol { Name: "Range", Arity: 1 } named &&
            named.ContainingNamespace?.ToDisplayString() == "Qryx.Core.Types")
        {
            argument = named.TypeArguments[0];
            return true;
        }

        argument = type;
        return false;
    }

    private static bool TryResolvePath(
        ITypeSymbol entity,
        string path,
        Compilation compilation,
        out string access,
        out ITypeSymbol memberType,
        out bool accessible,
        out string missingSegment,
        out string missingOwner)
    {
        var parts = new List<string>();
        ITypeSymbol current = entity;
        accessible = true;
        access = string.Empty;
        missingSegment = string.Empty;
        missingOwner = string.Empty;
        memberType = entity;

        foreach (var segment in path.Split('.'))
        {
            var property = FindProperty(current, segment);
            if (property is null)
            {
                missingSegment = segment;
                missingOwner = Display(current);
                return false;
            }

            if (property.GetMethod is null || !compilation.IsSymbolAccessibleWithin(property.GetMethod, compilation.Assembly))
                accessible = false;

            parts.Add(EscapeIdentifier(property.Name));
            current = property.Type;
        }

        access = string.Join(".", parts);
        memberType = current;
        return true;
    }

    /// <summary>Mirrors <c>Type.GetProperty(name, Public | Instance | IgnoreCase)</c>: exact case preferred.</summary>
    private static IPropertySymbol? FindProperty(ITypeSymbol type, string name)
    {
        if (name.Length == 0)
            return null;

        IPropertySymbol? caseInsensitive = null;
        for (var current = type; current is not null; current = current.BaseType)
        {
            foreach (var member in current.GetMembers())
            {
                if (member is not IPropertySymbol { IsStatic: false, IsIndexer: false } property)
                    continue;

                // Reflection treats a property as public when any accessor is public.
                if (property.DeclaredAccessibility != Accessibility.Public &&
                    property.GetMethod?.DeclaredAccessibility != Accessibility.Public &&
                    property.SetMethod?.DeclaredAccessibility != Accessibility.Public)
                {
                    continue;
                }

                if (string.Equals(property.Name, name, StringComparison.Ordinal))
                    return property;

                if (caseInsensitive is null && string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    caseInsensitive = property;
            }
        }

        return caseInsensitive;
    }

    private static bool SupportsOperator(FilterOp op, ITypeSymbol memberType)
    {
        TryGetNullableUnderlying(memberType, out var type);

        var isEquality = op is FilterOp.Equals or FilterOp.NotEquals;
        if (isEquality && (type.IsReferenceType || type.SpecialType == SpecialType.System_Boolean))
            return true;

        if (type.TypeKind == TypeKind.Enum || IsBuiltInNumeric(type.SpecialType))
            return true;

        if (type.SpecialType == SpecialType.System_String)
            return false;

        var operatorName = op switch
        {
            FilterOp.Equals => "op_Equality",
            FilterOp.NotEquals => "op_Inequality",
            FilterOp.GreaterThan => "op_GreaterThan",
            FilterOp.GreaterThanOrEqual => "op_GreaterThanOrEqual",
            FilterOp.LessThan => "op_LessThan",
            _ => "op_LessThanOrEqual"
        };

        for (var current = type; current is not null; current = current.BaseType)
        {
            foreach (var member in current.GetMembers(operatorName))
            {
                if (member is IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator, Parameters.Length: 2 } method &&
                    SymbolEqualityComparer.Default.Equals(method.Parameters[0].Type, type) &&
                    SymbolEqualityComparer.Default.Equals(method.Parameters[1].Type, type))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsBuiltInNumeric(SpecialType specialType) => specialType is
        SpecialType.System_Char or
        SpecialType.System_SByte or SpecialType.System_Byte or
        SpecialType.System_Int16 or SpecialType.System_UInt16 or
        SpecialType.System_Int32 or SpecialType.System_UInt32 or
        SpecialType.System_Int64 or SpecialType.System_UInt64 or
        SpecialType.System_Single or SpecialType.System_Double or
        SpecialType.System_Decimal;

    /// <summary>
    /// The value can be used directly against the member: identical type, lifted nullable, or implicit
    /// numeric widening (the value is cast to the member type so the column is never converted).
    /// </summary>
    private static bool TryGetValueConversion(Compilation compilation, ITypeSymbol valueType, ITypeSymbol memberType, out string? cast)
    {
        cast = null;

        if (SymbolEqualityComparer.Default.Equals(valueType, memberType))
            return true;

        if (TryGetNullableUnderlying(memberType, out var underlying) && SymbolEqualityComparer.Default.Equals(valueType, underlying))
            return true;

        if (compilation is CSharpCompilation csharp)
        {
            var conversion = csharp.ClassifyConversion(valueType, memberType);
            if (conversion.IsImplicit && !conversion.IsUserDefined && (conversion.IsNumeric || conversion.IsNullable))
            {
                cast = memberType.ToDisplayString(FullyQualified);
                return true;
            }
        }

        return false;
    }

    private static bool ContainsTypeParameter(ITypeSymbol type) => type switch
    {
        ITypeParameterSymbol => true,
        IArrayTypeSymbol array => ContainsTypeParameter(array.ElementType),
        INamedTypeSymbol named => named.TypeArguments.Any(ContainsTypeParameter) ||
                                  (named.ContainingType is not null && ContainsTypeParameter(named.ContainingType)),
        _ => false
    };

    private static List<ITypeParameterSymbol> CollectTypeParameters(INamedTypeSymbol type)
    {
        var chain = new List<INamedTypeSymbol>();
        for (var current = type; current is not null; current = current.ContainingType)
            chain.Insert(0, current);

        return chain.SelectMany(static t => t.TypeParameters).ToList();
    }

    private static IEnumerable<string> BuildConstraintClauses(List<ITypeParameterSymbol> typeParameters)
    {
        foreach (var parameter in typeParameters)
        {
            var constraints = new List<string>();

            if (parameter.HasUnmanagedTypeConstraint)
                constraints.Add("unmanaged");
            else if (parameter.HasValueTypeConstraint)
                constraints.Add("struct");
            else if (parameter.HasReferenceTypeConstraint)
                constraints.Add(parameter.ReferenceTypeConstraintNullableAnnotation == NullableAnnotation.Annotated ? "class?" : "class");
            else if (parameter.HasNotNullConstraint)
                constraints.Add("notnull");

            foreach (var constraintType in parameter.ConstraintTypes)
                constraints.Add(constraintType.ToDisplayString(FullyQualified));

            if (parameter.HasConstructorConstraint && !parameter.HasValueTypeConstraint && !parameter.HasUnmanagedTypeConstraint)
                constraints.Add("new()");

            if (constraints.Count > 0)
                yield return $"where {parameter.Name} : {string.Join(", ", constraints)}";
        }
    }

    private enum Access
    {
        None,
        Internal,
        Public
    }

    private static Access MinAccessibility(Access left, Access right) => left < right ? left : right;

    /// <summary>Accessibility of a type as seen from the generated class (same assembly, different type).</summary>
    private static Access EffectiveAccessibility(ITypeSymbol type)
    {
        switch (type)
        {
            case IArrayTypeSymbol array:
                return EffectiveAccessibility(array.ElementType);
            case ITypeParameterSymbol:
                return Access.Public;
            case INamedTypeSymbol named:
            {
                var result = named.DeclaredAccessibility switch
                {
                    Accessibility.Public => Access.Public,
                    Accessibility.Internal or Accessibility.ProtectedOrInternal => Access.Internal,
                    Accessibility.NotApplicable => Access.Public,
                    _ => Access.None
                };

                if (named.ContainingType is not null)
                    result = MinAccessibility(result, EffectiveAccessibility(named.ContainingType));

                foreach (var argument in named.TypeArguments)
                    result = MinAccessibility(result, EffectiveAccessibility(argument));

                return result;
            }
            default:
                return Access.Public;
        }
    }

    /// <summary>
    /// File name from the fully-qualified metadata name: namespace and nesting joined with '.', generic arity
    /// after '-' (never valid in an identifier, so names cannot collide), no prefix for the global namespace.
    /// </summary>
    private static string BuildHintName(INamedTypeSymbol type)
    {
        var parts = new List<string>();
        for (var current = type; current is not null; current = current.ContainingType)
            parts.Insert(0, current.MetadataName.Replace('`', '-'));

        var ns = type.ContainingNamespace;
        var builder = new StringBuilder();
        if (ns is not null && !ns.IsGlobalNamespace)
            builder.Append(ns.ToDisplayString()).Append('.');

        builder.Append(string.Join(".", parts));

        foreach (var invalid in new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*', ' ' })
            builder.Replace(invalid, '_');

        return builder.ToString();
    }

    private static string Display(ITypeSymbol type) => type.ToDisplayString(Minimal);

    private static string ToCamelCase(string name)
    {
        if (name.Length == 0)
            return "value";

        return char.ToLowerInvariant(name[0]) + name.Substring(1);
    }

    internal static string EscapeIdentifier(string name) =>
        SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;
}
