using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using static Respire.Analyzers.GeneratedModelBuilder;

namespace Respire.Analyzers;

/// <summary>Generates typed Search schemas for mapped models without runtime reflection.</summary>
[Generator(LanguageNames.CSharp)]
public sealed class RespireSearchGenerator : IIncrementalGenerator
{
    private const string FieldAttribute = "Respire.Search.RespireSearchFieldAttribute";
    private static readonly DiagnosticDescriptor InvalidSchema = new(
        DiagnosticIds.InvalidGeneratedSearch, "Unsupported Search schema", "{0}", DiagnosticIds.Category,
        DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://thomhurst.github.io/Respire/docs/guides/generated-search-schemas");

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var models = context.SyntaxProvider.ForAttributeWithMetadataName("Respire.Search.RespireSearchAttribute",
            static (node, _) => node is TypeDeclarationSyntax,
            static (attribute, token) => BuildSchema((INamedTypeSymbol)attribute.TargetSymbol,
                attribute.Attributes[0], attribute.SemanticModel.Compilation.GetSpecialType(SpecialType.System_Object),
                token)).WithTrackingName("RespireSearchModels");
        context.RegisterSourceOutput(models, static (output, model) =>
        {
            if (model.Error is not null)
                output.ReportDiagnostic(Diagnostic.Create(InvalidSchema,
                    Location.Create(model.Path, model.Span, model.LineSpan), model.Error));
            else output.AddSource(model.HintName!, SourceText.From(model.Source!, Encoding.UTF8));
        });
        // Roslyn's attribute provider does not visit property-target attributes on record parameters.
        var orphanFields = context.SyntaxProvider.CreateSyntaxProvider(
            static (node, _) => node is TypeDeclarationSyntax,
            static (syntax, token) => FindOrphanField(syntax, token));
        context.RegisterSourceOutput(orphanFields, static (output, model) =>
        {
            if (model is not null) output.ReportDiagnostic(Diagnostic.Create(InvalidSchema,
                Location.Create(model.Path, model.Span, model.LineSpan), model.Error));
        });
    }

    private static GeneratedModel? FindOrphanField(GeneratorSyntaxContext syntax, CancellationToken token)
    {
        if (syntax.SemanticModel.GetDeclaredSymbol(syntax.Node, token) is not INamedTypeSymbol type
            || type.DeclaringSyntaxReferences[0].Span != syntax.Node.Span
            || type.GetAttributes().Any(candidate => candidate.AttributeClass?.ToDisplayString() == "Respire.Search.RespireSearchAttribute")) return null;
        var property = type.GetMembers().OfType<IPropertySymbol>().FirstOrDefault(property =>
            property.GetAttributes().Any(candidate => candidate.AttributeClass?.ToDisplayString() == FieldAttribute));
        return property is null ? null : GeneratedModel.Failed(property, "Indexed properties need [RespireSearch] on their mapped model.");
    }

    internal static AttributeData? VectorAttribute(IPropertySymbol property) => property.GetAttributes()
        .FirstOrDefault(attribute => attribute.AttributeClass?.ToDisplayString() == FieldAttribute
            && attribute.ConstructorArguments.FirstOrDefault().Value is 5);

    internal static bool IsVectorProperty(IPropertySymbol property, bool json) => VectorAttribute(property) is not null
        && property.ContainingType.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString() == "Respire.Search.RespireSearchAttribute")
        && property.Type is IArrayTypeSymbol { Rank: 1 } array
        && (json ? array.ElementType.SpecialType is SpecialType.System_Single or SpecialType.System_Double
            : array.ElementType.SpecialType == SpecialType.System_Byte);

    internal static string VectorOptions(AttributeData attribute) =>
        "new global::Respire.Search.RespireSearchVectorOptions((global::Respire.Search.RespireSearchVectorAlgorithm)"
        + Option(attribute, "Algorithm", 0) + ", (global::Respire.Search.RespireSearchVectorType)"
        + Option(attribute, "VectorType", 0) + ", " + Option(attribute, "Dimensions", 0)
        + ", (global::Respire.Search.RespireSearchDistanceMetric)" + Option(attribute, "DistanceMetric", 2) + ")";

    private static T Option<T>(AttributeData attribute, string name, T fallback) =>
        attribute.NamedArguments.FirstOrDefault(pair => pair.Key == name).Value.Value is T value ? value : fallback;

    private static bool Has(AttributeData attribute, params string[] names) =>
        attribute.NamedArguments.Any(pair => names.Contains(pair.Key));

    private static GeneratedModel BuildSchema(INamedTypeSymbol type, AttributeData attribute,
        INamedTypeSymbol objectType, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var mappings = type.GetAttributes().Where(candidate => candidate.AttributeClass?.ToDisplayString()
            is "Respire.RespireHashAttribute" or "Respire.Json.RespireJsonAttribute").ToArray();
        if (mappings.Length != 1) return GeneratedModel.Failed(type, "Search schemas require exactly one [RespireHash] or [RespireJson] mapping.");
        var json = mappings[0].AttributeClass!.Name == "RespireJsonAttribute";
        var mapped = Build(type, mappings[0], token, json);
        if (mapped.Error is not null) return mapped;
        if (attribute.ConstructorArguments.FirstOrDefault().Value is not string index || string.IsNullOrWhiteSpace(index))
            return GeneratedModel.Failed(type, "Provide a non-empty Search index name.");
        var prefixArgument = attribute.NamedArguments.FirstOrDefault(pair => pair.Key == "Prefixes").Value;
        if (prefixArgument.IsNull || prefixArgument.Kind != TypedConstantKind.Array || prefixArgument.Values.Length == 0
            || prefixArgument.Values.Any(prefix => prefix.Value is not string text || string.IsNullOrWhiteSpace(text)))
            return GeneratedModel.Failed(type, "Provide at least one non-empty Search key prefix in Prefixes.");
        var name = type.Name + "SearchSchema";
        if (type.ContainingNamespace.GetMembers(name).Any())
            return GeneratedModel.Failed(type, $"Generated schema '{name}' already exists in this namespace.");
        var properties = type.GetMembers().OfType<IPropertySymbol>().Where(property =>
            property.GetAttributes().Any(candidate => candidate.AttributeClass?.ToDisplayString() == FieldAttribute)).ToArray();
        if (properties.Length == 0) return GeneratedModel.Failed(type, "Index at least one mapped property with [RespireSearchField].");
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        var fields = new List<string>();
        foreach (var property in properties)
        {
            token.ThrowIfCancellationRequested();
            var field = property.GetAttributes().First(candidate => candidate.AttributeClass?.ToDisplayString() == FieldAttribute);
            var fieldType = field.ConstructorArguments.FirstOrDefault().Value is int value ? value : -1;
            var alias = Option(field, "Alias", property.Name);
            if (property.IsStatic || property.DeclaredAccessibility != Accessibility.Public || string.IsNullOrWhiteSpace(alias)
                || !aliases.Add(alias)) return GeneratedModel.Failed(property, "Indexed properties must be public instance properties with distinct non-empty aliases.");
            var scalar = ScalarKind(property.Type);
            if (fieldType is < 0 or > 5
                || fieldType is 0 or 1 or 3 or 4 && scalar != "String"
                || fieldType == 2 && scalar is not ("Int32" or "Int64" or "Double" or "Decimal")
                || fieldType == 5 && !IsVectorProperty(property, json))
                return GeneratedModel.Failed(property, "TEXT/TAG/GEO/GEOSHAPE require string; NUMERIC requires int/long/double/decimal; VECTOR requires byte[] for hashes or float[]/double[] for JSON.");
            if (fieldType != 0 && Has(field, "Weight", "NoStem")
                || fieldType != 1 && Has(field, "Separator", "CaseSensitive")
                || fieldType != 5 && Has(field, "Dimensions", "VectorType", "Algorithm", "DistanceMetric"))
                return GeneratedModel.Failed(property, "Text, tag and vector options may only be set on their corresponding field types.");
            var weight = Option(field, "Weight", 1d);
            var separator = Option(field, "Separator", '\0');
            if (double.IsNaN(weight) || double.IsInfinity(weight) || weight < 0 || separator > 127)
                return GeneratedModel.Failed(property, "Text weight must be finite and non-negative; tag separator must be ASCII.");
            if (fieldType == 5)
            {
                var vectorType = Option(field, "VectorType", 0);
                var dimensions = Option(field, "Dimensions", 0);
                var elementSize = vectorType switch { 0 => 4, 1 => 8, 2 or 3 => 2, _ => 1 };
                if (dimensions <= 0 || vectorType is < 0 or > 5 || Option(field, "Algorithm", 0) is < 0 or > 1
                    || Option(field, "DistanceMetric", 2) is < 0 or > 2
                    || Option(field, "Sortable", false) || Option(field, "NoIndex", false)
                    || !json && (long)dimensions * elementSize > int.MaxValue
                    || json && vectorType != (((IArrayTypeSymbol)property.Type).ElementType.SpecialType == SpecialType.System_Single ? 0 : 1))
                    return GeneratedModel.Failed(property, "Vectors need positive representable dimensions, valid algorithm/type/metric, matching JSON element type, and cannot be SORTABLE or NOINDEX.");
            }
            var identifier = json ? "$[\"" + RespireJsonGenerator.EscapePathName(RespireJsonGenerator.JsonName(property)) + "\"]" : property.Name;
            var options = new StringBuilder("new global::Respire.Search.RespireSearchField(")
                .Append(Literal(identifier)).Append(", (global::Respire.Search.RespireSearchFieldType)").Append(fieldType)
                .Append(", ").Append(Literal(alias)).Append(", Sortable: ").Append(Option(field, "Sortable", false) ? "true" : "false")
                .Append(", NoIndex: ").Append(Option(field, "NoIndex", false) ? "true" : "false").Append(") { ");
            if (fieldType == 0) options.Append("Weight = ").Append(weight.ToString("R", CultureInfo.InvariantCulture)).Append("d, NoStem = ")
                .Append(Option(field, "NoStem", false) ? "true" : "false").Append(", ");
            if (fieldType == 1) options.Append("CaseSensitive = ").Append(Option(field, "CaseSensitive", false) ? "true" : "false")
                .Append(", Separator = ").Append(separator == '\0' ? "null" : "(char)" + (int)separator).Append(", ");
            if (fieldType == 5) options.Append("Vector = ").Append(VectorOptions(field)).Append(", ");
            fields.Add(options.Append('}').ToString());
        }
        var model = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var fieldCollection = "SchemaFields";
        while (properties.Any(property => property.Name == fieldCollection)) fieldCollection += "_";
        var source = new StringBuilder("// <auto-generated/>\n#nullable enable\n");
        if (!type.ContainingNamespace.IsGlobalNamespace) source.Append("namespace ").Append(type.ContainingNamespace.ToDisplayString()).Append(" {\n");
        source.Append(type.DeclaredAccessibility == Accessibility.Public ? "public" : "internal").Append(" sealed class ")
            .Append(name).Append(" : global::Respire.Search.IRespireSearchSchema<").Append(model).Append(">\n{\n    private ")
            .Append(name).Append("() { }\n    public static string IndexName => ").Append(Literal(index)).Append(";\n")
            .Append("    public static global::Respire.RespireKey GetKey(").Append(model).Append(" value) => ")
            .Append(type.Name).Append(json ? "JsonMapper" : "HashMapper").Append(".GetKey(value);\n")
            .Append("    public static global::Respire.Search.RespireSearchIndexDefinition Definition => new() { Source = global::Respire.Search.RespireSearchSource.")
            .Append(json ? "Json" : "Hash").Append(", Prefixes = global::System.Array.AsReadOnly(new string[] { ")
            .Append(string.Join(", ", prefixArgument.Values.Select(prefix => Literal((string)prefix.Value!)))).Append(" }), Fields = global::System.Array.AsReadOnly(new global::Respire.Search.RespireSearchField[] { ")
            .Append(string.Join(", ", properties.Select(property => "Fields.@" + property.Name))).Append(" }) };\n")
            .Append("    public static ").Append(fieldCollection).Append(" Fields { get; } = new();\n")
            .Append("    public sealed class ").Append(fieldCollection).Append("\n    {\n        internal ").Append(fieldCollection).Append("() { }\n");
        for (var i = 0; i < properties.Length; i++)
        {
            var hidesInheritedMember = objectType.GetMembers(properties[i].Name)
                .Any(member => member.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal
                    && member is not IMethodSymbol { MethodKind: MethodKind.Destructor });
            source.Append("        public ").Append(hidesInheritedMember ? "new " : "")
                .Append("global::Respire.Search.RespireSearchField @")
                .Append(properties[i].Name).Append(" { get; } = ").Append(fields[i]).Append(";\n");
        }
        source.Append("    }\n    public static void Validate(").Append(model).Append(" value)\n    {\n        global::System.ArgumentNullException.ThrowIfNull(value);\n");
        foreach (var property in properties.Where(property => VectorAttribute(property) is not null))
        {
            if (IsNullable(property.Type)) source.Append("        if (value.@").Append(property.Name).Append(" is not null)\n    ");
            source.Append("        global::Respire.Search.RespireSearchVectorValidation.Validate").Append(json ? "Json" : "Hash")
                .Append("(value.@").Append(property.Name).Append(", Fields.@").Append(property.Name).Append(".Vector!);\n");
        }
        source.Append("    }\n}\n");
        if (!type.ContainingNamespace.IsGlobalNamespace) source.Append("}\n");
        return new GeneratedModel(type.ToDisplayString().Replace("@", "") + ".SearchSchema.g.cs", source.ToString(), null, "", default, default);
    }
}
