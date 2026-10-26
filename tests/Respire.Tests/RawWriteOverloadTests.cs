using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class RawWriteOverloadTests
{
    [Test]
    [Arguments("\"{\\\"a\\\":1}\"")]
    [Arguments("new byte[] { 0, 255, 13, 10 }")]
    [Arguments("new ReadOnlyMemory<byte>(new byte[] { 0, 255 })")]
    [Arguments("new Memory<byte>(new byte[] { 0, 255 })")]
    [Arguments("new ArraySegment<byte>(new byte[] { 0, 255 })")]
    public async Task InferredRawInputs_SelectUnannotatedProtocolOverloads(string value)
    {
        // Arrays reserve collection expressions for consecutive slots. Wrap byte[] as memory for one binary slot.
        var (compilation, calls) = CompileCalls(value,
            arrayValue: value.StartsWith("new byte[]", StringComparison.Ordinal) ? "(ReadOnlyMemory<byte>)" + value : value);
        await Assert.That(compilation.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error).ToArray()).IsEmpty();
        var semantic = compilation.GetSemanticModel(compilation.SyntaxTrees.Single());
        foreach (var call in calls)
        {
            var method = (IMethodSymbol?)semantic.GetSymbolInfo(call).Symbol;
            await Assert.That(method).IsNotNull();
            await Assert.That(method!.IsGenericMethod).IsFalse().Because(call.ToString());
            await Assert.That(method.GetAttributes().Any(attribute => attribute.AttributeClass?.Name
                is "RequiresUnreferencedCodeAttribute" or "RequiresDynamicCodeAttribute")).IsFalse().Because(call.ToString());
        }
    }

    [Test]
    [Arguments("new object()")]
    [Arguments("\"text\"", "<string>")]
    public async Task ObjectAndExplicitGenericInputs_KeepSerializerWarnings(string value, string typeArgument = "")
    {
        var (compilation, calls) = CompileCalls(value, typeArgument);
        await Assert.That(compilation.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error).ToArray()).IsEmpty();
        var semantic = compilation.GetSemanticModel(compilation.SyntaxTrees.Single());
        foreach (var call in calls)
        {
            var method = (IMethodSymbol?)semantic.GetSymbolInfo(call).Symbol;
            await Assert.That(method).IsNotNull();
            await Assert.That(method!.IsGenericMethod).IsTrue().Because(call.ToString());
            await Assert.That(method.GetAttributes().Count(attribute => attribute.AttributeClass?.Name
                is "RequiresUnreferencedCodeAttribute" or "RequiresDynamicCodeAttribute")).IsEqualTo(2);
        }
    }

    [Test]
    [Arguments(LanguageVersion.CSharp12)]
    [Arguments(LanguageVersion.Preview)]
    public async Task NumericArrayCollectionExpressions_KeepMultipleSlots(LanguageVersion languageVersion)
    {
        const string source = """
            using System.Threading;
            using Respire;
            public class Writes
            {
                public void Run(IRespireClient client, IRespireCommandQueue queue, CancellationToken cancellationToken)
                {
                    client.Arrays.SetAsync("k", 0UL, [1, 2]);
                    client.Arrays.SetAsync("k", 0UL, [1, 2], cancellationToken);
                    queue.Arrays.Set("k", 0UL, [1, 2]);
                }
            }
            """;
        var (compilation, calls) = CompileSource(source, languageVersion);
        await Assert.That(compilation.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error).ToArray()).IsEmpty();
        var semantic = compilation.GetSemanticModel(compilation.SyntaxTrees.Single());
        foreach (var call in calls)
        {
            var method = (IMethodSymbol?)semantic.GetSymbolInfo(call).Symbol;
            await Assert.That(method).IsNotNull();
            var collectionType = method!.Parameters[2].Type;
            var elementType = collectionType is IArrayTypeSymbol array
                ? array.ElementType
                : ((INamedTypeSymbol)collectionType).TypeArguments.Single();
            await Assert.That(elementType.ToDisplayString()).IsEqualTo("Respire.RespireValue").Because(call.ToString());
        }
    }

    [Test]
    public async Task ArrayByteBuffers_KeepGenericBindingUnlessExplicitlyRaw()
    {
        const string source = """
            using System;
            using Respire;
            public class Writes
            {
                public void Run(IRespireClient client, IRespireCommandQueue queue, byte[] bytes)
                {
                    client.Arrays.SetAsync("k", 0UL, bytes);
                    queue.Arrays.Set("k", 0UL, bytes);
                    client.Arrays.SetAsync("k", 0UL, (ReadOnlyMemory<byte>)bytes);
                    queue.Arrays.Set("k", 0UL, (ReadOnlyMemory<byte>)bytes);
                    client.Arrays.SetAsync("k", 0UL, (RespireValue)bytes, default);
                    queue.Arrays.Set("k", 0UL, new RespireValue[] { bytes });
                }
            }
            """;
        var (compilation, calls) = CompileSource(source);
        await Assert.That(compilation.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error).ToArray()).IsEmpty();
        var semantic = compilation.GetSemanticModel(compilation.SyntaxTrees.Single());
        for (var index = 0; index < calls.Length; index++)
        {
            var method = (IMethodSymbol?)semantic.GetSymbolInfo(calls[index]).Symbol;
            await Assert.That(method).IsNotNull();
            await Assert.That(method!.IsGenericMethod).IsEqualTo(index < 2).Because(calls[index].ToString());
            await Assert.That(method.GetAttributes().Count(attribute => attribute.AttributeClass?.Name
                is "RequiresUnreferencedCodeAttribute" or "RequiresDynamicCodeAttribute")).IsEqualTo(index < 2 ? 2 : 0);
        }
    }

    [Test]
    public async Task NullLiteral_RequiresExplicitPayloadType()
    {
        var (compilation, calls) = CompileCalls("null");
        var errors = compilation.GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error).ToArray();
        await Assert.That(errors.Length).IsEqualTo(calls.Length);
        foreach (var error in errors)
            await Assert.That(error.Id).IsEqualTo("CS0121");
    }

    private static (CSharpCompilation Compilation, InvocationExpressionSyntax[] Calls) CompileCalls(
        string value, string typeArgument = "", string? arrayValue = null)
    {
        arrayValue ??= value;
        var source = $$"""
            using System;
            using Respire;
            public class Writes
            {
                public void Run(RespireClient concrete, IRespireClient client, RespireBatch batch,
                    RespireTransaction transaction, IRespireCommandQueue queue)
                {
                    concrete.SetAsync{{typeArgument}}("k", {{value}});
                    client.SetAsync{{typeArgument}}("k", {{value}}, TimeSpan.FromMinutes(1), RespireSetWhen.NotExists);
                    client.Strings.SetAsync{{typeArgument}}("k", {{value}});
                    client.Strings.SetConditionalAsync{{typeArgument}}("k", {{value}}, RespireValueCondition.EqualTo("old"));
                    client.Hashes.SetAsync{{typeArgument}}("k", "f", {{value}});
                    client.Hashes.SetAsync{{typeArgument}}("k", "f", {{value}}, RespireSetWhen.NotExists);
                    client.Arrays.SetAsync{{typeArgument}}("k", 0UL, {{arrayValue}});
                    client.PathHashes.SetAsync{{typeArgument}}("k", "p", "f", {{value}});
                    client.SortedSets.AddAsync{{typeArgument}}("k", {{value}}, 1D);
                    client.SortedSets.AddAsync{{typeArgument}}("k", default(RespireSortedSetAddOptions), {{value}}, 1D);
                    client.Sets.ContainsAsync{{typeArgument}}("k", {{value}});
                    batch.Set{{typeArgument}}("k", {{value}});
                    transaction.Set{{typeArgument}}("k", {{value}});
                    queue.Set{{typeArgument}}("k", {{value}});
                    queue.Strings.Set{{typeArgument}}("k", {{value}}, TimeSpan.FromMinutes(1), RespireSetWhen.NotExists);
                    queue.Strings.SetConditional{{typeArgument}}("k", {{value}}, RespireValueCondition.EqualTo("old"));
                    queue.Hashes.Set{{typeArgument}}("k", "f", {{value}});
                    queue.Hashes.Set{{typeArgument}}("k", "f", {{value}}, RespireSetWhen.NotExists);
                    queue.Arrays.Set{{typeArgument}}("k", 0UL, {{arrayValue}});
                    queue.PathHashes.Set{{typeArgument}}("k", "p", "f", {{value}});
                    queue.SortedSets.Add{{typeArgument}}("k", {{value}}, 1D);
                    queue.SortedSets.Add{{typeArgument}}("k", default(RespireSortedSetAddOptions), {{value}}, 1D);
                    queue.Sets.Contains{{typeArgument}}("k", {{value}});
                }
            }
            """;
        return CompileSource(source);
    }

    private static (CSharpCompilation Compilation, InvocationExpressionSyntax[] Calls) CompileSource(
        string source, LanguageVersion languageVersion = LanguageVersion.CSharp12)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(languageVersion));
        var compilation = CSharpCompilation.Create("RawWrites", [tree], References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        // Inspect only command calls, not constructors or calls used to create arguments.
        var calls = tree.GetRoot().DescendantNodes().OfType<ExpressionStatementSyntax>()
            .Select(statement => (InvocationExpressionSyntax)statement.Expression).ToArray();
        return (compilation, calls);
    }

    private static readonly MetadataReference[] References = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Append(typeof(RespireClient).Assembly.Location)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Select(path => MetadataReference.CreateFromFile(path))
        .ToArray();
}
