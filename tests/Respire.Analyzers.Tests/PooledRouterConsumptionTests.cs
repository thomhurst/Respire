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
/// Lifted conditional-access results may be returned directly; storage requires review
/// because CA2012 does not follow aliases extracted from Nullable&lt;ValueTask&lt;T&gt;&gt;.
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
            await Assert.That(calls.Any(call => compilation.GetSemanticModel(call.SyntaxTree).GetSymbolInfo(call).Symbol
                is IMethodSymbol method && method.Name == name)).IsTrue();
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
                    MemberBindingExpressionSyntax member => member.Name.Identifier.ValueText,
                    SimpleNameSyntax simple => simple.Identifier.ValueText,
                    _ => ""
                };
                if (!ProtectedMethods.Contains(name)) continue;
                // Fail closed on unresolved calls; an unbound new caller must not escape analysis.
                if (model.GetOperation(call) is not IInvocationOperation invocation)
                    throw new InvalidOperationException($"Unresolved routing call: {tree.FilePath}: {call}");
                if (!IsProtected(invocation.TargetMethod)) continue;
                if (call.Expression is MemberBindingExpressionSyntax && !IsDirectConditionalReturn(call))
                    throw new InvalidOperationException($"Unsupported pooled routing conditional result: {tree.FilePath}: {call}");
                calls.Add(call);
            }
            foreach (var reference in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
            {
                if (!ProtectedMethods.Contains(reference.Identifier.ValueText)
                    || model.GetSymbolInfo(reference).Symbol is not IMethodSymbol method || !IsProtected(method)) continue;
                if (reference.Ancestors().OfType<InvocationExpressionSyntax>()
                    .Any(call => model.GetOperation(call) is INameOfOperation
                        || call.Expression.Span.Contains(reference.Span)
                            && model.GetOperation(call) is IInvocationOperation invocation
                            && SymbolEqualityComparer.Default.Equals(invocation.TargetMethod.OriginalDefinition, method.OriginalDefinition))) continue;
                throw new InvalidOperationException($"Unsupported pooled routing method reference: {tree.FilePath}: {reference}");
            }
        }
        return calls.ToArray();
    }

    private static bool IsDirectConditionalReturn(InvocationExpressionSyntax call)
    {
        SyntaxNode? result = call.Parent as ConditionalAccessExpressionSyntax;
        if (result is null) return false;
        while (result.Parent is ParenthesizedExpressionSyntax parentheses) result = parentheses;
        return result.Parent is ReturnStatementSyntax or ArrowExpressionClauseSyntax;
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
    [Arguments("await Produce();", false, false)]
    [Arguments("await Produce().ConfigureAwait(false);", false, false)]
    [Arguments("var pending = Produce(); await pending;", false, false)]
    [Arguments("var pending = Produce(); if (!pending.IsCompletedSuccessfully) throw new Exception(); pending.GetAwaiter().GetResult();", false, false)]
    [Arguments("var pending = Produce(); var task = pending.AsTask(); await task; await task;", false, false)]
    [Arguments("return Produce();", false, true)]
    [Arguments("var pending = Produce(); return pending;", false, true)]
    [Arguments("return Forward(Produce());", false, true)]
    [Arguments("var pending = Produce(); return flag ? Forward(pending) : pending;", false, true)]
    [Arguments("var pending = Produce(); await pending; await pending;", true, false)]
    [Arguments("var pending = Produce(); await pending.ConfigureAwait(false); await pending;", true, false)]
    [Arguments("var pending = Produce(); await pending.AsTask(); await pending;", true, false)]
    [Arguments("#pragma warning disable CA2012\nvar pending = Produce(); await pending; await pending;\n#pragma warning restore CA2012", true, false)]
    [Arguments("Produce();", true, false)]
    [Arguments("var pending = Produce(); pending.GetAwaiter().GetResult();", true, false)]
    public async Task ControlsDistinguishSingleConsumptionFromReuse(string body, bool invalid, bool forwarding)
    {
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
    [Arguments("Func<ValueTask<int>> HiddenCaller() => GetConnectionAsync;")]
    [Arguments("ValueTask<int> HiddenCaller() => ((Func<ValueTask<int>>)GetConnectionAsync)();")]
    public async Task UnsupportedReferenceCannotHideANewCallerFromCa2012(string declaration)
    {
        const string source = """
            using System;
            using System.Threading.Tasks;
            namespace Respire.Internal;
            class ReadEndpointRouter
            {
                ValueTask<int> GetConnectionAsync() => default;
                DECLARATION
            }
            """;
        var compilation = TestInspectionSource.CreateCompilation([CSharpSyntaxTree.ParseText(source.Replace("DECLARATION", declaration, StringComparison.Ordinal))]);
        await Assert.That(compilation.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error).ToArray()).IsEmpty();
        var rejected = false;
        try { FindProtectedCalls(compilation); }
        catch (InvalidOperationException error) when (error.Message.StartsWith("Unsupported pooled routing", StringComparison.Ordinal))
        {
            rejected = true;
        }
        await Assert.That(rejected).IsTrue();
    }

    [Test]
    [Arguments("ValueTask<int>? NewCaller(OWNER? router) => router?.Produce();", false)]
    [Arguments("async Task NewCaller(OWNER? router) { var pending = router?.Produce(); if (pending is {} value) { await value; await value; } }", true)]
    public async Task ConditionalAccessIsDiscoveredAndRequiresReviewForLiftedAliases(string declaration, bool requiresReview)
    {
        foreach (var name in ProtectedMethods)
        {
            var owner = name == "GetConnectionAsync" ? "ReadEndpointRouter" : "RespireClient";
            var space = name == "GetConnectionAsync" ? "Respire.Internal" : "Respire";
            var source = "using System.Threading.Tasks; namespace " + space + "; class " + owner
                + " { ValueTask<int> " + name + "() => default; "
                + declaration.Replace("OWNER", owner, StringComparison.Ordinal).Replace("Produce()", name + "()", StringComparison.Ordinal) + " }";
            var compilation = TestInspectionSource.CreateCompilation([CSharpSyntaxTree.ParseText(source)]);
            await Assert.That(compilation.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error).ToArray()).IsEmpty();
            InvocationExpressionSyntax[] calls;
            try { calls = FindProtectedCalls(compilation); }
            catch (InvalidOperationException error) when (error.Message.StartsWith("Unsupported pooled routing conditional result:", StringComparison.Ordinal))
            {
                await Assert.That(requiresReview).IsTrue();
                continue;
            }
            await Assert.That(requiresReview).IsFalse();
            await Assert.That(calls.Length).IsEqualTo(1);
            var violations = await FindViolationsAsync(compilation, calls);
            await Assert.That(violations).IsEmpty();
        }
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
