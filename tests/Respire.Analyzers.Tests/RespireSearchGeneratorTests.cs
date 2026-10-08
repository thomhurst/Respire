using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Respire.Json;
using Respire.Search;
using TUnit.Assertions;
using TUnit.Core;

namespace Respire.Analyzers.Tests;

[NotInParallel]
public class RespireSearchGeneratorTests
{
    [Test]
    [Arguments("GetType")]
    [Arguments("ToString")]
    [Arguments("Equals")]
    [Arguments("GetHashCode")]
    [Arguments("ReferenceEquals")]
    [Arguments("MemberwiseClone")]
    [Arguments("Finalize")]
    public async Task IndexedInheritedMemberNamesCompileWithoutWarnings(string property)
    {
        var modifier = property == "Finalize" ? "" : "new ";
        var (source, diagnostics) = Generate($$"""
            [RespireJson("model:{Id}"), RespireSearch("models", Prefixes = new[] { "model:" })]
            public partial class Model
            {
                public string Id { get; set; } = "42";
                [RespireSearchField(RespireSearchFieldType.Text)]
                public {{modifier}}string {{property}} { get; set; } = "value";
            }
            """);
        await Assert.That(diagnostics).IsEmpty();
        await Assert.That(source).Contains("public " + modifier + "global::Respire.Search.RespireSearchField @" + property);
    }

    [Test]
    [Arguments("RespireHash", "byte[]", "Float32")]
    [Arguments("RespireJson", "float[]", "Float32")]
    [Arguments("RespireJson", "double[]", "Float64")]
    public async Task SchemasAndVectorMappersCompileWithoutReflection(string mapping, string vector, string elementType)
    {
        var (source, diagnostics) = Generate($$"""
            [{{mapping}}("model:{Id}"), RespireSearch("models-v1", Prefixes = new[] { "model:" })]
            public partial record Model(string Id,
                [property: RespireSearchField(RespireSearchFieldType.Text, NoStem = true, Weight = 2)] string Title,
                [property: RespireSearchField(RespireSearchFieldType.Tag, Separator = '|', CaseSensitive = true)] string Category,
                [property: RespireSearchField(RespireSearchFieldType.Numeric, Sortable = true)] int? Age,
                [property: RespireSearchField(RespireSearchFieldType.Vector, Dimensions = 2, VectorType = RespireSearchVectorType.{{elementType}})] {{vector}} Embedding);
            """);
        await Assert.That(diagnostics).IsEmpty();
        await Assert.That(source).Contains("IRespireSearchSchema<global::Model>");
        await Assert.That(source).Contains("Array.AsReadOnly");
        await Assert.That(source.Contains("Reflection")).IsFalse();
    }

    [Test]
    [Arguments("RespireHash(\"m:{Id}\")", "int", "Text", "")]
    [Arguments("RespireHash(\"m:{Id}\")", "string", "Numeric", "")]
    [Arguments("RespireHash(\"m:{Id}\")", "string", "Tag", ", Weight = 2")]
    [Arguments("RespireHash(\"m:{Id}\")", "string", "Text", ", Dimensions = 2")]
    [Arguments("RespireHash(\"m:{Id}\")", "string", "Text", ", Weight = -1")]
    [Arguments("RespireHash(\"m:{Id}\")", "string", "Tag", ", Separator = 'é'")]
    [Arguments("RespireHash(\"m:{Id}\")", "byte[]", "Vector", ", Dimensions = 0")]
    [Arguments("RespireHash(\"m:{Id}\")", "byte[]", "Vector", ", Dimensions = 2, Sortable = true")]
    [Arguments("RespireHash(\"m:{Id}\")", "byte[]", "Vector", ", Dimensions = 2, Algorithm = (RespireSearchVectorAlgorithm)10")]
    [Arguments("RespireJson(\"m:{Id}\")", "double[]", "Vector", ", Dimensions = 2")]
    [Arguments("RespireJson(\"m:{Id}\")", "byte[]", "Vector", ", Dimensions = 2")]
    [Arguments("RespireHash(\"m:{Id}\"), RespireJson(\"m:{Id}\")", "string", "Text", "")]
    public async Task InvalidFieldsProduceSearchDiagnostic(string mapping, string type, string kind, string options)
    {
        var (_, diagnostics) = Generate($$"""
            [{{mapping}}, RespireSearch("models", Prefixes = new[] { "m:" })]
            public partial record Model(string Id,
                [property: RespireSearchField(RespireSearchFieldType.{{kind}}{{options}})] {{type}} Value);
            """);
        await Assert.That(diagnostics.Any(item => item.Id == DiagnosticIds.InvalidGeneratedSearch)).IsTrue();
        await Assert.That(diagnostics.Any(item => item.Id == "CS8785")).IsFalse();
    }

    [Test]
    [Arguments("[RespireSearch(\"models\", Prefixes = new[] { \"m:\" })] public partial record Model(string Id);")]
    [Arguments("[RespireHash(\"m:{Id}\"), RespireSearch(\"models\")] public partial record Model(string Id);")]
    [Arguments("[RespireHash(\"m:{Id}\"), RespireSearch(\"models\", Prefixes = new[] { \"\" })] public partial record Model(string Id);")]
    [Arguments("[RespireHash(\"m:{Id}\"), RespireSearch(\"\", Prefixes = new[] { \"m:\" })] public partial record Model(string Id);")]
    public async Task InvalidModelMetadataProducesSearchDiagnostic(string declaration)
    {
        var (_, diagnostics) = Generate(declaration);
        await Assert.That(diagnostics.Any(item => item.Id == DiagnosticIds.InvalidGeneratedSearch)).IsTrue();
    }

    [Test]
    public async Task IndexedPropertiesRequireSchemaAndGeneratedFieldCollectionAvoidsNameCollisions()
    {
        var (_, invalid) = Generate("""
            [RespireHash("m:{Id}")] public partial record Model(string Id,
                [property: RespireSearchField(RespireSearchFieldType.Text)] string Name);
            """);
        await Assert.That(invalid.Any(item => item.Id == DiagnosticIds.InvalidGeneratedSearch)).IsTrue();
        var (_, valid) = Generate("""
            [RespireHash("m:{Id}"), RespireSearch("models", Prefixes = new[] { "m:" })]
            public partial record Model(string Id,
                [property: RespireSearchField(RespireSearchFieldType.Text)] string Fields,
                [property: RespireSearchField(RespireSearchFieldType.Text)] string SchemaFields);
            """);
        await Assert.That(valid).IsEmpty();
    }

    [Test]
    public async Task DuplicateAliasesAreRejectedAndJsonMappedNamesAreEscaped()
    {
        var (_, invalid) = Generate("""
            [RespireJson("m:{Id}"), RespireSearch("models", Prefixes = new[] { "m:" })]
            public partial record Model(string Id,
                [property: RespireSearchField(RespireSearchFieldType.Text, Alias = "same")] string Name,
                [property: RespireSearchField(RespireSearchFieldType.Tag, Alias = "same")] string Category);
            """);
        await Assert.That(invalid.Any(item => item.Id == DiagnosticIds.InvalidGeneratedSearch)).IsTrue();
        var (source, valid) = Generate("""
            namespace @event {
                [RespireJson("m:{Id}"), RespireSearch("models", Prefixes = new[] { "m:" })]
                public partial record @class(string Id,
                    [property: System.Text.Json.Serialization.JsonPropertyName("a.\"\\姓名")]
                    [property: RespireSearchField(RespireSearchFieldType.Text)] string @event);
            }
            """);
        await Assert.That(valid).IsEmpty();
        await Assert.That(source).Contains("Fields.@event");
    }

    private static (string Source, Diagnostic[] Diagnostics) Generate(string declaration)
    {
        var options = new CSharpParseOptions(LanguageVersion.CSharp12);
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(RespireSearchAttribute).Assembly.Location).Append(typeof(RespireJsonAttribute).Assembly.Location)
            .Append(typeof(RespireHashAttribute).Assembly.Location).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("SearchConsumer", [CSharpSyntaxTree.ParseText(
            "#nullable enable\nusing Respire; using Respire.Json; using Respire.Search;\n" + declaration, options)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new RespireHashGenerator().AsSourceGenerator(),
            new RespireJsonGenerator().AsSourceGenerator(), new RespireSearchGenerator().AsSourceGenerator()], parseOptions: options);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        return (string.Join("\n", driver.GetRunResult().GeneratedTrees.Select(tree => tree.ToString())),
            diagnostics.Concat(output.GetDiagnostics()).Where(item => item.Severity >= DiagnosticSeverity.Warning).ToArray());
    }
}
