using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TUnit.Assertions;
using TUnit.Core;

namespace Respire.Analyzers.Tests;

[NotInParallel]
public class RespireCommandGeneratorTests
{
    private const string Preamble = "#nullable enable\nusing Respire; using System; using System.Threading; using System.Threading.Tasks;\n";

    [Test]
    [Arguments("ValueTask<string?> Get(RespireKey key, string path = \"$\", CancellationToken token = default);")]
    [Arguments("Task<long> Get(byte[] key, params RespireValue[] args);")]
    [Arguments("ValueTask<byte[]?[]> Get(string key, long count = 3, double factor = double.NaN);")]
    [Arguments("ValueTask<long?[]?> Get(string key, RespireCommandFlags flags = RespireCommandFlags.NoRedirect);")]
    [Arguments("Task<RespireResult> Get(RespireKey[] keys, string[] options);")]
    [Arguments("ValueTask Get(string __client, int __command0, int __arguments, int __position, int __result);")]
    [Arguments("ValueTask<int> @event(ReadOnlyMemory<byte> bytes, decimal amount = 3.25M);")]
    [Arguments("Task<bool[]> Get(Guid id, DateTimeOffset time, TimeSpan duration, char letter = '\\n');")]
    [Arguments("ValueTask<double?[]> Get(double[] values, bool enabled = true);")]
    [Arguments("ValueTask<string> ToString();")]
    public async Task SupportedDeclarationsCompile(string method)
    {
        var (generated, diagnostics) = Generate(Preamble + "namespace Demo { [RespireCommands] public interface IModule { [RespireCommand(\"my.module\")] " + method + " } }");
        await Assert.That(diagnostics).IsEmpty();
        await Assert.That(generated).Contains("RespireCommand.Create(\"my.module\")");
        await Assert.That(generated.Contains(".As<")).IsFalse();
        await Assert.That(generated.Contains("Reflection")).IsFalse();
    }

    [Test]
    [Arguments("[RespireCommands] public interface IModule<T> { }")]
    [Arguments("public class Outer { [RespireCommands] public interface IModule { } }")]
    [Arguments("[RespireCommands] public interface IModule : IDisposable { }")]
    [Arguments("[RespireCommands] public interface IModule { int Count { get; } }")]
    [Arguments("[RespireCommands] public interface IModule { ValueTask<string> Get(string key); }")]
    [Arguments("[RespireCommands] public interface IModule { [RespireCommand(\"AUTH user\")] ValueTask Get(); }")]
    [Arguments("[RespireCommands] public interface IModule { [RespireCommand(\"\")] ValueTask Get(); }")]
    [Arguments("[RespireCommands] public interface IModule { [RespireCommand(\"\\u975eASCII\")] ValueTask Get(); }")]
    [Arguments("[RespireCommands] public interface IModule { [RespireCommand(\"X.GET\")] int Get(); }")]
    [Arguments("[RespireCommands] public interface IModule { [RespireCommand(\"X.GET\")] ValueTask<object> Get(); }")]
    [Arguments("[RespireCommands] public interface IModule { [RespireCommand(\"X.GET\")] ValueTask<RespireResult[]> Get(); }")]
    [Arguments("[RespireCommands] public interface IModule { [RespireCommand(\"X.GET\")] ValueTask Get<T>(); }")]
    [Arguments("[RespireCommands] public interface IModule { [RespireCommand(\"X.GET\")] ValueTask Get(ref int value); }")]
    [Arguments("[RespireCommands] public interface IModule { [RespireCommand(\"X.GET\")] ValueTask Get(object value); }")]
    [Arguments("[RespireCommands] public interface IModule { [RespireCommand(\"X.GET\")] ValueTask Get(CancellationToken a, CancellationToken b); }")]
    [Arguments("[RespireCommands] public interface IModule { [RespireCommand(\"X.GET\")] ValueTask Get(RespireCommandFlags a, RespireCommandFlags b); }")]
    [Arguments("[RespireCommands] public interface IModule { [RespireCommand(\"X.GET\")] static abstract ValueTask Get(); }")]
    [Arguments("[RespireCommands] public interface IModule { [RespireCommand(\"X.GET\")] ValueTask Get() => default; }")]
    [Arguments("[RespireCommands] public interface IModule { } public class IModuleImplementation { }")]
    public async Task InvalidDeclarationsHaveActionableDiagnostic(string declaration)
    {
        var (generated, diagnostics) = Generate(Preamble + declaration);
        await Assert.That(diagnostics.Any(diagnostic => diagnostic.Id == "RESP003")).IsTrue();
        await Assert.That(diagnostics.Any(diagnostic => diagnostic.Id == "CS8785")).IsFalse();
        await Assert.That(generated).IsEqualTo("");
    }

    [Test]
    public async Task NamespacesOverloadsAndKeywordIdentifiersCompile()
    {
        var (_, diagnostics) = Generate(Preamble + """
            namespace @event {
                [RespireCommands] public partial interface IModule {
                    [RespireCommand("X.GET")] ValueTask<int> Get(string key);
                }
                public partial interface IModule {
                    [RespireCommand("X.GET")] ValueTask<int> Get(string key, int index);
                }
            }
            namespace Other {
                [RespireCommands] internal interface IModule {
                    [RespireCommand("X.GET")] ValueTask<string> __client(string __command0);
                }
            }
            """);
        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task NullableObliviousDeclarationsCompileWithoutWarnings()
    {
        var (generated, diagnostics) = Generate("""
            #nullable disable
            using Respire; using System.Threading.Tasks;
            namespace Demo {
                [RespireCommands] public interface IModule {
                    [RespireCommand("X.GET")] Task<string> Text(string key);
                    [RespireCommand("X.GET")] ValueTask<string[]> Texts(string[] keys);
                    [RespireCommand("X.GET")] ValueTask<byte[][]> Blobs();
                    [RespireCommand("X.GET")] ValueTask<long> Count();
                }
            }
            """);
        await Assert.That(diagnostics).IsEmpty();
        await Assert.That(generated).Contains("return null!;");
        await Assert.That(generated.Contains("The command returned null")).IsTrue();
    }

    [Test]
    public async Task InvalidDeclarationDiagnosticLinksToGuide()
    {
        var (_, diagnostics) = Generate(Preamble + "[RespireCommands] public interface IModule { ValueTask<string> Get(string key); }");
        var diagnostic = diagnostics.Single(candidate => candidate.Id == DiagnosticIds.InvalidGeneratedCommand);
        await Assert.That(diagnostic.Descriptor.HelpLinkUri).EndsWith("/guides/generated-commands");
    }

    [Test]
    [Arguments("[RespireCommand(\"X.GET\")] ValueTask<int> Get(string key);")]
    [Arguments("[RespireCommand(\"X.GET\")] int Get(string key);")]
    public async Task UnrelatedEditsReuseCachedModelsWithoutRetainingSymbols(string method)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.CSharp12);
        var compilation = CreateCompilation(Preamble + "namespace Demo { [RespireCommands] public interface IModule { " + method + " } }", parseOptions);
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new RespireCommandGenerator().AsSourceGenerator()],
            parseOptions: parseOptions, driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
        driver = driver.RunGenerators(compilation);
        var first = driver.GetRunResult().Results.Single();
        await Assert.That(first.TrackedSteps["RespireCommandInterfaces"]
            .SelectMany(step => step.Outputs).Any(output => output.Value is ISymbol or Compilation)).IsFalse();

        driver = driver.RunGenerators(compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("class Unrelated { }", parseOptions)));
        var second = driver.GetRunResult().Results.Single();
        await Assert.That(second.TrackedSteps["RespireCommandInterfaces"].SelectMany(step => step.Outputs)
            .All(output => output.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged)).IsTrue();
        await Assert.That(second.TrackedOutputSteps.SelectMany(step => step.Value).SelectMany(step => step.Outputs)
            .All(output => output.Reason == IncrementalStepRunReason.Cached)).IsTrue();
    }

    private static CSharpCompilation CreateCompilation(string source, CSharpParseOptions parseOptions)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Append(typeof(RespireCommand).Assembly.Location).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path));
        return CSharpCompilation.Create("GeneratedConsumer", [CSharpSyntaxTree.ParseText(source, parseOptions)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    }

    private static (string Generated, Diagnostic[] Diagnostics) Generate(string source)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.CSharp12);
        var compilation = CreateCompilation(source, parseOptions);
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new RespireCommandGenerator().AsSourceGenerator()], parseOptions: parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics);
        return (string.Join("\n", driver.GetRunResult().GeneratedTrees.Select(tree => tree.ToString())),
            generatorDiagnostics.Concat(output.GetDiagnostics()).Where(diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning).ToArray());
    }
}
