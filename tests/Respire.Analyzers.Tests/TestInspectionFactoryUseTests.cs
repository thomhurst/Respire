using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using static Respire.Analyzers.Tests.TestInspectionSource;

namespace Respire.Analyzers.Tests;

public class TestInspectionFactoryUseTests
{
    [Test]
    [Arguments("new RespireConnection.TestInspection(owner)")]
    [Arguments("new global::Respire.Networking.RespireConnection.TestInspection(owner)")]
    [Arguments("new View(owner)")]
    [Arguments("new(owner)")]
    public async Task DirectViewConstructionCannotBypassFactoryGuard(string expression)
    {
        var source = """
            using View = Respire.Networking.RespireConnection.TestInspection;
            namespace Respire.Networking;
            internal class RespireConnection
            {
                internal readonly ref struct TestInspection(RespireConnection owner) { }
                internal TestInspection InspectForTests() => new(this);
            }
            internal class Consumer
            {
                internal static RespireConnection.TestInspection Create(RespireConnection owner) => EXPRESSION;
            }
            """.Replace("EXPRESSION", expression);
        var root = Parse(source, false);
        var compilation = CreateCompilation([root.SyntaxTree]);
        await Assert.That(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)).IsEmpty();
        await Assert.That(FindFactoryUses(root)).Count().IsEqualTo(1);
    }

    [Test]
    public async Task ConstructionGuardPermitsDesignatedFactoriesAndUnrelatedViews()
    {
        const string source = """
            namespace Respire.Networking
            {
                internal class RespireConnection
                {
                    internal readonly ref struct TestInspection(RespireConnection owner) { }
                    internal TestInspection InspectForTests() => new(this);
                }
            }
            namespace Other
            {
                internal class TestInspection { }
                internal class Consumer { object Create() => new TestInspection(); }
            }
            """;
        var root = Parse(source, false);
        await Assert.That(CreateCompilation([root.SyntaxTree]).GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)).IsEmpty();
        await Assert.That(FindFactoryUses(root)).IsEmpty();
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
    public async Task UnescapedNameofMethodDoesNotHideFactoryCalls()
    {
        const string source = """
            class Example
            {
                static object nameof(object value) => value;
                object InspectForTests() => this;
                void Coordinate() { nameof(InspectForTests()); }
            }
            """;
        var root = Parse(source, false);
        var compilation = CreateCompilation([root.SyntaxTree]);
        await Assert.That(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)).IsEmpty();
        await Assert.That(FindFactoryUses(root)).Count().IsEqualTo(1);
    }

    [Test]
    public async Task NameofShadowingInAnotherPartialIsBoundAcrossSourceFiles()
    {
        var declaration = Parse("partial class Example { static object nameof(object value) => value; object InspectForTests() => this; }", false);
        var caller = Parse("partial class Example { void Coordinate() { nameof(InspectForTests()); } }", false);
        var compilation = CreateCompilation([declaration.SyntaxTree, caller.SyntaxTree]);
        await Assert.That(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)).IsEmpty();
        await Assert.That(FindFactoryUses(caller, compilation.GetSemanticModel(caller.SyntaxTree))).Count().IsEqualTo(1);
    }

    [Test]
    public async Task NameofLocalFunctionDoesNotHideFactoryCalls()
    {
        const string source = """
            class Example
            {
                object InspectForTests() => this;
                void Coordinate()
                {
                    object nameof(object value) => value;
                    nameof(InspectForTests());
                }
            }
            """;
        var root = Parse(source, false);
        var compilation = CreateCompilation([root.SyntaxTree]);
        await Assert.That(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)).IsEmpty();
        await Assert.That(FindFactoryUses(root)).Count().IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ProductionSourceDoesNotUseInspectionFactories(bool net10)
    {
        var sources = ReadLibrarySources().Select(source => (source.Path, Root: Parse(source.Text, net10))).ToArray();
        var compilation = CreateCompilation(sources.Select(source => source.Root.SyntaxTree));
        var violations = sources.SelectMany(source => FindFactoryUses(source.Root, compilation.GetSemanticModel(source.Root.SyntaxTree))
            .Select(use => $"{source.Path}: {use}")).ToArray();
        await Assert.That(violations).IsEmpty();
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
}
