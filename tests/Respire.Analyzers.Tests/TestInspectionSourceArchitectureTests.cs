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
    public async Task InventoryIncludesExplicitInterfaceMembers()
    {
        const string source = """
            namespace Respire.Networking;
            partial class RespireConnection : IInspection
            {
                int IInspection.ReadSlots() => 42;
                int IInspection.Slots => 42;
                int IInspection.this[int index] => 42;
                event System.Action IInspection.Changed { add { } remove { } }
            }
            """;
        await Assert.That(FindOwnerSurface(Parse(source, false))).IsEquivalentTo([
            "Respire.Networking.RespireConnection | method IInspection.ReadSlots() : int",
            "Respire.Networking.RespireConnection | property IInspection.Slots : int",
            "Respire.Networking.RespireConnection | indexer IInspection.this(int) : int",
            "Respire.Networking.RespireConnection | event IInspection.Changed : System.Action"]);
    }

    [Test]
    public async Task InventoryIgnoresBodyStyleParameterNamesAttributesAndConstraints()
    {
        const string original = """
            namespace Respire.Networking;
            partial class RespireConnection
            {
                internal int Slots => 42;
                internal int Read<T>(int count) where T : class => count;
            }
            """;
        const string changed = """
            namespace Respire.Networking;
            partial class RespireConnection
            {
                internal int Slots { get { return 43; } }
                [System.Obsolete] internal int Read<T>(int renamed) where T : struct { return renamed; }
            }
            """;
        await Assert.That(FindOwnerSurface(Parse(changed, false)))
            .IsEquivalentTo(FindOwnerSurface(Parse(original, false)));
    }

    [Test]
    public async Task EscapedNameofMethodDoesNotHideFactoryCalls()
    {
        const string source = """
            class Example
            {
                static void @nameof(object value) { }
                void Coordinate() { @nameof(connection.InspectForTests()); }
            }
            """;
        await Assert.That(FindFactoryUses(Parse(source, false))).Count().IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OwnerSurfaceMatchesReviewedInventory(bool net10)
    {
        var actual = ReadLibrarySources().SelectMany(source => FindOwnerSurface(Parse(source.Text, net10)))
            .Order(StringComparer.Ordinal).ToArray();
        using var stream = OpenResource("TestInspectionOwnerSurface.txt");
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
        var violations = ReadLibrarySources().SelectMany(source => FindFactoryUses(Parse(source.Text, net10))
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
            "Respire.Networking.RespireConnection | property BorrowedSlots : int",
            "Respire.Networking.RespireConnection | method Send(string) : void"]);
        await Assert.That(FindOwnerSurface(Parse(original.Replace("42", "43"), false)))
            .IsEquivalentTo(reviewed);
        await Assert.That(FindOwnerSurface(Parse(original.Replace("Respire.Networking", "Respire . @Networking"), false)))
            .IsEquivalentTo(reviewed);
        await Assert.That(FindOwnerSurface(Parse("namespace Other; class RespireConnection { internal int BorrowedSlots => 42; }", false)))
            .IsEmpty();
    }

    [Test]
    public async Task FactoryGuardDetectsProductionCallsAndMethodGroupsButPermitsMetadataUses()
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
        await Assert.That(FindFactoryUses(Parse(source, false))).Count().IsEqualTo(6);
        await Assert.That(FindFactoryUses(Parse("class Example { void Run() { connection.Send(); var name = nameof(connection.InspectForTests); } }", false)))
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
            net10 ? "Respire.Networking.RespireConnection | property BorrowedSlots : int"
                : "Respire.Networking.RespireConnection | property AlternateSlots : string"]);
        await Assert.That(FindFactoryUses(root)).Count().IsEqualTo(1);
    }

    [Test]
    public async Task EmbeddedProductionSourcesExcludeFriendTestSource()
    {
        var sources = ReadLibrarySources().ToArray();
        await Assert.That(sources).IsNotEmpty();
        await Assert.That(sources.Any(source => source.Path.Split('/')
            .Any(segment => segment.Equals("tests", StringComparison.OrdinalIgnoreCase)))).IsFalse();
        await Assert.That(sources.Any(source => source.Path.EndsWith("/RespireConnection.TestInspection.cs", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task MissingResourceReportsItsName()
    {
        const string missing = "MissingTestInspectionOwnerSurface.txt";
        InvalidOperationException? failure = null;
        try { using var stream = OpenResource(missing); }
        catch (InvalidOperationException error) { failure = error; }
        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.Message).IsEqualTo("Missing embedded resource: " + missing);
    }

    private static Stream OpenResource(string resource)
        => typeof(TestInspectionSourceArchitectureTests).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("Missing embedded resource: " + resource);

    private static IEnumerable<(string Path, string Text)> ReadLibrarySources()
    {
        var assembly = typeof(TestInspectionSourceArchitectureTests).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            var path = resource.Replace('\\', '/');
            if (!path.StartsWith("LibrarySource/", StringComparison.Ordinal)) continue;
            using var stream = OpenResource(resource);
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
                // Explicit implementations remain callable through an interface, despite lacking accessibility modifiers.
                var modifiers = member.Modifiers;
                if (!modifiers.Any(SyntaxKind.PublicKeyword) && !modifiers.Any(SyntaxKind.InternalKeyword)
                    && !modifiers.Any(SyntaxKind.ProtectedKeyword) && ExplicitInterface(member) is null) continue;
                members.AddRange(MemberKeys(member).Select(key => name + " | " + key));
            }
        }
        return members.ToArray();
    }

    private static string[] FindFactoryUses(SyntaxNode root)
    {
        return root.DescendantNodes().OfType<SimpleNameSyntax>()
            .Where(name => name.Identifier.ValueText == FactoryName && !name.Ancestors()
                .OfType<InvocationExpressionSyntax>().Any(call => call.Expression is IdentifierNameSyntax
                    { Identifier.Text: "nameof" }))
            .Select(name => $"line {name.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: {name.Parent}")
            .ToArray();
    }

    private static ExplicitInterfaceSpecifierSyntax? ExplicitInterface(MemberDeclarationSyntax member)
        => member switch
        {
            MethodDeclarationSyntax method => method.ExplicitInterfaceSpecifier,
            PropertyDeclarationSyntax property => property.ExplicitInterfaceSpecifier,
            IndexerDeclarationSyntax indexer => indexer.ExplicitInterfaceSpecifier,
            EventDeclarationSyntax @event => @event.ExplicitInterfaceSpecifier,
            _ => null
        };

    private static IEnumerable<string> MemberKeys(MemberDeclarationSyntax member)
    {
        var prefix = ExplicitInterface(member) is { } specifier ? TypeText(specifier.Name) + "." : "";
        switch (member)
        {
            case MethodDeclarationSyntax method:
                yield return $"method {prefix}{method.Identifier.ValueText}{Arity(method.TypeParameterList)}({Parameters(method.ParameterList)}) : {TypeText(method.ReturnType)}";
                break;
            case ConstructorDeclarationSyntax constructor:
                yield return $"constructor {constructor.Identifier.ValueText}({Parameters(constructor.ParameterList)})";
                break;
            case PropertyDeclarationSyntax property:
                yield return $"property {prefix}{property.Identifier.ValueText} : {TypeText(property.Type)}";
                break;
            case IndexerDeclarationSyntax indexer:
                yield return $"indexer {prefix}this({Parameters(indexer.ParameterList)}) : {TypeText(indexer.Type)}";
                break;
            case EventDeclarationSyntax @event:
                yield return $"event {prefix}{@event.Identifier.ValueText} : {TypeText(@event.Type)}";
                break;
            case BaseFieldDeclarationSyntax field:
                foreach (var variable in field.Declaration.Variables)
                    yield return $"{(field is EventFieldDeclarationSyntax ? "event" : "field")} {variable.Identifier.ValueText} : {TypeText(field.Declaration.Type)}";
                break;
            case TypeDeclarationSyntax type:
                var kind = type.Keyword.ValueText;
                if (type is RecordDeclarationSyntax record)
                    kind = record.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ? "record struct" : "record class";
                yield return $"{kind} {type.Identifier.ValueText}{Arity(type.TypeParameterList)}({Parameters(type.ParameterList)})";
                break;
            case EnumDeclarationSyntax enumeration:
                yield return $"enum {enumeration.Identifier.ValueText} : {(enumeration.BaseList is { } bases ? TypeText(bases.Types.Single().Type) : "int")}";
                break;
            case DelegateDeclarationSyntax @delegate:
                yield return $"delegate {@delegate.Identifier.ValueText}{Arity(@delegate.TypeParameterList)}({Parameters(@delegate.ParameterList)}) : {TypeText(@delegate.ReturnType)}";
                break;
            case OperatorDeclarationSyntax @operator:
                yield return $"operator {@operator.OperatorToken.ValueText}({Parameters(@operator.ParameterList)}) : {TypeText(@operator.ReturnType)}";
                break;
            case ConversionOperatorDeclarationSyntax conversion:
                yield return $"conversion {conversion.ImplicitOrExplicitKeyword.ValueText}({Parameters(conversion.ParameterList)}) : {TypeText(conversion.Type)}";
                break;
            default:
                throw new InvalidOperationException("Uninventoried member kind: " + member.Kind());
        }
    }

    private static string Arity(TypeParameterListSyntax? parameters)
        => parameters is null ? "" : "`" + parameters.Parameters.Count;

    private static string Parameters(BaseParameterListSyntax? list)
        => list is null ? "" : string.Join(", ", list.Parameters.Select(parameter =>
            string.Concat(parameter.Modifiers.Select(modifier => modifier.ValueText + " ")) + TypeText(parameter.Type!)));

    private static string TypeText(SyntaxNode type)
        => type.ReplaceTrivia(type.DescendantTrivia(), (_, _) => default).NormalizeWhitespace().ToFullString();
}
