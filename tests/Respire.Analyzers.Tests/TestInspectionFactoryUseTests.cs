using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using static Respire.Analyzers.Tests.TestInspectionSource;

namespace Respire.Analyzers.Tests;

public class TestInspectionFactoryUseTests
{
    /// <summary>Checks compile-valid same-name overloads and nested methods cannot construct borrowed views.</summary>
    [Test]
    [Arguments("internal TestInspection InspectForTests(int ignored) => new(this);")]
    [Arguments("internal static TestInspection InspectForTests(RespireConnection owner) => new(owner);")]
    [Arguments("internal TestInspection InspectForTests<T>() => new(this);")]
    [Arguments("internal object InspectForTests(int ignored) { _ = new TestInspection(this); return this; }")]
    [Arguments("internal class Helper { internal TestInspection InspectForTests(RespireConnection owner) => new(owner); }")]
    public async Task OnlyDesignatedFactoryMayConstructView(string declaration)
    {
        var root = Parse("""
            namespace Respire.Networking;
            internal class RespireConnection
            {
                internal readonly ref struct TestInspection(RespireConnection owner) { }
                internal TestInspection InspectForTests() => new(this);
                DECLARATION
            }
            """.Replace("DECLARATION", declaration), false);
        var compilation = CreateCompilation([root.SyntaxTree]);
        await Assert.That(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)).IsEmpty();
        await Assert.That(FindFactoryUses(root)).Count().IsEqualTo(1);
    }

    /// <summary>Checks aliased and target-typed construction binds to a view declared in another partial file.</summary>
    [Test]
    [Arguments("new View(owner)")]
    [Arguments("new(owner)")]
    public async Task ConstructionInAnotherPartialUsesWholeCompilation(string expression)
    {
        var declaration = Parse("""
            namespace Respire.Networking;
            internal partial class RespireConnection
            {
                internal readonly ref struct TestInspection(RespireConnection owner) { }
                internal TestInspection InspectForTests() => new(this);
            }
            """, false);
        var caller = Parse("""
            using View = Respire.Networking.RespireConnection.TestInspection;
            namespace Respire.Networking;
            internal partial class RespireConnection
            {
                internal static TestInspection Borrow(RespireConnection owner) => EXPRESSION;
            }
            """.Replace("EXPRESSION", expression), false);
        var compilation = CreateCompilation([declaration.SyntaxTree, caller.SyntaxTree]);
        await Assert.That(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)).IsEmpty();
        await Assert.That(FindFactoryUses(caller, compilation.GetSemanticModel(caller.SyntaxTree))).Count().IsEqualTo(1);
    }

    /// <summary>Checks each supported construction spelling is rejected outside its owner's factory.</summary>
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

    /// <summary>Preserves legitimate factory construction and unrelated types sharing the view's simple name.</summary>
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

    /// <summary>Ensures an escaped ordinary method call receives no metadata exemption.</summary>
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

    /// <summary>Distinguishes an ordinary method named nameof from the compiler's metadata operation.</summary>
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

    /// <summary>Ensures a shadowing helper in another partial declaration remains an executable call.</summary>
    [Test]
    public async Task NameofShadowingInAnotherPartialIsBoundAcrossSourceFiles()
    {
        var declaration = Parse("partial class Example { static object nameof(object value) => value; object InspectForTests() => this; }", false);
        var caller = Parse("partial class Example { void Coordinate() { nameof(InspectForTests()); } }", false);
        var compilation = CreateCompilation([declaration.SyntaxTree, caller.SyntaxTree]);
        await Assert.That(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)).IsEmpty();
        await Assert.That(FindFactoryUses(caller, compilation.GetSemanticModel(caller.SyntaxTree))).Count().IsEqualTo(1);
    }

    /// <summary>Checks local functions named nameof cannot hide an executable factory reference.</summary>
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

    /// <summary>Scans all embedded production trees under every SDK-derived framework configuration.</summary>
    [Test]
    public async Task ProductionSourceDoesNotUseInspectionFactories()
    {
        foreach (var configuration in ReadSourceConfigurations())
        {
            var sources = ReadLibrarySources().Select(source => (source.Path, Root: Parse(source.Text, configuration))).ToArray();
            var compilation = CreateCompilation(sources.Select(source => source.Root.SyntaxTree));
            RequireResolvedViews(compilation);
            var violations = sources.SelectMany(source => FindFactoryUses(source.Root, compilation.GetSemanticModel(source.Root.SyntaxTree))
                .Select(use => $"{configuration.Framework}: {source.Path}: {use}")).ToArray();
            await Assert.That(violations).IsEmpty();
        }
    }

    /// <summary>Checks a missing designated view fails before production construction analysis.</summary>
    [Test]
    public async Task MissingDesignatedViewCannotPassVacuously()
    {
        var root = Parse("namespace Respire.Networking; internal class RespireConnection { }", false);
        var compilation = CreateCompilation([root.SyntaxTree]);
        await Assert.That(() => RequireResolvedViews(compilation, ["Respire.Networking.RespireConnection"]))
            .Throws<InvalidOperationException>();
    }

    /// <summary>Covers executable reference spellings while preserving deliberate metadata and textual mentions.</summary>
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
