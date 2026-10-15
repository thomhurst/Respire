using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Analyzers.Tests;

public class ExperimentalApiTests
{
    // Compile consumer code without the repository's NoWarn opt-in. Test the shipped
    // metadata for both TFMs, including inferred return types and interface entry points.
    [Test]
    [Arguments("net8.0")]
    [Arguments("net10.0")]
    public async Task ConsumersMustOptInToEachFeature(string framework)
    {
        var references = References(framework);
        var cases = new (string Id, string Code)[]
        {
            ("RSP0001", "object M() => typeof(Respire.VectorData.RespireVectorDataFilterKind);"),
            ("RSP0002", "object M() => typeof(Respire.AI.RespireSemanticCacheExtensions);"),
            ("RSP0003", "object M() => new RespireHedgedReadOptions();"),
            ("RSP0003", "object M() => new RespireOptions { HedgedReads = null };"),
            ("RSP0004", "object M() => typeof(RespireHashImportSession);"),
            ("RSP0004", "void M(IRespireHashCommands hashes) { _ = hashes.CreateImportSessionAsync(); }"),
            ("RSP0004", "void M(IRespireHashCommands hashes) { _ = hashes.CreateImportSessionAsync(default(RespireKey)); }"),
            ("RSP0004", "void M(IRespireBatchHashCommands hashes) { _ = hashes.DiscardAllImports(); }"),
            ("RSP0004", "void M(IRespireBatchHashCommands hashes) { _ = hashes.DiscardImport(default); }"),
            ("RSP0004", "void M(IRespireBatchHashCommands hashes) { _ = hashes.PrepareImport(default, default(System.ReadOnlySpan<RespireValue>)); }"),
            ("RSP0004", "void M(IRespireBatchHashCommands hashes) { _ = hashes.Import(default, default, default(System.ReadOnlySpan<RespireValue>)); }"),
            ("RSP0005", "void M(RespireBatch batch) { _ = batch.Execute(default(RespireCommand)); }"),
            ("RSP0005", "void M(RespireTransaction transaction) { _ = transaction.Execute(default(RespireCommand)); }"),
            ("RSP0005", "void M(RespireWatchedTransaction transaction) { _ = transaction.Execute(default(RespireCommand)); }"),
            ("RSP0005", "void M(IRespireCommandQueue queue) { _ = queue.Execute(default(RespireCommand)); }"),
            ("RSP0006", "object M() => typeof(Respire.StackExchangeCompat.RespireStackExchangeExtensions);")
        };

        foreach (var (id, code) in cases)
        {
            var diagnostics = Compile(references, code);
            var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
            await Assert.That(errors.Length).IsGreaterThan(0).Because($"Expected {id} for {framework}: {code}");
            await Assert.That(errors.All(d => d.Id == id)).IsTrue().Because(string.Join("\n", errors.Select(d => d.ToString())));
            await Assert.That(errors.All(d => d.Descriptor.HelpLinkUri ==
                $"https://thomhurst.github.io/Respire/docs/diagnostics/{id}")).IsTrue();

            var optedIn = Compile(references, code, id);
            await Assert.That(optedIn.Any(d => d.Severity == DiagnosticSeverity.Error)).IsFalse()
                .Because(string.Join("\n", optedIn.Select(d => d.ToString())));
        }
    }

    [Test]
    [Arguments("net8.0")]
    [Arguments("net10.0")]
    public async Task StableClientAndQueueApisNeedNoOptIn(string framework)
    {
        var diagnostics = Compile(References(framework), """
            object M(RespireClient client, IRespireCommandQueue queue)
            {
                _ = new RespireOptions();
                _ = queue.Hashes;
                _ = client.Hashes;
                return client.CreateBatch();
            }
            """);
        await Assert.That(diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)).IsFalse();
    }

    [Test]
    [Arguments("net8.0")]
    [Arguments("net10.0")]
    public async Task OptInIsSpecificToTheSelectedFeature(string framework)
    {
        var diagnostics = Compile(References(framework), """
            object M() => typeof(Respire.VectorData.RespireVectorDataFilterKind);
            object N() => new RespireOptions { HedgedReads = null };
            """, "RSP0001");
        var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        await Assert.That(errors.Length).IsEqualTo(1);
        await Assert.That(errors[0].Id).IsEqualTo("RSP0003");
    }

    private static MetadataReference[] References(string framework)
    {
        var platform = GeneratorTestReferences.All
            .Where(reference => !Path.GetFileName(reference.Display)!.StartsWith("Respire", StringComparison.Ordinal));
        string[] packages = ["Respire", "Respire.VectorData", "Respire.AI", "Respire.StackExchangeCompat"];
        return platform.Concat(packages.Select(name => MetadataReference.CreateFromFile(
            Path.Combine(AppContext.BaseDirectory, "local-initialization-audit", framework, name + ".dll")))).ToArray();
    }

    private static Diagnostic[] Compile(MetadataReference[] references, string code, string? suppress = null)
    {
        var source = (suppress is null ? "" : $"#pragma warning disable {suppress}\n")
            + "using Respire; class Consumer { " + code + " }";
        return CSharpCompilation.Create("Consumer", [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)).GetDiagnostics().ToArray();
    }
}
