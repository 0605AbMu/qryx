using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.OpenApi;
using Qryx.Core.Constants;
using Qryx.Core.Contracts;
using Qryx.Core.Enums;
using Qryx.Expressions.Metadata;
using Qryx.Expressions.Options;

namespace Qryx.ApiDocs.Internal;

/// <summary>
/// Rewrites the query parameters that ApiExplorer produced for a Qryx query DTO (<see cref="IQryxRequest{TEntity}"/>)
/// so that they match what the Qryx binders actually accept: camelCase keys, all optional, inline schemas for Qryx
/// value types (<c>Range&lt;T&gt;</c>, <c>QryxList&lt;T&gt;</c>, enums, collections) and a description with the filter
/// operator for every <c>[Filter]</c> property. Works on the Microsoft.OpenApi object model, so it is shared (linked
/// source) by the Swashbuckle operation filter and the Microsoft.AspNetCore.OpenApi operation transformer.
/// </summary>
internal static class QryxOperationDocumenter
{
    private static readonly string[] StandardPropertyNames =
    [
        nameof(QryxRequest<object>.Page),
        nameof(QryxRequest<object>.PageSize),
        nameof(QryxRequest<object>.SortBy),
        nameof(QryxRequest<object>.IsDescending),
        nameof(QryxRequest<object>.AfterCursor),
        nameof(QryxRequest<object>.IncludeTotal)
    ];

    /// <summary>Documents every Qryx DTO bound by the endpoint described by <paramref name="description"/>.</summary>
    /// <param name="operation">Operation generated for the endpoint.</param>
    /// <param name="description">ApiExplorer description of the endpoint.</param>
    /// <param name="replacedSchemaIds">
    /// Receives the component schema ids that the replaced parameter schemas referenced (e.g. <c>Int32Range</c>), so
    /// that the caller can drop them from the document when nothing else uses them.
    /// </param>
    public static void Apply(OpenApiOperation operation, ApiDescription description, ISet<string>? replacedSchemaIds = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(description);

        var dtoTypes = FindQryxDtoTypes(description);
        if (dtoTypes.Count == 0)
            return;

        var isMvc = description.ActionDescriptor is ControllerActionDescriptor;
        var options = QryxOptions.Global;
        operation.Parameters ??= new List<IOpenApiParameter>();

        foreach (var dtoType in dtoTypes)
        {
            var entityType = GetEntityType(dtoType);
            if (entityType == null)
                continue;

            var documented = DescribeDto(dtoType, entityType, isMvc, options);
            RewriteParameters(operation.Parameters, documented, replacedSchemaIds);
        }
    }

    /// <summary>Whether <paramref name="type"/> is a concrete Qryx query DTO.</summary>
    public static bool IsQryxDto(Type? type) =>
        type is { IsAbstract: false, IsInterface: false } && GetEntityType(type) != null;

    private static Type? GetEntityType(Type dtoType)
    {
        foreach (var iface in dtoType.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IQryxRequest<>))
                return iface.GetGenericArguments()[0];
        }

        return null;
    }

    private static List<Type> FindQryxDtoTypes(ApiDescription description)
    {
        var result = new List<Type>();

        void Add(Type? type)
        {
            if (type != null && IsQryxDto(type) && !result.Contains(type))
                result.Add(type);
        }

        // MVC: the action parameter ([FromQuery] XQuery q) is the DTO itself.
        foreach (var parameter in description.ActionDescriptor.Parameters)
            Add(parameter.ParameterType);

        foreach (var parameter in description.ParameterDescriptions)
        {
            Add(parameter.Type);
            Add(parameter.ModelMetadata?.ContainerType);
            Add(parameter.ParameterDescriptor?.ParameterType);

            // Minimal APIs: [AsParameters] properties surface as ParameterInfo wrappers whose Member is the property.
            if (parameter.ParameterDescriptor is IParameterInfoParameterDescriptor { ParameterInfo.Member: PropertyInfo property })
                Add(property.ReflectedType);
        }

        // Minimal APIs: the handler method is part of the endpoint metadata.
        foreach (var metadata in description.ActionDescriptor.EndpointMetadata)
        {
            if (metadata is MethodInfo method)
            {
                foreach (var parameter in method.GetParameters())
                    Add(parameter.ParameterType);
            }
        }

        return result;
    }

    private static Dictionary<string, QryxParameterDoc> DescribeDto(Type dtoType, Type entityType, bool isMvc, QryxOptions options)
    {
        var dtoMetadata = MetadataCache.GetDtoMetadata(dtoType);
        var entityMetadata = MetadataCache.GetEntityMetadata(entityType);
        var docs = new Dictionary<string, QryxParameterDoc>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in StandardPropertyNames)
        {
            var property = dtoType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property == null)
                continue;

            var excluded = name == nameof(QryxRequest<object>.IncludeTotal) && !dtoMetadata.AllowTotal;
            docs[name] = excluded
                ? QryxParameterDoc.Excluded(name)
                : DescribeStandard(name, dtoMetadata, entityMetadata, options);
        }

        foreach (var filter in dtoMetadata.Filters)
        {
            if (!docs.ContainsKey(filter.DtoPropertyName))
                docs[filter.DtoPropertyName] = DescribeFilter(filter, entityMetadata, isMvc);
        }

        return docs;
    }

    private static void RewriteParameters(
        IList<IOpenApiParameter> parameters,
        Dictionary<string, QryxParameterDoc> docs,
        ISet<string>? replacedSchemaIds)
    {
        var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < parameters.Count; i++)
        {
            if (parameters[i] is not OpenApiParameter parameter || parameter.In is not ParameterLocation.Query ||
                string.IsNullOrEmpty(parameter.Name))
            {
                continue;
            }

            // MVC ApiExplorer expands complex values into "AgeRange.From" / "AgeRange.To": collapse them into "ageRange".
            var dot = parameter.Name.IndexOf('.');
            var rootName = dot < 0 ? parameter.Name : parameter.Name[..dot];
            if (!docs.TryGetValue(rootName, out var doc))
                continue;

            if (replacedSchemaIds != null)
                CollectSchemaReferences(parameter.Schema, replacedSchemaIds);

            if (doc.IsExcluded || !handled.Add(doc.PropertyName))
            {
                parameters.RemoveAt(i--);
                continue;
            }

            doc.ApplyTo(parameter, existingDescription: dot < 0 ? parameter.Description : null);
        }

        // Parameters that ApiExplorer did not produce at all (e.g. a type it could not describe) are added, but only
        // when the DTO is bound from the query string at all (not e.g. [FromBody] or under a custom prefix).
        if (handled.Count == 0)
            return;

        foreach (var doc in docs.Values)
        {
            if (doc.IsExcluded || handled.Contains(doc.PropertyName))
                continue;

            var parameter = new OpenApiParameter { In = ParameterLocation.Query };
            doc.ApplyTo(parameter, existingDescription: null);
            parameters.Add(parameter);
        }
    }

    private static void CollectSchemaReferences(IOpenApiSchema? schema, ISet<string> ids, int depth = 0)
    {
        if (schema == null || depth > 4)
            return;

        if (schema is OpenApiSchemaReference reference)
        {
            if (!string.IsNullOrEmpty(reference.Reference.Id))
                ids.Add(reference.Reference.Id);
            return;
        }

        CollectSchemaReferences(schema.Items, ids, depth + 1);
        foreach (var group in new[] { schema.AllOf, schema.AnyOf, schema.OneOf })
        {
            if (group == null)
                continue;
            foreach (var item in group)
                CollectSchemaReferences(item, ids, depth + 1);
        }
    }

    private static QryxParameterDoc DescribeStandard(string name, DtoMetadata dto, EntityMetadata entity, QryxOptions options)
    {
        var maxPageSize = options.MaxPageSize;
        var defaultPageSize = Math.Min(options.DefaultPageSize, maxPageSize);

        return name switch
        {
            nameof(QryxRequest<object>.Page) => new QryxParameterDoc(
                name,
                new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int32", Minimum = "1", Default = JsonValue.Create(QryxConstants.DefaultPage) },
                $"1-based page number (offset pagination). Default: {QryxConstants.DefaultPage}. Ignored when afterCursor is set."),

            nameof(QryxRequest<object>.PageSize) => new QryxParameterDoc(
                name,
                new OpenApiSchema
                {
                    Type = JsonSchemaType.Integer,
                    Format = "int32",
                    Minimum = "1",
                    Maximum = maxPageSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Default = JsonValue.Create(defaultPageSize)
                },
                $"Items per page. Default: {defaultPageSize}; larger values are clamped to {maxPageSize}."),

            nameof(QryxRequest<object>.SortBy) => new QryxParameterDoc(
                name,
                new OpenApiSchema { Type = JsonSchemaType.String, MaxLength = QryxConstants.MaxSortByLength },
                DescribeSortBy(dto, entity)),

            nameof(QryxRequest<object>.IsDescending) => new QryxParameterDoc(
                name,
                new OpenApiSchema { Type = JsonSchemaType.Boolean, Default = JsonValue.Create(false) },
                "Default direction for sortBy fields without asc/desc. Default: false."),

            nameof(QryxRequest<object>.AfterCursor) => new QryxParameterDoc(
                name,
                new OpenApiSchema { Type = JsonSchemaType.String },
                "Opaque keyset cursor: pass the nextCursor value of the previous page to get the next one (page is then ignored)."),

            _ => new QryxParameterDoc(
                name,
                new OpenApiSchema { Type = JsonSchemaType.Boolean, Default = JsonValue.Create(false) },
                "Also return the total number of matching rows (total). Default: false; true runs an extra COUNT query.")
        };
    }

    private static string DescribeSortBy(DtoMetadata dto, EntityMetadata entity)
    {
        var fields = new SortedSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in dto.SortableProperties.Concat(entity.SortableProperties))
        {
            if (entity.HasProperty(name) && seen.Add(name))
                fields.Add(ToCamelCasePath(name));
        }

        var text = new StringBuilder(
            $"Comma-separated sort fields, each optionally followed by asc or desc (e.g. \"createdAt desc, name\"); at most {QryxConstants.MaxSortClauses} fields.");
        text.Append(fields.Count > 0
            ? $" Sortable fields: {string.Join(", ", fields)}."
            : " This query declares no sortable fields.");
        return text.ToString();
    }

    private static QryxParameterDoc DescribeFilter(FilterPropertyMetadata filter, EntityMetadata entity, bool isMvc)
    {
        var shape = QryxValueTypes.GetShape(filter.DtoPropertyType, out var elementType);
        var queryName = ToCamelCase(filter.DtoPropertyName);
        var description = new StringBuilder("Filter: ").Append(DescribeOperator(filter, shape)).Append('.');
        OpenApiSchema schema;
        ParameterStyle? style = null;
        bool? explode = null;
        JsonNode? example = null;

        switch (shape)
        {
            case QryxValueShape.Range:
            {
                var (low, high) = QryxValueTypes.GetSampleValues(elementType, forRange: true);
                schema = new OpenApiSchema { Type = JsonSchemaType.String };
                example = JsonValue.Create($"{low}..{high}");
                description.Append($" Inclusive range from..to, from.. or ..to of {QryxValueTypes.DescribeType(elementType)} (e.g. {low}..{high}).");
                break;
            }
            case QryxValueShape.CommaSeparatedList:
            {
                var (low, high) = QryxValueTypes.GetSampleValues(elementType);
                schema = new OpenApiSchema { Type = JsonSchemaType.Array, Items = QryxValueTypes.CreateScalarSchema(elementType) };
                style = ParameterStyle.Form;
                explode = false;
                description.Append($" Comma-separated values (e.g. {low},{high}); repeated keys are also accepted.");
                if (QryxValueTypes.Unwrap(elementType).IsEnum)
                    description.Append(" Enum names are case-insensitive; numeric values are also accepted.");
                break;
            }
            case QryxValueShape.RepeatedKeys:
            {
                var (low, high) = QryxValueTypes.GetSampleValues(elementType);
                schema = new OpenApiSchema { Type = JsonSchemaType.Array, Items = QryxValueTypes.CreateScalarSchema(elementType) };
                style = ParameterStyle.Form;
                explode = true;
                description.Append($" Repeat the key for each value (e.g. {queryName}={low}&{queryName}={high}).");
                if (QryxValueTypes.Unwrap(elementType).IsEnum)
                    description.Append(EnumCasingNote(isMvc));
                break;
            }
            default:
            {
                schema = QryxValueTypes.CreateScalarSchema(elementType);
                var valueType = QryxValueTypes.Unwrap(elementType);
                if (valueType.IsEnum)
                {
                    description.Append(EnumCasingNote(isMvc));
                }
                else if (valueType == typeof(string) && filter.TargetPropertyPaths.Count == 1 &&
                         entity.ResolveProperty(filter.TargetPropertyPaths[0])?.PropertyType is { } targetType &&
                         QryxValueTypes.Unwrap(targetType).IsEnum)
                {
                    // string DTO property mapped to an enum column: the query engine parses it case-insensitively.
                    description.Append($" One of: {string.Join(", ", Enum.GetNames(QryxValueTypes.Unwrap(targetType)))} (case-insensitive).");
                }

                break;
            }
        }

        return new QryxParameterDoc(filter.DtoPropertyName, schema, description.ToString(), style, explode, example, isFilter: true);
    }

    private static string EnumCasingNote(bool isMvc) => isMvc
        ? " Enum names are case-insensitive; numeric values are also accepted."
        : " Enum names are case-sensitive; numeric values are also accepted.";

    private static string DescribeOperator(FilterPropertyMetadata filter, QryxValueShape shape)
    {
        var targets = string.Join(" or ", filter.TargetPropertyPaths);
        var casing = filter.Invariant ? " (case-insensitive)" : " (case-sensitive)";

        return filter.Operator switch
        {
            Op.Equals => $"{targets} = value",
            Op.NotEquals => $"{targets} != value",
            Op.GreaterThan => $"{targets} > value",
            Op.GreaterThanOrEqual => $"{targets} >= value",
            Op.LessThan => $"{targets} < value",
            Op.LessThanOrEqual => $"{targets} <= value",
            Op.StartsWith => $"{targets} starts with value{casing}",
            Op.EndsWith => $"{targets} ends with value{casing}",
            Op.Contains => $"{targets} contains value{casing}",
            Op.In => $"{targets} in values",
            Op.NotIn => $"{targets} not in values",
            Op.Between when shape == QryxValueShape.Range => $"{targets} between from and to (inclusive)",
            Op.Between => $"{targets} = value",
            Op.IsNull => $"true: {targets} is null, false: {targets} is not null",
            Op.IsNotNull => $"true: {targets} is not null, false: {targets} is null",
            Op.Fts => $"full-text search in {targets} (web search syntax: \"phrase\", or, -word)",
            Op.JsonContains => $"{targets} contains the JSON value (object/array) or has the top-level key",
            _ => $"{targets} {filter.Operator} value"
        };
    }

    internal static string ToCamelCase(string name) => JsonNamingPolicy.CamelCase.ConvertName(name);

    private static string ToCamelCasePath(string path) =>
        path.Contains('.') ? string.Join('.', path.Split('.').Select(ToCamelCase)) : ToCamelCase(path);

    /// <summary>Documentation of one Qryx query parameter.</summary>
    private sealed class QryxParameterDoc
    {
        private readonly OpenApiSchema? _schema;
        private readonly string? _description;
        private readonly ParameterStyle? _style;
        private readonly bool? _explode;
        private readonly JsonNode? _example;
        private readonly bool _isFilter;

        public QryxParameterDoc(
            string propertyName,
            OpenApiSchema schema,
            string description,
            ParameterStyle? style = null,
            bool? explode = null,
            JsonNode? example = null,
            bool isFilter = false)
        {
            PropertyName = propertyName;
            _schema = schema;
            _description = description;
            _style = style;
            _explode = explode;
            _example = example;
            _isFilter = isFilter;
        }

        private QryxParameterDoc(string propertyName)
        {
            PropertyName = propertyName;
            IsExcluded = true;
        }

        public static QryxParameterDoc Excluded(string propertyName) => new(propertyName);

        public string PropertyName { get; }

        /// <summary>The parameter is ignored by the binder for this DTO and must not be documented.</summary>
        public bool IsExcluded { get; }

        public void ApplyTo(OpenApiParameter parameter, string? existingDescription)
        {
            parameter.Name = ToCamelCase(PropertyName);
            parameter.In = ParameterLocation.Query;
            parameter.Required = false;
            parameter.Content = null;
            parameter.Schema = _schema;
            parameter.Example = _example?.DeepClone();
            parameter.Examples = null;

            if (_style != null)
                parameter.Style = _style;
            if (_explode != null)
                parameter.Explode = _explode.Value;

            // Keep a user-written description (XML docs) of a filter property and append the operator hint.
            if (!_isFilter || string.IsNullOrWhiteSpace(existingDescription))
                parameter.Description = _description;
            else if (!existingDescription.Contains(_description!, StringComparison.Ordinal))
                parameter.Description = $"{existingDescription.TrimEnd()} {_description}";
        }
    }
}
