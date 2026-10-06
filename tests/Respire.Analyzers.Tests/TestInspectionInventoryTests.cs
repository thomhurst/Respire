using Microsoft.CodeAnalysis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;
using static Respire.Analyzers.Tests.TestInspectionSource;

namespace Respire.Analyzers.Tests;

public class TestInspectionInventoryTests
{
    /// <summary>Checks an aliased indirect subclass in another file cannot expose protected owner state unnoticed.</summary>
    [Test]
    public async Task DerivedTypesBindAcrossFilesAndAliases()
    {
        var declaration = Parse("""
            namespace Respire;
            public class RespireTransactionBase { private protected object WatchPool => new(); }
            public class Parent : RespireTransactionBase { }
            """, false);
        var caller = Parse("""
            using Base = Respire.Parent;
            namespace Other;
            internal class Outer
            {
                internal class Child : Base { internal object BorrowedPool => WatchPool; }
            }
            """, false);
        var compilation = CreateCompilation([declaration.SyntaxTree, caller.SyntaxTree]);
        await Assert.That(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)).IsEmpty();
        await Assert.That(FindOwnerSurface(caller, compilation.GetSemanticModel(caller.SyntaxTree)))
            .IsEquivalentTo(["Other.Outer.Child | property BorrowedPool [internal; get] : object"]);
    }

    /// <summary>Checks differently named accessors on direct and indirect subclasses require inventory review.</summary>
    [Test]
    [Arguments("Respire.RespireTransactionBase")]
    [Arguments("Parent")]
    public async Task InventoryIncludesPrivilegedDerivedMembers(string baseType)
    {
        var original = """
            namespace Respire;
            public class RespireTransactionBase { private protected object WatchPool => new(); }
            public class Parent : RespireTransactionBase { }
            public class Child : BASE { MEMBER }
            """.Replace("BASE", baseType);
        var changed = original.Replace("MEMBER", "internal object BorrowedPool => WatchPool;");
        var root = Parse(changed, false);
        await Assert.That(CreateCompilation([root.SyntaxTree]).GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)).IsEmpty();
        await Assert.That(FindOwnerSurface(root).Except(FindOwnerSurface(Parse(original.Replace("MEMBER", ""), false))))
            .IsEquivalentTo(["Respire.Child | property BorrowedPool [internal; get] : object"]);
    }

    /// <summary>Checks writable, initializable and more accessible property shapes change their reviewed keys.</summary>
    [Test]
    [Arguments("internal int Slots { get; set; }")]
    [Arguments("internal int Slots { get; init; }")]
    [Arguments("internal int Slots { get; private set; }")]
    [Arguments("public int Slots { get; }")]
    public async Task InventoryDetectsPropertyAccessorAndAccessibilityChanges(string declaration)
    {
        const string original = "namespace Respire.Networking; class RespireConnection { internal int Slots { get; } }";
        var changed = original.Replace("internal int Slots { get; }", declaration);
        await Assert.That(FindOwnerSurface(Parse(changed, false))
            .Except(FindOwnerSurface(Parse(original, false))).Count()).IsEqualTo(1);
    }

    /// <summary>Checks adding an indexer setter changes its reviewed declaration.</summary>
    [Test]
    public async Task InventoryDetectsWritableIndexer()
    {
        const string original = "namespace Respire.Networking; class RespireConnection { internal int this[int i] => 1; }";
        var changed = original.Replace("=> 1;", "{ get => 1; set { } }");
        await Assert.That(FindOwnerSurface(Parse(changed, false))
            .Except(FindOwnerSurface(Parse(original, false))).Count()).IsEqualTo(1);
    }

    /// <summary>Checks nested helpers and static view members cannot create an unreviewed state-access route.</summary>
    [Test]
    [Arguments("internal class Helper { MEMBER }")]
    [Arguments("internal class Helper { internal class Nested { MEMBER } }")]
    [Arguments("internal readonly ref struct TestInspection { MEMBER }")]
    public async Task InventoryDetectsStaticAccessorsInsideAccessibleNestedTypes(string nested)
    {
        var source = "namespace Respire.Networking; class RespireConnection { private object _state = new(); " + nested + " }";
        var original = source.Replace("MEMBER", "");
        var changed = source.Replace("MEMBER", "internal static object Borrow(RespireConnection owner) => owner._state;");
        var root = Parse(changed, false);
        await Assert.That(CreateCompilation([root.SyntaxTree]).GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)).IsEmpty();
        await Assert.That(FindOwnerSurface(root).Except(FindOwnerSurface(Parse(original, false))).Count()).IsEqualTo(1);
    }

    /// <summary>Preserves the designated borrowed view and inaccessible implementation helpers.</summary>
    [Test]
    [Arguments("internal readonly ref struct TestInspection { MEMBER }")]
    [Arguments("private class Helper { MEMBER }")]
    public async Task InventoryPermitsBorrowedInstanceViewsAndPrivateHelpers(string nested)
    {
        var source = "namespace Respire.Networking; class RespireConnection { " + nested + " }";
        await Assert.That(FindOwnerSurface(Parse(source.Replace("MEMBER", "internal int State => 42;"), false)))
            .IsEquivalentTo(FindOwnerSurface(Parse(source.Replace("MEMBER", ""), false)));
    }

    /// <summary>Checks removing readonly or ref restrictions changes the view's reviewed declaration.</summary>
    [Test]
    [Arguments("ref struct")]
    [Arguments("readonly struct")]
    [Arguments("struct")]
    public async Task InventoryDetectsRemovedViewLifetimeModifiers(string replacement)
    {
        const string original = "namespace Respire.Networking; class RespireConnection { internal readonly ref struct TestInspection { } }";
        var changed = original.Replace("readonly ref struct", replacement);
        await Assert.That(FindOwnerSurface(Parse(changed, false))
            .Except(FindOwnerSurface(Parse(original, false))).Count()).IsEqualTo(1);
    }

    /// <summary>Rejects owner declaration kinds the class-based inventory does not support.</summary>
    [Test]
    [Arguments("struct")]
    [Arguments("record")]
    [Arguments("record struct")]
    [Arguments("interface")]
    public async Task InventoryRejectsUnsupportedOwnerKinds(string kind)
    {
        var root = Parse($"namespace Respire.Networking; {kind} RespireConnection {{ }}", false);
        await Assert.That(() => FindOwnerSurface(root)).Throws<InvalidOperationException>();
    }

    /// <summary>Checks missing owners and wrong-namespace names cannot silently reduce guard coverage.</summary>
    [Test]
    public async Task OwnerDiscoveryRequiresEveryQualifiedOwner()
    {
        var roots = Owners.Select(name => Parse($"namespace {name[..name.LastIndexOf('.')]} {{ class {name[(name.LastIndexOf('.') + 1)..]} {{ }} }}", false)).ToArray();
        await Assert.That(FindMissingOwners(roots)).IsEmpty();
        var omitted = roots[0];
        await Assert.That(FindMissingOwners(roots.Skip(1))).IsEquivalentTo(
            FindOwnerDeclarations(omitted).Select(owner => owner.Name));
        await Assert.That(FindMissingOwners([Parse("namespace Other; class RespireConnection { }", false)]))
            .IsEquivalentTo(Owners);
    }

    /// <summary>Checks primary constructors use the same reviewed overload keys as explicit constructors.</summary>
    [Test]
    [Arguments("")]
    [Arguments("internal")]
    [Arguments("public")]
    [Arguments("public abstract")]
    public async Task InventoryDetectsOwnerPrimaryConstructorOverloads(string accessibility)
    {
        const string declaration = """
            namespace Respire.Networking;
            partial class RespireConnection
            {
                internal RespireConnection() { }
            }
            """;
        var original = declaration.Replace("partial class", accessibility + " partial class");
        var changed = original.Replace("class RespireConnection", "class RespireConnection(int count)")
            .Replace("internal RespireConnection() { }", "internal RespireConnection() : this(0) { }");
        var root = Parse(changed, false);
        var compilation = CreateCompilation([root.SyntaxTree]);
        await Assert.That(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)).IsEmpty();
        await Assert.That(FindOwnerSurface(root).Except(FindOwnerSurface(Parse(original, false)))).IsEquivalentTo([
            "Respire.Networking.RespireConnection | constructor RespireConnection(int)"]);
        var explicitConstructor = original.Replace("internal RespireConnection() { }",
            "internal RespireConnection() { } public RespireConnection(int renamed) { }");
        await Assert.That(FindOwnerSurface(root)).IsEquivalentTo(FindOwnerSurface(Parse(explicitConstructor, false)));
    }

    /// <summary>Covers interface methods, properties, indexers and events callable through a cast.</summary>
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
            "Respire.Networking.RespireConnection | property IInspection.Slots [explicit; get] : int",
            "Respire.Networking.RespireConnection | indexer IInspection.this(int) [explicit; get] : int",
            "Respire.Networking.RespireConnection | event IInspection.Changed : System.Action"]);
    }

    /// <summary>Preserves declaration keys across implementation and non-signature maintenance edits.</summary>
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

    /// <summary>Requires exact reviewed signatures and per-type counts under every production framework configuration.</summary>
    [Test]
    public async Task OwnerSurfaceMatchesReviewedInventory()
    {
        using var stream = OpenResource("TestInspectionOwnerSurface.txt");
        using var reader = new StreamReader(stream);
        var lines = (await reader.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Trim()).ToArray();
        var expected = lines.Where(line => !line.StartsWith('#')).ToArray();
        var counts = lines.Where(line => line.StartsWith("# Count: ", StringComparison.Ordinal))
            .Select(line => line[9..].Split(" | ")).ToDictionary(parts => parts[0], parts => int.Parse(parts[1]), StringComparer.Ordinal);
        foreach (var configuration in ReadSourceConfigurations())
        {
            var roots = ReadLibrarySources().Select(source => Parse(source.Text, configuration)).ToArray();
            await Assert.That(FindMissingOwners(roots)).IsEmpty();
            var compilation = CreateCompilation(roots.Select(root => root.SyntaxTree));
            var actual = roots.SelectMany(root => FindOwnerSurface(root, compilation.GetSemanticModel(root.SyntaxTree)))
                .Order(StringComparer.Ordinal).ToArray();
            // An empty inventory is deliberately a failure, never an automatic snapshot update.
            if (expected.Length == 0)
                throw new InvalidOperationException("Missing reviewed inventory:\n" + string.Join('\n', actual));
            await Assert.That(actual).IsEquivalentTo(expected).Because(
                $"{configuration.Framework}: Review changes using docs/TEST_INSPECTION.md. Only for approved operational changes, copy the exact unreviewed lines into TestInspectionOwnerSurface.txt and remove the exact removed lines. Never accept a new inspection bypass.\n"
                + "Unreviewed signatures:\n" + string.Join('\n', actual.Except(expected, StringComparer.Ordinal))
                + "\nRemoved signatures:\n" + string.Join('\n', expected.Except(actual, StringComparer.Ordinal)));
            await Assert.That(counts).IsNotEmpty();
            foreach (var group in actual.GroupBy(signature => signature.Split(" | ")[0], StringComparer.Ordinal))
            {
                await Assert.That(counts.ContainsKey(group.Key)).IsTrue().Because("Missing reviewed count for " + group.Key);
                await Assert.That(group.Count()).IsEqualTo(counts[group.Key]).Because("Reviewed count for " + group.Key);
            }
            await Assert.That(counts.Keys).IsEquivalentTo(actual.Select(signature => signature.Split(" | ")[0]).Distinct());
        }
    }

    /// <summary>Checks new access paths require review while existing operations retain stable keys.</summary>
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
            "Respire.Networking.RespireConnection | property BorrowedSlots [internal; get] : int",
            "Respire.Networking.RespireConnection | method Send(string) : void"]);
        await Assert.That(FindOwnerSurface(Parse(original.Replace("42", "43"), false)))
            .IsEquivalentTo(reviewed);
        await Assert.That(FindOwnerSurface(Parse(original.Replace("Respire.Networking", "Respire . @Networking"), false)))
            .IsEquivalentTo(reviewed);
        await Assert.That(FindOwnerSurface(Parse("namespace Other; class RespireConnection { internal int BorrowedSlots => 42; }", false)))
            .IsEmpty();
    }

    /// <summary>Checks both framework branches participate in surface and executable-use guards.</summary>
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
            net10 ? "Respire.Networking.RespireConnection | property BorrowedSlots [internal; get] : int"
                : "Respire.Networking.RespireConnection | property AlternateSlots [internal; get] : string"]);
        await Assert.That(FindFactoryUses(root)).Count().IsEqualTo(1);
    }
}
