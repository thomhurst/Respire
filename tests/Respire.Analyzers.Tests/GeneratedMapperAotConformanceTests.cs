using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Respire.Json;
using Respire.Search;
using TUnit.Assertions;
using TUnit.Core;

namespace Respire.Analyzers.Tests;

[NotInParallel]
public class GeneratedMapperAotConformanceTests
{
    [Test]
    public async Task GeneratedScalarAndVectorMappersNeverUseReflectionOrDynamicSerialization()
    {
        var compilation = CreateCompilation("""
            [RespireHash("hash:{Id}")]
            public partial record HashModel(string Id, [property: RespireFieldTtl(500)] string? Name,
                bool? Flag, int? Count, long? Total, double? Score, decimal? Price, Guid? Token, DateTimeOffset? Created);
            [RespireJson("json:{Id}")]
            public partial record JsonModel(string Id, string? Name,
                bool? Flag, int? Count, long? Total, double? Score, decimal? Price, Guid? Token, DateTimeOffset? Created);
            [RespireHash("hash-vector:{Id}"), RespireSearch("hashes", Prefixes = new[] { "hash-vector:" })]
            public partial record HashVector(string Id,
                [property: RespireSearchField(RespireSearchFieldType.Vector, Dimensions = 2)] byte[] Embedding);
            [RespireJson("json-vector:{Id}"), RespireSearch("json", Prefixes = new[] { "json-vector:" })]
            public partial record JsonVector(string Id,
                [property: RespireSearchField(RespireSearchFieldType.Vector, Dimensions = 2)] float[] Embedding);
            """);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new RespireHashGenerator().AsSourceGenerator(), new RespireJsonGenerator().AsSourceGenerator(),
                new RespireSearchGenerator().AsSourceGenerator()], parseOptions: ParseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        await Assert.That(diagnostics.Concat(output.GetDiagnostics())
            .Where(item => item.Severity >= DiagnosticSeverity.Warning).ToArray()).IsEmpty();
        var generated = driver.GetRunResult().GeneratedTrees;
        await Assert.That(generated.Length).IsEqualTo(6);
        await Assert.That(generated.SelectMany(tree => ForbiddenCalls(output, tree)).ToArray()).IsEmpty();
    }

    [Test]
    [Arguments("Activator.CreateInstance(typeof(object))")]
    [Arguments("typeof(object).GetProperties()")]
    [Arguments("System.Text.Json.JsonSerializer.Serialize(new object())")]
    [Arguments("new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()")]
    [Arguments("((dynamic)new object()).ToString()")]
    public async Task ConformanceScannerRejectsForbiddenPositiveControls(string expression)
    {
        var compilation = CreateCompilation("public class Control { public object? Run() => " + expression + "; }");
        await Assert.That(ForbiddenCalls(compilation, compilation.SyntaxTrees.Single()).Any()).IsTrue();
    }

    private static IEnumerable<string> ForbiddenCalls(Compilation compilation, SyntaxTree tree)
    {
        var semantic = compilation.GetSemanticModel(tree);
        foreach (var node in tree.GetRoot().DescendantNodes())
        {
            if (node is ExpressionSyntax expression && semantic.GetTypeInfo(expression).Type?.TypeKind == TypeKind.Dynamic)
                yield return "Dynamic binding: " + node;
            if (node is not (InvocationExpressionSyntax or ObjectCreationExpressionSyntax)) continue;
            if (semantic.GetSymbolInfo(node).Symbol is not IMethodSymbol method) continue;
            var type = method.ContainingType.ToDisplayString();
            if (method.ContainingNamespace.ToDisplayString().StartsWith("System.Reflection", StringComparison.Ordinal)
                || type is "System.Type" or "System.Activator" or "System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver"
                || method.GetAttributes().Any(attribute => attribute.AttributeClass?.Name
                    is "RequiresDynamicCodeAttribute" or "RequiresUnreferencedCodeAttribute"))
                yield return "Reflection or dynamic code: " + method.ToDisplayString();
            if (type == "System.Text.Json.JsonSerializer"
                && !method.Parameters.Any(parameter => parameter.Type.ToDisplayString()
                    .StartsWith("System.Text.Json.Serialization.Metadata.JsonTypeInfo", StringComparison.Ordinal)))
                yield return "JSON serialization without explicit metadata: " + method.ToDisplayString();
        }
    }

    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.CSharp12);

    private static CSharpCompilation CreateCompilation(string source)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(RespireHashAttribute).Assembly.Location).Append(typeof(RespireJsonAttribute).Assembly.Location)
            .Append(typeof(RespireSearchAttribute).Assembly.Location).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path));
        return CSharpCompilation.Create("MapperConformance", [CSharpSyntaxTree.ParseText(
            "#nullable enable\nusing System; using Respire; using Respire.Json; using Respire.Search;\n" + source, ParseOptions)],
            references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
    }
}
