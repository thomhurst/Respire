using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Analyzers.Tests;

public class PendingResultSuppressorTests
{
    [Test]
    [Arguments("net8.0", false)]
    [Arguments("net8.0", true)]
    [Arguments("net10.0", false)]
    [Arguments("net10.0", true)]
    public async Task QueuedCallsAreSuppressedIncludingWithWarningsAsErrors(string framework, bool warningsAsErrors)
    {
        var compilation = Compile(framework, """
            public async Task Run(RespireClient client, RespireWatchedTransaction watched, IRespireCommandQueue queue)
            {
                await using var batch = client.CreateBatch();
                foreach (var key in new[] { "first", "second" })
                    batch.Expire(key, TimeSpan.FromMinutes(1));
                batch.Sets.Add("index", new RespireValue[] { "id" });
                batch.Get<string>("key");
                queue.Expire("key", TimeSpan.FromMinutes(1));
                queue.Sets.Add("index", new RespireValue[] { "id" });
                await batch.ExecuteAsync();
                await using var transaction = client.CreateTransaction();
                transaction.Expire("key", TimeSpan.FromMinutes(1));
                transaction.Sets.Add("index", new RespireValue[] { "id" });
                watched.Expire("key", TimeSpan.FromMinutes(1));
                transaction.QueueExpiry();
                QueueExtensions.QueueExpiry(transaction);
                await transaction.CommitAsync();
            }
            """, warningsAsErrors);

        // Positive control: these exact consumer calls raise CS4014 without the suppressor.
        var original = compilation.GetDiagnostics();
        await Assert.That(original.Count(d => d.Id == "CS4014")).IsEqualTo(10);
        await Assert.That(original.Where(d => d.Id != "CS4014" && d.Severity >= DiagnosticSeverity.Warning).ToArray()).IsEmpty();

        var diagnostics = await Analyze(compilation);
        await Assert.That(diagnostics.Where(d => !d.IsSuppressed && d.Severity >= DiagnosticSeverity.Warning).ToArray()).IsEmpty();
        await Assert.That(diagnostics.Count(d => d.Id == "CS4014" && d.IsSuppressed)).IsEqualTo(10);
    }

    [Test]
    public async Task RealAsyncOperationsAndUnrelatedAwaitablesStillWarn()
    {
        var compilation = Compile("net10.0", """
            public async Task Run(RespireClient client)
            {
                await using var batch = client.CreateBatch();
                batch.Expire("key", TimeSpan.FromMinutes(1));
                batch.ExecuteAsync();
                await using var transaction = client.CreateTransaction();
                transaction.CommitAsync();
                client.GetStringAsync("key");
                Task.Delay(1);
                OtherPending();
                await Task.CompletedTask;
            }
            private static Task<RespirePending<bool>> OtherPending() => throw new NotImplementedException();
            """);
        var diagnostics = await Analyze(compilation);
        await Assert.That(diagnostics.Count(d => d.Id == "CS4014" && !d.IsSuppressed)).IsEqualTo(5);
        await Assert.That(diagnostics.Count(d => d.Id == "CS4014" && d.IsSuppressed)).IsEqualTo(1);
        await Assert.That(diagnostics.Where(d => !d.IsSuppressed && d.Id != "CS4014" && d.Severity >= DiagnosticSeverity.Warning).ToArray()).IsEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EarlyAwaitStillReportsResp002(bool transaction)
    {
        var compilation = Compile("net10.0", $$"""
            public async Task Run(RespireClient client)
            {
                await using var queue = client.{{(transaction ? "CreateTransaction" : "CreateBatch")}}();
                await queue.Expire("key", TimeSpan.FromMinutes(1));
            }
            """);
        var diagnostics = await Analyze(compilation, new PendingReadBeforeFlushAnalyzer());
        await Assert.That(diagnostics.Count(d => d.Id == "RESP002" && !d.IsSuppressed)).IsEqualTo(1);
        await Assert.That(diagnostics.Where(d => d.Id != "RESP002" && d.Severity >= DiagnosticSeverity.Warning).ToArray()).IsEmpty();
    }

    [Test]
    public async Task PendingRemainsAwaitableAfterExecution()
    {
        var compilation = Compile("net10.0", """
            public async Task Run(RespireClient client)
            {
                await using var batch = client.CreateBatch();
                var pending = batch.Expire("key", TimeSpan.FromMinutes(1));
                await batch.ExecuteAsync();
                bool result = await pending;
                Console.WriteLine(result);
            }
            """);
        var diagnostics = await Analyze(compilation, new PendingReadBeforeFlushAnalyzer());
        await Assert.That(diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning).ToArray()).IsEmpty();
    }

    [Test]
    public async Task SameNamedUnrelatedAwaitableStillWarns()
    {
        var compilation = Compile("net10.0", """
            public sealed class RespirePending<T>
            {
                public System.Runtime.CompilerServices.TaskAwaiter<T> GetAwaiter()
                    => Task.FromResult(default(T)).GetAwaiter();
            }
            private static RespirePending<bool> OtherPending() => new RespirePending<bool>();
            public async Task Run()
            {
                OtherPending();
                await Task.CompletedTask;
            }
            """);
        var diagnostics = await Analyze(compilation);
        await Assert.That(diagnostics.Count(d => d.Id == "CS4014" && !d.IsSuppressed)).IsEqualTo(1);
        await Assert.That(diagnostics.Where(d => d.Id != "CS4014" && d.Severity >= DiagnosticSeverity.Warning).ToArray()).IsEmpty();
    }

    [Test]
    public async Task SuppressionCanBeDisabledExplicitly()
    {
        var compilation = Compile("net10.0", """
            public async Task Run(RespireClient client)
            {
                await using var batch = client.CreateBatch();
                batch.Expire("key", TimeSpan.FromMinutes(1));
                await batch.ExecuteAsync();
            }
            """);
        compilation = compilation.WithOptions(compilation.Options.WithSpecificDiagnosticOptions(
            new Dictionary<string, ReportDiagnostic> { ["RESPSPR001"] = ReportDiagnostic.Suppress }));
        var diagnostics = await Analyze(compilation);
        await Assert.That(diagnostics.Count(d => d.Id == "CS4014" && !d.IsSuppressed)).IsEqualTo(1);
    }

    private static CSharpCompilation Compile(string framework, string members, bool warningsAsErrors = false)
    {
        var references = GeneratorTestReferences.All
            .Where(reference => !Path.GetFileName(reference.Display)!.StartsWith("Respire", StringComparison.Ordinal))
            .Append(MetadataReference.CreateFromFile(Path.Combine(AppContext.BaseDirectory,
                "local-initialization-audit", framework, "Respire.dll")));
        var source = """
            using System;
            using System.Threading.Tasks;
            using Respire;
            public static class QueueExtensions
            {
                public static RespirePending<bool> QueueExpiry(this IRespireCommandQueue queue)
                    => queue.Expire("key", TimeSpan.FromMinutes(1));
            }
            public class Consumer
            {
            """ + members + "}";
        return CSharpCompilation.Create("Consumer", [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                generalDiagnosticOption: warningsAsErrors ? ReportDiagnostic.Error : ReportDiagnostic.Default));
    }

    private static Task<ImmutableArray<Diagnostic>> Analyze(CSharpCompilation compilation, params DiagnosticAnalyzer[] additional)
        => compilation.WithAnalyzers([new PendingResultSuppressor(), .. additional],
            new CompilationWithAnalyzersOptions(new AnalyzerOptions([]), onAnalyzerException: null,
                concurrentAnalysis: true, logAnalyzerExecutionTime: false, reportSuppressedDiagnostics: true))
            .GetAllDiagnosticsAsync();
}
