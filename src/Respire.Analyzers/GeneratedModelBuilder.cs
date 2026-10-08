using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Respire.Analyzers;

internal static class GeneratedModelBuilder
{
    internal static GeneratedModel Build(INamedTypeSymbol type, AttributeData attribute, CancellationToken token, bool json = false)
    {
        token.ThrowIfCancellationRequested();
        var kind = json ? "JSON" : "Hash";
        if (type.TypeKind != TypeKind.Class || type.IsAbstract || type.IsStatic || type.IsFileLocal
            || type.Arity != 0 || type.ContainingType is not null
            || type.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal)
            || type.BaseType?.SpecialType != SpecialType.System_Object
            || type.DeclaringSyntaxReferences.Any(reference => reference.GetSyntax(token) is not TypeDeclarationSyntax declaration
                || !declaration.Modifiers.Any(SyntaxKind.PartialKeyword)))
            return GeneratedModel.Failed(type, $"{kind} models must be public or internal, top-level, non-generic partial classes or record classes without inheritance.");

        if (type.GetMembers().OfType<IFieldSymbol>().Any(field => !field.IsStatic && !field.IsImplicitlyDeclared
                && field.DeclaredAccessibility == Accessibility.Public))
            return GeneratedModel.Failed(type, "Use public properties instead of public instance fields in a model.");

        var properties = type.GetMembers().OfType<IPropertySymbol>()
            .Where(property => !property.IsStatic && !property.IsImplicitlyDeclared
                && property.DeclaredAccessibility == Accessibility.Public).ToArray();
        if (properties.Length == 0)
            return GeneratedModel.Failed(type, $"{kind} models need at least one public instance property.");
        if (properties.Select(property => property.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != properties.Length)
            return GeneratedModel.Failed(type, $"{kind} property names must be distinct ignoring case so constructor parameter binding is unambiguous.");
        foreach (var property in properties)
        {
            token.ThrowIfCancellationRequested();
            if (property.IsIndexer || property.GetMethod?.DeclaredAccessibility != Accessibility.Public
                || property.SetMethod?.DeclaredAccessibility != Accessibility.Public || property.ReturnsByRef
                || property.ReturnsByRefReadonly || ScalarKind(property.Type) is null)
                return GeneratedModel.Failed(property, $"Property '{property.Name}' needs public get and set/init accessors and a supported scalar type: string, bool, int, long, double, decimal, Guid or DateTimeOffset (including nullable variants).");
            var ttl = json ? null : FieldTtl(property);
            if (ttl is not null && ttl <= 0)
                return GeneratedModel.Failed(property, "[RespireFieldTtl] requires a positive expiry in milliseconds.");
        }

        if (json && type.GetMembers().Any(member => member.GetAttributes().Any(attribute =>
                attribute.AttributeClass?.ContainingNamespace.ToDisplayString() == "System.Text.Json.Serialization"
                && (attribute.AttributeClass.Name != "JsonPropertyNameAttribute" || member is not IPropertySymbol))))
            return GeneratedModel.Failed(type, "JSON models support [JsonPropertyName] on properties only; other System.Text.Json member attributes are not supported.");
        if (json && type.GetAttributes().Any(attribute =>
                attribute.AttributeClass?.ContainingNamespace.ToDisplayString() == "System.Text.Json.Serialization"))
            return GeneratedModel.Failed(type, "System.Text.Json type attributes are not supported on generated JSON models.");
        if (json && properties.Any(property => property.GetAttributes().Any(attribute =>
                attribute.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonPropertyNameAttribute"
                && attribute.ConstructorArguments.FirstOrDefault().Value is not string)))
            return GeneratedModel.Failed(type, "[JsonPropertyName] needs a non-null JSON property name.");
        if (json && properties.Select(RespireJsonGenerator.JsonName).Distinct(StringComparer.Ordinal).Count() != properties.Length)
            return GeneratedModel.Failed(type, "JSON property names must be distinct.");

        var constructor = type.InstanceConstructors.Where(candidate =>
                candidate.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal
                && candidate.Parameters.All(parameter => parameter.RefKind == RefKind.None
                    && properties.Any(property => property.Name.Equals(parameter.Name, StringComparison.OrdinalIgnoreCase)
                        && SymbolEqualityComparer.IncludeNullability.Equals(property.Type, parameter.Type))))
            .OrderBy(candidate => candidate.Parameters.Length).FirstOrDefault();
        if (constructor is null)
            return GeneratedModel.Failed(type, $"{kind} models need an accessible constructor whose parameters match public properties by name and type, or an accessible parameterless constructor.");
        if (!SetsRequiredMembers(constructor) && type.GetMembers().Any(member =>
                member.DeclaredAccessibility != Accessibility.Public
                && (member is IPropertySymbol { IsRequired: true } or IFieldSymbol { IsRequired: true })))
            return GeneratedModel.Failed(type, "Non-public required members need a selected constructor marked [SetsRequiredMembers].");

        var mapper = type.Name + (json ? "JsonMapper" : "HashMapper");
        if (type.ContainingNamespace.GetMembers(mapper).Any())
            return GeneratedModel.Failed(type, $"Generated mapper '{mapper}' already exists in this namespace.");
        if (attribute.ConstructorArguments.Length != 1 || attribute.ConstructorArguments[0].Value is not string template
            || template.Length == 0)
            return GeneratedModel.Failed(type, $"Provide a non-empty key template to [Respire{(json ? "Json" : "Hash")}].");
        var key = RenderKey(template, properties);
        if (key is null)
            return GeneratedModel.Failed(type, "Key templates use non-nullable property placeholders such as {Id}; escape literal braces as {{ and }}. Every placeholder must name a mapped property.");

        return new GeneratedModel(type.ToDisplayString().Replace("@", "") + (json ? ".JsonMapper.g.cs" : ".HashMapper.g.cs"),
            json ? RespireJsonGenerator.Render(type, mapper, properties, constructor, key) : RenderHash(type, mapper, properties, constructor, key), null, "", default, default);
    }

    private static string RenderHash(INamedTypeSymbol type, string mapper, IPropertySymbol[] properties,
        IMethodSymbol constructor, string key)
    {
        var modelType = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var source = new StringBuilder("// <auto-generated/>\n#nullable enable\n");
        if (!type.ContainingNamespace.IsGlobalNamespace)
            source.Append("namespace ").Append(type.ContainingNamespace.ToDisplayString()).Append(" {\n");
        source.Append("/// <summary>Generated scalar hash codec and Redis model operations.</summary>\n")
            .Append(type.DeclaredAccessibility == Accessibility.Public ? "public" : "internal")
            .Append(" static class ").Append(mapper).Append("\n{\n")
            .Append("    /// <summary>Expands the model key template.</summary>\n")
            .Append("    public static global::Respire.RespireKey GetKey(").Append(modelType).Append(" value)\n    {\n")
            .Append("        global::System.ArgumentNullException.ThrowIfNull(value);\n")
            .Append("        return new global::Respire.RespireKey(").Append(key).Append(");\n    }\n")
            .Append("    /// <summary>Encodes public scalar properties; null values are absent fields.</summary>\n")
            .Append("    public static global::System.Collections.Generic.Dictionary<string, string> ToFields(")
            .Append(modelType).Append(" value)\n    {\n")
            .Append("        global::System.ArgumentNullException.ThrowIfNull(value);\n")
            .Append("        var fields = new global::System.Collections.Generic.Dictionary<string, string>(")
            .Append(properties.Length).Append(", global::System.StringComparer.Ordinal);\n");
        for (var index = 0; index < properties.Length; index++)
        {
            var property = properties[index];
            var value = "value.@" + property.Name;
            if (IsNullable(property.Type))
            {
                value = "__value" + index;
                source.Append("        if (value.@").Append(property.Name).Append(" is { } ").Append(value).Append(")\n    ");
            }
            source.Append("        fields.Add(").Append(Literal(property.Name)).Append(", ")
                .Append(Encode(property, value)).Append(");\n");
        }
        source.Append("        return fields;\n    }\n")
            .Append("    /// <summary>Decodes a full hash representation; missing required fields fail.</summary>\n")
            .Append("    public static ").Append(modelType)
            .Append(" FromFields(global::System.Collections.Generic.IReadOnlyDictionary<string, string> fields)\n    {\n")
            .Append("        global::System.ArgumentNullException.ThrowIfNull(fields);\n");
        for (var index = 0; index < properties.Length; index++)
        {
            var property = properties[index];
            source.Append("        var __field").Append(index).Append(" = ");
            if (IsNullable(property.Type))
                source.Append("fields.TryGetValue(").Append(Literal(property.Name)).Append(", out var __text")
                    .Append(index).Append(") && __text").Append(index).Append(" is not null ? (")
                    .Append(property.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat
                        .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier)))
                    .Append(')').Append(Decode(property.Type, "__text" + index)).Append(" : null");
            else
                source.Append(Decode(property.Type, "RequiredField(fields, " + Literal(property.Name) + ")"));
            source.Append(";\n");
        }
        var constructorProperties = constructor.Parameters.Select(parameter => Array.FindIndex(properties,
            property => property.Name.Equals(parameter.Name, StringComparison.OrdinalIgnoreCase))).ToArray();
        var setsRequiredMembers = SetsRequiredMembers(constructor);
        var initializerProperties = Enumerable.Range(0, properties.Length).Where(index =>
            !constructorProperties.Contains(index) || properties[index].IsRequired && !setsRequiredMembers).ToArray();
        source.Append("        return new ").Append(modelType).Append('(')
            .Append(string.Join(", ", constructorProperties.Select(index => "__field" + index))).Append(')');
        if (initializerProperties.Length != 0)
        {
            source.Append("\n        {\n");
            // Bound properties need another assignment only when C# required-member rules demand it.
            foreach (var index in initializerProperties)
                source.Append("            @").Append(properties[index].Name).Append(" = __field").Append(index).Append(",\n");
            source.Append("        }");
        }
        source.Append(";\n    }\n");
        RenderIO(source, modelType, properties);
        source.Append("    private static string RequiredField(global::System.Collections.Generic.IReadOnlyDictionary<string, string> fields, string name)\n")
            .Append("        => fields.TryGetValue(name, out var value) && value is not null ? value : throw new global::System.FormatException(\"Missing required hash field: \" + name);\n")
            .Append("    private static string RequiredValue(string? value, string name)\n")
            .Append("        => value ?? throw new global::System.ArgumentException(\"Null required hash property: \" + name, \"value\");\n")
            .Append("    private static bool ParseBoolean(string value)\n")
            .Append("        => value switch { \"1\" => true, \"0\" => false, _ => throw new global::System.FormatException(\"Boolean hash fields must be 1 or 0.\") };\n")
            .Append("    // Double.Parse trims special values even when whitespace styles are disabled.\n")
            .Append("    private static string UnpaddedDoubleText(string value)\n")
            .Append("        => value.Length != 0 && (global::System.Char.IsWhiteSpace(value[0]) || global::System.Char.IsWhiteSpace(value[value.Length - 1])) ? throw new global::System.FormatException(\"Numeric hash fields must not contain surrounding whitespace.\") : value;\n")
            .Append("}\n");
        if (!type.ContainingNamespace.IsGlobalNamespace) source.Append("}\n");
        return source.ToString();
    }

    private static void RenderIO(StringBuilder source, string modelType, IPropertySymbol[] properties)
    {
        var partialType = "PartialRead";
        while (properties.Any(property => property.Name == partialType)) partialType += "_";
        var mapped = "new string[] { " + string.Join(", ", properties.Select(property => Literal(property.Name))) + " }";
        const string client = "global::Respire.IRespireClient client";
        const string key = "global::Respire.RespireKey key";
        const string token = "global::System.Threading.CancellationToken cancellationToken = default";
        source.Append("    private static readonly global::System.Collections.Generic.Dictionary<string, long> s_fieldTtls = new(global::System.StringComparer.Ordinal) { ")
            .Append(string.Join(", ", properties.Where(property => FieldTtl(property) is not null)
                .Select(property => "{ " + Literal(property.Name) + ", " + FieldTtl(property)!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "L }")))
            .Append(" };\n")
            .Append("    /// <summary>Tracks an explicit key. A null baseline creates a full first write; otherwise the baseline is assumed persisted.</summary>\n")
            .Append("    public static global::Respire.RespireHashChangeTracker<").Append(modelType).Append("> Track(")
            .Append(client).Append(", ").Append(key).Append(", ").Append(modelType).Append("? baseline = null, global::Respire.RespireHashExpiryMode expiryMode = global::Respire.RespireHashExpiryMode.HSetEx)\n")
            .Append("        => new(client, key, ToFields, s_mappedFields, s_fieldTtls, baseline, expiryMode);\n")
            .Append("    /// <summary>Tracks an assumed persisted model at its generated key.</summary>\n")
            .Append("    public static global::Respire.RespireHashChangeTracker<").Append(modelType).Append("> Track(")
            .Append(client).Append(", ").Append(modelType).Append(" baseline, global::Respire.RespireHashExpiryMode expiryMode = global::Respire.RespireHashExpiryMode.HSetEx)\n")
            .Append("        => Track(client, GetKey(baseline), baseline, expiryMode);\n");
        source.Append("    private static readonly string[] s_mappedFields = ").Append(mapped).Append(";\n")
            .Append("    /// <summary>Writes the model at its generated key; null mapped fields are removed.</summary>\n")
            .Append("    public static global::System.Threading.Tasks.ValueTask SetAsync(").Append(client).Append(", ")
            .Append(modelType).Append(" value, ").Append(token).Append(")\n")
            .Append("        => SetAsync(client, GetKey(value), global::Respire.RespireHashExpiryMode.HSetEx, value, cancellationToken);\n")
            .Append("    /// <summary>Writes with an explicit field expiry mode.</summary>\n")
            .Append("    public static global::System.Threading.Tasks.ValueTask SetAsync(").Append(client).Append(", ")
            .Append("global::Respire.RespireHashExpiryMode expiryMode, ").Append(modelType).Append(" value, ").Append(token).Append(")\n")
            .Append("        => SetAsync(client, GetKey(value), expiryMode, value, cancellationToken);\n")
            .Append("    /// <summary>Writes at an explicit key. Multiple command groups are not atomic; unknown fields remain.</summary>\n")
            .Append("    public static global::System.Threading.Tasks.ValueTask SetAsync(").Append(client).Append(", ").Append(key)
            .Append(", ").Append(modelType).Append(" value, ").Append(token).Append(")\n")
            .Append("        => SetAsync(client, key, global::Respire.RespireHashExpiryMode.HSetEx, value, cancellationToken);\n")
            .Append("    /// <summary>Writes at an explicit key with an explicit field expiry mode.</summary>\n")
            .Append("    public static global::System.Threading.Tasks.ValueTask SetAsync(").Append(client).Append(", ").Append(key)
            .Append(", global::Respire.RespireHashExpiryMode expiryMode, ").Append(modelType).Append(" value, ").Append(token).Append(")\n")
            .Append("        => global::Respire.RespireHashModelIO.WriteAsync(client, key, ToFields(value), s_mappedFields, s_fieldTtls, expiryMode, cancellationToken);\n")
            .Append("    /// <summary>Reads a full model with HGETALL; an absent or empty hash returns null.</summary>\n")
            .Append("    public static async global::System.Threading.Tasks.ValueTask<").Append(modelType).Append("?> GetAsync(")
            .Append(client).Append(", ").Append(key).Append(", ").Append(token).Append(")\n    {\n")
            .Append("        var fields = await global::Respire.RespireHashModelIO.ReadAsync(client, key, cancellationToken).ConfigureAwait(false);\n")
            .Append("        return fields.Count == 0 ? null : FromFields(fields);\n    }\n")
            .Append("    /// <summary>Partial model fields. Unselected and selected-but-missing properties remain distinct.</summary>\n")
            .Append("    public sealed class ").Append(partialType).Append("\n    {\n");
        foreach (var property in properties)
        {
            var propertyType = NullableTypeName(property.Type);
            source.Append("        /// <summary>Selection, presence and decoded value of ").Append(property.Name).Append(".</summary>\n")
                .Append("        public global::Respire.RespireHashField<").Append(propertyType).Append("> @")
                .Append(property.Name).Append(" { get; internal set; }\n");
        }
        source.Append("    }\n")
            .Append("    /// <summary>Reads distinct mapped fields with HMGET, without constructing a full model.</summary>\n")
            .Append("    public static async global::System.Threading.Tasks.ValueTask<").Append(partialType).Append("> GetPartialAsync(")
            .Append(client).Append(", ").Append(key).Append(", string[] fields, ").Append(token).Append(")\n    {\n")
            .Append("        var values = await global::Respire.RespireHashModelIO.ReadPartialAsync(client, key, fields, s_mappedFields, cancellationToken).ConfigureAwait(false);\n")
            .Append("        var result = new ").Append(partialType).Append("();\n");
        for (var index = 0; index < properties.Length; index++)
        {
            var property = properties[index];
            var propertyType = NullableTypeName(property.Type);
            var text = "__text" + index;
            source.Append("        if (values.TryGetValue(").Append(Literal(property.Name)).Append(", out var ").Append(text).Append("))\n")
                .Append("            result.@").Append(property.Name).Append(" = new global::Respire.RespireHashField<")
                .Append(propertyType).Append(">(true, ").Append(text).Append(" is not null, ").Append(text)
                .Append(" is null ? default : ").Append(Decode(property.Type, text)).Append(");\n");
        }
        source.Append("        return result;\n    }\n");
    }

    private static string NullableTypeName(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat
        .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier));

    internal static bool SetsRequiredMembers(IMethodSymbol constructor) => constructor.GetAttributes().Any(attribute =>
        attribute.AttributeClass?.ToDisplayString() == "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute");

    private static long? FieldTtl(IPropertySymbol property) => property.GetAttributes()
        .FirstOrDefault(attribute => attribute.AttributeClass?.ToDisplayString() == "Respire.RespireFieldTtlAttribute")
        ?.ConstructorArguments.FirstOrDefault().Value as long?;

    private static string? RenderKey(string template, IPropertySymbol[] properties)
    {
        var parts = new List<string>();
        var text = new StringBuilder();
        for (var index = 0; index < template.Length; index++)
        {
            var character = template[index];
            if (character is not ('{' or '}')) { text.Append(character); continue; }
            if (index + 1 < template.Length && template[index + 1] == character)
            { text.Append(character); index++; continue; }
            if (character == '}') return null;
            var end = template.IndexOf('}', index + 1);
            if (end < 0) return null;
            var name = template.Substring(index + 1, end - index - 1);
            var property = properties.FirstOrDefault(candidate => candidate.Name == name);
            if (property is null || IsNullable(property.Type)) return null;
            if (text.Length != 0) { parts.Add(Literal(text.ToString())); text.Clear(); }
            parts.Add(Encode(property, "value.@" + name));
            index = end;
        }
        if (text.Length != 0) parts.Add(Literal(text.ToString()));
        if (parts.Count == 0) return Literal("");
        return parts.Count == 1 ? parts[0] : "global::System.String.Concat(" + string.Join(", ", parts) + ")";
    }

    private static string Encode(IPropertySymbol property, string value) => ScalarKind(property.Type) switch
    {
        "String" => "RequiredValue(" + value + ", " + Literal(property.Name) + ")",
        "Boolean" => value + " ? \"1\" : \"0\"",
        "Guid" => value + ".ToString(\"D\")",
        "DateTimeOffset" => value + ".ToString(\"O\", global::System.Globalization.CultureInfo.InvariantCulture)",
        "Double" => value + ".ToString(\"R\", global::System.Globalization.CultureInfo.InvariantCulture)",
        _ => value + ".ToString(global::System.Globalization.CultureInfo.InvariantCulture)",
    };

    private static string Decode(ITypeSymbol type, string value)
    {
        var scalar = ScalarKind(type);
        if (scalar == "Double")
            value = "UnpaddedDoubleText(" + value + ")";
        var styles = "global::System.Globalization.NumberStyles.AllowLeadingSign";
        if (scalar is "Double" or "Decimal")
            styles += " | global::System.Globalization.NumberStyles.AllowDecimalPoint | global::System.Globalization.NumberStyles.AllowExponent";
        return scalar switch
        {
            "String" => value,
            "Boolean" => "ParseBoolean(" + value + ")",
            "Guid" => "global::System.Guid.ParseExact(" + value + ", \"D\")",
            "DateTimeOffset" => "global::System.DateTimeOffset.ParseExact(" + value + ", \"O\", global::System.Globalization.CultureInfo.InvariantCulture, global::System.Globalization.DateTimeStyles.None)",
            _ => "global::System." + scalar + ".Parse(" + value + ", " + styles + ", global::System.Globalization.CultureInfo.InvariantCulture)",
        };
    }

    internal static string? ScalarKind(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            type = nullable.TypeArguments[0];
        return type.SpecialType switch
        {
            SpecialType.System_String => "String",
            SpecialType.System_Boolean => "Boolean",
            SpecialType.System_Int32 => "Int32",
            SpecialType.System_Int64 => "Int64",
            SpecialType.System_Double => "Double",
            SpecialType.System_Decimal => "Decimal",
            _ when type.ToDisplayString() == "System.Guid" => "Guid",
            _ when type.ToDisplayString() == "System.DateTimeOffset" => "DateTimeOffset",
            _ => null,
        };
    }

    internal static bool IsNullable(ITypeSymbol type) => type.NullableAnnotation == NullableAnnotation.Annotated
        || type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T };

    internal static string Literal(string value) => SymbolDisplay.FormatLiteral(value, true);

    // Keep only rendered output and location values; never retain a Compilation, symbol or SyntaxTree.
    internal sealed class GeneratedModel(string? hintName, string? source, string? error, string path,
        TextSpan span, LinePositionSpan lineSpan) : IEquatable<GeneratedModel>
    {
        public string? HintName { get; } = hintName;
        public string? Source { get; } = source;
        public string? Error { get; } = error;
        public string Path { get; } = path;
        public TextSpan Span { get; } = span;
        public LinePositionSpan LineSpan { get; } = lineSpan;
        public static GeneratedModel Failed(ISymbol symbol, string error)
        {
            var location = symbol.Locations.First(candidate => candidate.IsInSource);
            return new GeneratedModel(null, null, error, location.SourceTree!.FilePath,
                location.SourceSpan, location.GetLineSpan().Span);
        }
        public bool Equals(GeneratedModel? other) => other is not null && HintName == other.HintName
            && Source == other.Source && Error == other.Error && Path == other.Path
            && Span.Equals(other.Span) && LineSpan.Equals(other.LineSpan);
        public override bool Equals(object? obj) => Equals(obj as GeneratedModel);
        public override int GetHashCode() => (Source?.GetHashCode() ?? 0) ^ (Error?.GetHashCode() ?? 0) ^ Span.GetHashCode();
    }
}
