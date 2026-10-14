using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TUnit.Assertions;
using TUnit.Core;

namespace Respire.Analyzers.Tests;

public class RespireOptionsBuilderGeneratorTests
{
    [Test]
    [Arguments("")]
    [Arguments("public sealed class RespireOptionsBuilder { private Respire.RespireOptions _options = new(); }")]
    [Arguments("public sealed partial class RespireOptionsBuilder { }")]
    public async Task ConsumerWithMatchingAssemblyName_DoesNotGenerateWithoutBuilderContract(string declaration)
    {
        var compilation = CreateCompilation("namespace Respire.DependencyInjection { " + declaration + " }");

        var (result, output) = Generate(compilation);

        await Assert.That(result.GeneratedTrees.Length).IsEqualTo(0);
        await Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)).IsEmpty();
    }

    [Test]
    public async Task ReferencedBuilder_DoesNotTriggerGeneration()
    {
        var referenceCompilation = CreateCompilation("""
            namespace Respire.DependencyInjection;
            public sealed partial class RespireOptionsBuilder
            {
                private Respire.RespireOptions _options = new();
            }
            """).WithAssemblyName("BuilderReference");
        using var stream = new MemoryStream();
        await Assert.That(referenceCompilation.Emit(stream).Success).IsTrue();
        var compilation = CreateCompilation("").AddReferences(MetadataReference.CreateFromImage(stream.ToArray()));

        var (result, output) = Generate(compilation);

        await Assert.That(result.GeneratedTrees.Length).IsEqualTo(0);
        await Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)).IsEmpty();
    }

    [Test]
    public async Task SourceBuilder_GeneratesPropertiesThatCompile()
    {
        var compilation = CreateCompilation("""
            namespace Respire.DependencyInjection;
            public sealed partial class RespireOptionsBuilder
            {
                private Respire.RespireOptions _options = new();
                public Respire.RespireOptions Build() => _options;
            }
            public static class Consumer
            {
                public static Respire.RespireOptions Create() => new RespireOptionsBuilder { Connections = 3 }.Build();
            }
            """);

        var (result, output) = Generate(compilation);

        await Assert.That(result.GeneratedTrees.Length).IsEqualTo(1);
        await Assert.That(output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)).IsEmpty();
    }

    private static CSharpCompilation CreateCompilation(string source) => CSharpCompilation.Create(
        "Respire.DependencyInjection",
        [CSharpSyntaxTree.ParseText(source)],
        GeneratorTestReferences.All,
        // Match the DI project's opt-in to the experimental hedged-read options surface.
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            .WithSpecificDiagnosticOptions(new Dictionary<string, ReportDiagnostic> { ["RSP0003"] = ReportDiagnostic.Suppress }));

    private static (GeneratorDriverRunResult Result, Compilation Output) Generate(CSharpCompilation compilation)
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new RespireOptionsBuilderGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _);
        return (driver.GetRunResult(), output);
    }
}
