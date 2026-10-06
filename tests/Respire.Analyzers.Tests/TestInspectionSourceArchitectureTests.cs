using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Analyzers.Tests;

public class TestInspectionSourceArchitectureTests
{
    private const string FactoryName = "InspectForTests";
    private static readonly HashSet<string> Owners = [
        "Respire.Networking.RespireConnection", "Respire.Networking.PendingResponse",
        "Respire.ClientSideCacheCoordinator", "Respire.RespireTransactionBase"];

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OwnerSurfaceMatchesReviewedInventory(bool net10)
    {
        var actual = ReadLibrarySources().SelectMany(source => FindOwnerSurface(Parse(source.Text, net10)))
            .Order(StringComparer.Ordinal).ToArray();
        using var stream = typeof(TestInspectionSourceArchitectureTests).Assembly
            .GetManifestResourceStream("TestInspectionOwnerSurface.txt")!;
        using var reader = new StreamReader(stream);
        var expected = (await reader.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim()).Where(line => !line.StartsWith('#')).ToArray();
        // An empty inventory is deliberately a failure, never an automatic snapshot update.
        if (expected.Length == 0)
            throw new InvalidOperationException("Missing reviewed inventory:\n" + string.Join('\n', actual));
        await Assert.That(actual).IsEquivalentTo(expected);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ProductionSourceDoesNotUseInspectionFactories(bool net10)
    {
        var violations = ReadLibrarySources().SelectMany(source => FindFactoryUses(Parse(source.Text, net10), false)
            .Select(use => $"{source.Path}: {use}")).ToArray();
        await Assert.That(violations).IsEmpty();
    }

    [Test]
    public async Task InventoryDetectsRenamedAccessorAndNewOverloadButPermitsReviewedOperations()
    {
        const string original = """
            namespace Respire.Networking;
            partial class RespireConnection
            {
                internal void Send(int count) { }
                private int ImplementationDetail => 42;
                internal readonly ref struct TestInspection { internal int Inflight => 42; }
            }
            """;
        var reviewed = FindOwnerSurface(Parse(original, false));
        var changed = original.Replace("private int ImplementationDetail", "internal int BorrowedSlots")
            .Replace("internal void Send(int count) { }", "internal void Send(int count) { } internal void Send(string text) { }");
        var added = FindOwnerSurface(Parse(changed, false)).Except(reviewed).ToArray();
        await Assert.That(added).IsEquivalentTo([
            "Respire.Networking.RespireConnection | internal int BorrowedSlots;",
            "Respire.Networking.RespireConnection | internal void Send(string text)"]);
        await Assert.That(FindOwnerSurface(Parse(original.Replace("42", "43"), false)))
            .IsEquivalentTo(reviewed);
        await Assert.That(FindOwnerSurface(Parse(original.Replace("Respire.Networking", "Respire . @Networking"), false)))
            .IsEquivalentTo(reviewed);
        await Assert.That(FindOwnerSurface(Parse("namespace Other; class RespireConnection { internal int BorrowedSlots => 42; }", false)))
            .IsEmpty();
    }

    [Test]
    public async Task FactoryGuardDetectsProductionCallsAndMethodGroupsButPermitsFriendAndMetadataUses()
    {
        const string source = """
            class Example
            {
                internal TestInspection InspectForTests() => new();
                void Coordinate()
                {
                    var direct = connection.InspectForTests();
                    var conditional = connection?.InspectForTests();
                    var unqualified = InspectForTests();
                    var methodGroup = connection.InspectForTests;
                    var escaped = connection.@InspectForTests();
                    var generic = connection.InspectForTests<object>();
                    var metadata = nameof(connection.InspectForTests);
                    connection.Send();
                    // connection.InspectForTests();
                    var text = "connection.InspectForTests()";
                }
            }
            """;
        await Assert.That(FindFactoryUses(Parse(source, false), false)).Count().IsEqualTo(6);
        await Assert.That(FindFactoryUses(Parse(source, false), true)).IsEmpty();
        await Assert.That(FindFactoryUses(Parse("class Example { void Run() { connection.Send(); var name = nameof(connection.InspectForTests); } }", false), false))
            .IsEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FrameworkConditionalAccessorsAndCallsAreInspected(bool net10)
    {
        const string source = """
            namespace Respire.Networking;
            partial class RespireConnection
            {
            #if NET10_0_OR_GREATER
                internal int BorrowedSlots => 42;
                private void Coordinate() { connection.InspectForTests(); }
            #else
                internal string AlternateSlots => "slots";
                private void Coordinate() { InspectForTests(); }
            #endif
            }
            """;
        var root = Parse(source, net10);
        await Assert.That(FindOwnerSurface(root)).IsEquivalentTo([
            net10 ? "Respire.Networking.RespireConnection | internal int BorrowedSlots;"
                : "Respire.Networking.RespireConnection | internal string AlternateSlots;"]);
        await Assert.That(FindFactoryUses(root, false)).Count().IsEqualTo(1);
    }

    private static IEnumerable<(string Path, string Text)> ReadLibrarySources()
    {
        var assembly = typeof(TestInspectionSourceArchitectureTests).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            var path = resource.Replace('\\', '/');
            if (!path.StartsWith("LibrarySource/", StringComparison.Ordinal)) continue;
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            yield return (path, reader.ReadToEnd());
        }
    }

    private static SyntaxNode Parse(string source, bool net10)
    {
        string[] symbols = ["NET", "NETCOREAPP", "NET8_0_OR_GREATER", "NET7_0_OR_GREATER",
            "NET6_0_OR_GREATER", "NET5_0_OR_GREATER", "NETCOREAPP3_1_OR_GREATER",
            "NETCOREAPP3_0_OR_GREATER", "NETCOREAPP2_2_OR_GREATER", "NETCOREAPP2_1_OR_GREATER",
            "NETCOREAPP2_0_OR_GREATER", "NETCOREAPP1_1_OR_GREATER", "NETCOREAPP1_0_OR_GREATER"];
        symbols = symbols.Concat(net10 ? ["NET10_0", "NET10_0_OR_GREATER", "NET9_0_OR_GREATER"] : new[] { "NET8_0" }).ToArray();
        return CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview,
            preprocessorSymbols: symbols)).GetRoot();
    }

    private static string[] FindOwnerSurface(SyntaxNode root)
    {
        var members = new List<string>();
        foreach (var owner in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            if (owner.Ancestors().OfType<TypeDeclarationSyntax>().Any()) continue;
            var namespaces = owner.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse()
                .Select(space => string.Concat(space.Name.DescendantTokens().Select(token => token.ValueText)));
            var name = string.Join('.', namespaces.Append(owner.Identifier.ValueText));
            if (!Owners.Contains(name)) continue;
            foreach (var member in owner.Members)
            {
                // Default-private implementation and private-only declarations cannot expose a friend-test accessor.
                var modifiers = member.Modifiers;
                if (!modifiers.Any(SyntaxKind.PublicKeyword) && !modifiers.Any(SyntaxKind.InternalKeyword)
                    && !modifiers.Any(SyntaxKind.ProtectedKeyword)) continue;
                var header = member;
                if (header is TypeDeclarationSyntax type) header = type.WithMembers(default);
                if (header is EnumDeclarationSyntax enumeration) header = enumeration.WithMembers(default);
                var signature = new HeaderRewriter().Visit(header)!.WithoutTrivia().NormalizeWhitespace().ToFullString();
                members.Add(name + " | " + Regex.Replace(signature, @"\s+", " "));
            }
        }
        return members.ToArray();
    }

    private static string[] FindFactoryUses(SyntaxNode root, bool friendTestSource)
    {
        if (friendTestSource) return [];
        return root.DescendantNodes().OfType<SimpleNameSyntax>()
            .Where(name => name.Identifier.ValueText == FactoryName && !name.Ancestors()
                .OfType<InvocationExpressionSyntax>().Any(call => call.Expression is IdentifierNameSyntax
                    { Identifier.ValueText: "nameof" }))
            .Select(name => $"line {name.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: {name.Parent}")
            .ToArray();
    }

    private sealed class HeaderRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxTrivia VisitTrivia(SyntaxTrivia trivia) => default;
        public override SyntaxNode? VisitBlock(BlockSyntax node) => null;
        public override SyntaxNode? VisitArrowExpressionClause(ArrowExpressionClauseSyntax node) => null;
        public override SyntaxNode? VisitEqualsValueClause(EqualsValueClauseSyntax node) => null;
        public override SyntaxNode? VisitConstructorInitializer(ConstructorInitializerSyntax node) => null;
    }
}
