using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.NetCore.Analyzers.Tasks;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Analyzers.Tests;

/// <summary>
/// Applies CA2012 to every production invocation of the three pooled routing methods.
/// CA2012 is intraprocedural: forwarding transfers responsibility to the recipient;
/// this does not prove arbitrary callees, field aliases or external callers consume once.
/// Method-group conversion is rejected for review because CA2012 cannot track its target.
/// </summary>
public class PooledRouterConsumptionTests
{
    private static readonly string[] ProtectedMethods =
    ["GetConnectionAsync", "SendReadFromAsync", "SendBulkStreamViaReadRouterAsync"];

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EveryProductionPooledRouterCallSatisfiesCa2012(bool net10)
    {
        var configuration = TestInspectionSource.ReadSourceConfigurations()
            .Single(item => item.Framework == (net10 ? "net10.0" : "net8.0"));
        var trees = TestInspectionSource.ReadLibrarySources()
            .Where(source => source.Path.StartsWith("LibrarySource/Respire/", StringComparison.Ordinal))
            .Select(source => CSharpSyntaxTree.ParseText(source.Text,
                new CSharpParseOptions(LanguageVersion.Preview, preprocessorSymbols: configuration.Symbols), source.Path))
            .Append(CSharpSyntaxTree.ParseText("global using System; global using System.Collections.Generic; global using System.Linq; global using System.Threading; global using System.Threading.Tasks;",
                new CSharpParseOptions(LanguageVersion.Preview)))
            .ToArray();
        var compilation = TestInspectionSource.CreateCompilation(trees);
        var calls = FindProtectedCalls(compilation);
        foreach (var name in ProtectedMethods)
            await Assert.That(calls.Any(call => call.ToString().Contains(name, StringComparison.Ordinal))).IsTrue();
        var violations = await FindViolationsAsync(compilation, calls);
        await Assert.That(violations).IsEmpty();
    }

    private static InvocationExpressionSyntax[] FindProtectedCalls(CSharpCompilation compilation)
    {
        var calls = new List<InvocationExpressionSyntax>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var call in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = call.Expression switch
                {
                    MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                    SimpleNameSyntax simple => simple.Identifier.ValueText,
                    _ => ""
                };
                if (!ProtectedMethods.Contains(name)) continue;
                // Fail closed on unresolved calls; an unbound new caller must not escape analysis.
                if (model.GetOperation(call) is not IInvocationOperation invocation)
                    throw new InvalidOperationException($"Unresolved routing call: {tree.FilePath}: {call}");
                if (IsProtected(invocation.TargetMethod)) calls.Add(call);
            }
            foreach (var reference in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
            {
                if (!ProtectedMethods.Contains(reference.Identifier.ValueText)
                    || model.GetSymbolInfo(reference).Symbol is not IMethodSymbol method || !IsProtected(method)) continue;
                if (reference.Ancestors().OfType<InvocationExpressionSyntax>()
                    .Any(call => call.Expression.Span.Contains(reference.Span) || model.GetOperation(call) is INameOfOperation)) continue;
                throw new InvalidOperationException($"Unsupported pooled routing method reference: {tree.FilePath}: {reference}");
            }
        }
        return calls.ToArray();
    }

    private static bool IsProtected(IMethodSymbol method)
        => method.ContainingType.ToDisplayString() switch
        {
            "Respire.Internal.ReadEndpointRouter" => method.Name == "GetConnectionAsync",
            "Respire.RespireClient" => method.Name is "SendReadFromAsync" or "SendBulkStreamViaReadRouterAsync",
            _ => false
        };

    private static async Task<string[]> FindViolationsAsync(CSharpCompilation compilation, InvocationExpressionSyntax[] calls)
    {
        var diagnostics = await AnalyzeAsync(compilation);
        // A configured-await diagnostic can cover ConfigureAwait as well as the original call.
        return diagnostics.Where(diagnostic => calls.Any(call =>
            diagnostic.Location.SourceTree == call.SyntaxTree && call.Span.IntersectsWith(diagnostic.Location.SourceSpan)))
            .Select(diagnostic => diagnostic.ToString()).ToArray();
    }

    [Test]
    [Arguments("await Produce();", false)]
    [Arguments("await Produce().ConfigureAwait(false);", false)]
    [Arguments("var pending = Produce(); await pending;", false)]
    [Arguments("var pending = Produce(); if (!pending.IsCompletedSuccessfully) throw new Exception(); pending.GetAwaiter().GetResult();", false)]
    [Arguments("var pending = Produce(); var task = pending.AsTask(); await task; await task;", false)]
    [Arguments("return Produce();", false)]
    [Arguments("var pending = Produce(); return pending;", false)]
    [Arguments("return Forward(Produce());", false)]
    [Arguments("var pending = Produce(); return flag ? Forward(pending) : pending;", false)]
    [Arguments("var pending = Produce(); await pending; await pending;", true)]
    [Arguments("var pending = Produce(); await pending.ConfigureAwait(false); await pending;", true)]
    [Arguments("var pending = Produce(); await pending.AsTask(); await pending;", true)]
    [Arguments("#pragma warning disable CA2012\nvar pending = Produce(); await pending; await pending;\n#pragma warning restore CA2012", true)]
    [Arguments("Produce();", true)]
    [Arguments("var pending = Produce(); pending.GetAwaiter().GetResult();", true)]
    public async Task ControlsDistinguishSingleConsumptionFromReuse(string body, bool invalid)
    {
        var forwarding = body.Contains("return", StringComparison.Ordinal);
        foreach (var name in ProtectedMethods)
        {
            var owner = name == "GetConnectionAsync" ? "namespace Respire.Internal; class ReadEndpointRouter" : "namespace Respire; partial class RespireClient";
            var source = "using System; using System.Threading.Tasks; " + owner + " { "
                + "static ValueTask<int> " + name + "() => default; static ValueTask<int> Forward(ValueTask<int> value) => value; "
                + (forwarding ? "ValueTask<int> NewCaller(bool flag) { " : "async Task NewCaller(bool flag) { ")
                + Environment.NewLine + body.Replace("Produce()", name + "()", StringComparison.Ordinal) + Environment.NewLine + " } }";
            var compilation = TestInspectionSource.CreateCompilation([CSharpSyntaxTree.ParseText(source)]);
            await Assert.That(compilation.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error).ToArray()).IsEmpty();
            var calls = FindProtectedCalls(compilation);
            await Assert.That(calls.Length).IsEqualTo(1);
            var violations = await FindViolationsAsync(compilation, calls);
            await Assert.That(violations.Length > 0).IsEqualTo(invalid);
        }
    }

    [Test]
    public async Task MethodGroupCannotHideANewCallerFromCa2012()
    {
        const string source = """
            using System;
            using System.Threading.Tasks;
            namespace Respire.Internal;
            class ReadEndpointRouter
            {
                ValueTask<int> GetConnectionAsync() => default;
                Func<ValueTask<int>> HiddenCaller() => GetConnectionAsync;
            }
            """;
        var compilation = TestInspectionSource.CreateCompilation([CSharpSyntaxTree.ParseText(source)]);
        await Assert.That(compilation.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error).ToArray()).IsEmpty();
        var rejected = false;
        try { FindProtectedCalls(compilation); }
        catch (InvalidOperationException error) when (error.Message.StartsWith("Unsupported pooled routing method reference:", StringComparison.Ordinal))
        {
            rejected = true;
        }
        await Assert.That(rejected).IsTrue();
    }

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(CSharpCompilation compilation)
    {
        // Explicit severity also makes suggestion-level diagnostics execute in this harness.
        compilation = compilation.WithOptions(compilation.Options.WithSpecificDiagnosticOptions(
            compilation.Options.SpecificDiagnosticOptions.SetItem("CA2012", ReportDiagnostic.Warn)));
        var options = new CompilationWithAnalyzersOptions(new AnalyzerOptions([]), onAnalyzerException: null,
            concurrentAnalysis: true, logAnalyzerExecutionTime: false, reportSuppressedDiagnostics: true);
        var diagnostics = await compilation.WithAnalyzers([new UseValueTasksCorrectlyAnalyzer()], options).GetAnalyzerDiagnosticsAsync();
        var failures = diagnostics.Where(item => item.Id == "AD0001").ToArray();
        if (failures.Length != 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures.Select(item => item.ToString())));
        return diagnostics.Where(item => item.Id == "CA2012").ToImmutableArray();
    }
}
