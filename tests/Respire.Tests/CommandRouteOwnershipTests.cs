using System.Text.Json;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class CommandRouteOwnershipTests
{
    private const string Fixture = """
        namespace Respire;
        public interface IExampleCommands { ValueTask<int> GetAsync(int key); }
        internal class ExampleCommands : IExampleCommands {
            public ValueTask<int> GetAsync(int key) => default;
            private ValueTask<int> RetryAsync(int key) => default;
            private void Probe() { }
        }
        """;
    private const string Route = "GetAsync(int):ValueTask<int>";
    private const string Owner = "Respire.ExampleCommands." + Route;
    private static CommandRouteOwnership.Member[] Discover(string source)
        => Discover([("fixture.cs", source)]);
    private static CommandRouteOwnership.Member[] Discover(IEnumerable<(string File, string Source)> files)
        => CommandRouteOwnership.Discover(files).Where(m => m.Framework == "net8.0").ToArray();
    private static CommandRouteOwnership.Inventory ControlInventory(params CommandRouteOwnership.Boundary[] boundaries)
        => new([new("Respire.IExampleCommands", "Respire.ExampleCommands", [Route], Contract: "Own validation through cleanup.")], boundaries);

    [Test]
    [Arguments("DEBUG")]
    [Arguments("TRACE")]
    public async Task ConfigurationConstantsMatchTheCompiledCore(string symbol)
    {
        var expected = symbol == "TRACE";
#if DEBUG
        expected = true;
#endif
        var source = CommandRouteOwnership.Discover([("configuration.cs", $$"""
            public class Routes {
            #if {{symbol}}
                public int Added() => 1;
            #endif
                private void Probe() { }
            }
            """)]);
        foreach (var framework in new[] { "net8.0", "net10.0" })
        {
            await Assert.That(source.Any(m => m.Framework == framework && m.PublicRoute)).IsEqualTo(expected);
            await Assert.That(CommandRouteOwnership.Validate(source, new([], []))
                .Contains("Undeclared public route: Routes.Added():int [" + framework + "]")).IsEqualTo(expected);
        }
    }

    [Test]
    [Arguments("public class Routes { public void Added( { }")]
    [Arguments("public static class Routes { extension(string value) { public int Added() => 1; } }")]
    public async Task ParserErrorsAndUnsupportedDeclarationsFailClosed(string source)
    {
        await Assert.That(() => CommandRouteOwnership.Discover([("unsupported.cs", source)]))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task ModernCollectionExpressionRoutesAreDiscovered()
    {
        var source = Discover("public class Routes { public int[] Added(bool all) => all ? [] : [1]; }");
        await Assert.That(CommandRouteOwnership.Validate(source, new([], [])))
            .Contains("Undeclared public route: Routes.Added(bool):int[] [net8.0]");
    }

    [Test]
    public async Task ImplicitlyPublicNestedInterfaceRequiresExecutableOwner()
    {
        var source = CommandRouteOwnership.Discover([("fixture.cs", """
            public interface Outer { interface Commands { ValueTask RunAsync(); } }
            internal class Owner : Outer.Commands { public ValueTask RunAsync() => default; }
            """)]);
        var inventory = new CommandRouteOwnership.Inventory([
            new("Outer.Commands", "Owner", ["RunAsync():ValueTask"], Contract: "Own calls through completion.")], []);
        foreach (var framework in new[] { "net8.0", "net10.0" })
        {
            await Assert.That(CommandRouteOwnership.Validate(source, new([], [])))
                .Contains("Undeclared public route: Outer.Commands.RunAsync():ValueTask [" + framework + "]");
            await Assert.That(CommandRouteOwnership.Validate(source.Where(m => m.Type != "Owner").ToArray(), inventory))
                .Contains("Missing final owner: Outer.Commands.RunAsync():ValueTask => Owner.RunAsync():ValueTask [" + framework + "]");
        }
        await Assert.That(CommandRouteOwnership.Validate(source, inventory)).IsEmpty();
    }

    [Test]
    [Arguments("interface", "")]
    [Arguments("class", "public")]
    [Arguments("struct", "public")]
    [Arguments("record", "public")]
    [Arguments("record struct", "public")]
    public async Task TypesNestedInPublicInterfacesDefaultToPublic(string kind, string methodVisibility)
    {
        var source = CommandRouteOwnership.Discover([("fixture.cs", "public interface Outer { interface Middle { "
            + kind + " Commands { " + methodVisibility + " ValueTask RunAsync() => default; } } }")]);
        await Assert.That(source.All(m => m.PublicRoute)).IsTrue();
        foreach (var framework in new[] { "net8.0", "net10.0" })
            await Assert.That(CommandRouteOwnership.Validate(source, new([], [])))
                .Contains("Undeclared public route: Outer.Middle.Commands.RunAsync():ValueTask [" + framework + "]");
        var inventory = new CommandRouteOwnership.Inventory([
            new("Outer.Middle.Commands", "Outer.Middle.Commands", ["RunAsync():ValueTask"], Contract: "Own calls through completion.")], []);
        await Assert.That(CommandRouteOwnership.Validate(source, inventory)).IsEmpty();
    }

    [Test]
    [Arguments("public interface", "private")]
    [Arguments("public interface", "internal")]
    [Arguments("public interface", "protected")]
    [Arguments("public interface", "private protected")]
    [Arguments("public interface", "protected internal")]
    [Arguments("internal interface", "")]
    [Arguments("public class", "")]
    public async Task NonPublicNestedTypesDoNotBecomePublicRoutes(string outer, string nestedVisibility)
    {
        var source = CommandRouteOwnership.Discover([("fixture.cs", outer + " Outer { " + nestedVisibility
            + " interface Commands { ValueTask RunAsync() => default; } }")]);
        await Assert.That(source.All(m => !m.PublicRoute)).IsTrue();
        await Assert.That(CommandRouteOwnership.Validate(source, new([], []))).IsEmpty();
    }

    [Test]
    [Arguments("", "ICommands<T>", "Respire")]
    [Arguments("using Respire;", "ICommands<T>", "Implementation")]
    [Arguments("using Root = Respire;", "Root.ICommands<T>", "Implementation")]
    [Arguments("", "global::Respire.ICommands<T>", "Implementation")]
    [Arguments("using Contract = Respire.ICommands<int>;", "Contract", "Implementation")]
    public async Task GenericOwnersImplementNormalizedContract(string imports, string contract, string ownerNamespace)
    {
        var source = CommandRouteOwnership.Discover([
            ("contract.cs", "namespace Respire; public interface ICommands<T> { int Get(); }"),
            ("owner.cs", imports + " namespace " + ownerNamespace + "; internal class Commands<T> : " + contract
                + " { public int Get() => 1; }")]);
        var inventory = new CommandRouteOwnership.Inventory([
            new("Respire.ICommands`1", ownerNamespace + ".Commands`1", ["Get():int"], Contract: "Control")], []);
        await Assert.That(CommandRouteOwnership.Validate(source, inventory)).IsEmpty();
    }

    [Test]
    public async Task SourceRoutesHaveDeclaredExecutableFinalOwners()
    {
        var repo = CommandRouteOwnership.FindRepository();
        var files = Directory.EnumerateFiles(Path.Combine(repo, "src", "Respire"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(repo, f).Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj"))
            .Select(f => (File: Path.GetRelativePath(repo, f), Source: File.ReadAllText(f)));
        var source = CommandRouteOwnership.Discover(files);
        var declarations = Directory.EnumerateFiles(Path.Combine(repo, "src", "Respire"), "*.ownership.json", SearchOption.AllDirectories)
            .Select(file => (File: Path.GetRelativePath(repo, file)[..^".ownership.json".Length],
                Inventory: JsonSerializer.Deserialize<CommandRouteOwnership.Inventory>(File.ReadAllText(file))!)).ToArray();
        var inventories = declarations.Select(d => d.Inventory).ToArray();
        var inventory = new CommandRouteOwnership.Inventory(inventories.SelectMany(i => i.Surfaces).ToArray(),
            inventories.SelectMany(i => i.Boundaries).ToArray(), inventories.SelectMany(i => i.NonRoutes ?? []).ToArray());
        var errors = CommandRouteOwnership.Validate(source, inventory).ToList();
        foreach (var declaration in declarations)
        {
            var ids = declaration.Inventory.Surfaces.SelectMany(s => s.Members.Select(m => s.Type + "." + m))
                .Concat(declaration.Inventory.Boundaries.Select(b => b.Member))
                .Concat((declaration.Inventory.NonRoutes ?? []).Select(n => n.Member));
            foreach (var id in ids)
                if (!source.Any(m => m.Id == id && (m.Files ?? [m.File]).Contains(declaration.File, StringComparer.Ordinal)))
                    errors.Add("Declaration must stay beside its source: " + id + " in " + declaration.File);
        }
        await Assert.That(string.Join(Environment.NewLine, errors)).IsEqualTo("");
        // Pin the non-public families as well: deleting a declaration cannot silently erase its coverage.
        foreach (var name in RequiredBoundaries)
            await Assert.That(inventory.Boundaries.Any(b => b.Name == name)).IsTrue();
    }

    [Test]
    public async Task NewPublicRouteWithoutDeclarationFails()
    {
        var source = Fixture.Replace("ValueTask<int> GetAsync(int key);",
            "ValueTask<int> GetAsync(int key); ValueTask<int> NewAsync(string key);");
        await Assert.That(CommandRouteOwnership.Validate(Discover(source), ControlInventory())
            .Any(e => e.StartsWith("Undeclared public route:", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task NewOverloadWithoutDeclarationFails()
    {
        var source = Fixture.Replace("ValueTask<int> GetAsync(int key);",
            "ValueTask<int> GetAsync(int key); ValueTask<int> GetAsync(string key);");
        await Assert.That(CommandRouteOwnership.Validate(Discover(source), ControlInventory())
            .Any(e => e.Contains("GetAsync(string)", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task RemovingOwnerImplementationFailsEvenWithUnchangedPublicSurface()
    {
        var source = Fixture.Replace("public ValueTask<int> GetAsync(int key) => default;", "");
        await Assert.That(CommandRouteOwnership.Validate(Discover(source), ControlInventory())
            .Any(e => e.StartsWith("Missing final owner:", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task RemovingOwnerDeclarationFails()
    {
        var inventory = ControlInventory() with { Surfaces = [new("Respire.IExampleCommands", "", [Route], Contract: "Control")] };
        await Assert.That(CommandRouteOwnership.Validate(Discover(Fixture), inventory)
            .Any(e => e.StartsWith("Missing final owner:", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task PublicContractWithoutExecutableOwnerFails()
    {
        var inventory = ControlInventory() with { Surfaces = [new("Respire.IExampleCommands", "Respire.IExampleCommands", [Route], Contract: "Control")] };
        await Assert.That(CommandRouteOwnership.Validate(Discover(Fixture), inventory)
            .Any(e => e.StartsWith("Missing final owner:", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task EveryDeclaredAlternativeOwnerMustRemainExecutable()
    {
        var source = Fixture + " internal class OtherCommands : IExampleCommands { public ValueTask<int> GetAsync(int key) => default; }";
        var inventory = ControlInventory() with { Surfaces = [new("Respire.IExampleCommands", "Respire.ExampleCommands", [Route],
            Contract: "Both implementations own their caller.", AdditionalOwnerTypes: ["Respire.OtherCommands"])] };
        await Assert.That(CommandRouteOwnership.Validate(Discover(source), inventory)).IsEmpty();
        await Assert.That(CommandRouteOwnership.Validate(Discover(Fixture), inventory)
            .Any(e => e.StartsWith("Missing alternative final owner:", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task HelperBorrowerAndInternalObservationAreValidWithoutBecomingFinalOwners()
    {
        var helper = new CommandRouteOwnership.Boundary("helper", "helper", "Respire.ExampleCommands.RetryAsync(int):ValueTask<int>", Owner, "Delegate caller completion.");
        var borrower = helper with { Name = "borrower", Role = "borrower", Contract = "Report handled retry and complete only borrowed lease." };
        var probe = new CommandRouteOwnership.Boundary("probe", "internal", "Respire.ExampleCommands.Probe():void", null, "Probe has no caller final failure.");
        await Assert.That(CommandRouteOwnership.Validate(Discover(Fixture), ControlInventory(helper, borrower, probe))).IsEmpty();
    }

    [Test]
    public async Task BorrowerCannotDeclareItselfFinalOwner()
    {
        const string member = "Respire.ExampleCommands.RetryAsync(int):ValueTask<int>";
        var borrower = new CommandRouteOwnership.Boundary("retry", "borrower", member, member, "Control");
        await Assert.That(CommandRouteOwnership.Validate(Discover(Fixture), ControlInventory(borrower))
            .Any(e => e.StartsWith("Borrowed/helper boundary cannot own itself:", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task InternalObservationCannotPublishCallerFailure()
    {
        var probe = new CommandRouteOwnership.Boundary("probe", "internal", "Respire.ExampleCommands.Probe():void", Owner, "Control");
        await Assert.That(CommandRouteOwnership.Validate(Discover(Fixture), ControlInventory(probe))
            .Any(e => e.StartsWith("Internal observation cannot publish caller failure:", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task PublicPartialRouteWithoutRepeatedVisibilityIsDiscovered()
    {
        var source = CommandRouteOwnership.Discover([
            ("first.cs", "namespace Respire; public partial class Routes { }"),
            ("second.cs", "namespace Respire; partial class Routes { public ValueTask<int> AddedAsync() => default; }")])
            .Where(m => m.Framework == "net8.0").ToArray();
        await Assert.That(source.Single().PublicRoute).IsTrue();
        await Assert.That(CommandRouteOwnership.Validate(source, new([], []))
            .Any(e => e.StartsWith("Undeclared public route:", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task FrameworkConditionalRoutesAreDiscovered()
    {
        const string source = """
            namespace Respire;
            public class Routes {
            #if NET8_0
                public ValueTask<int> EightAsync() => default;
            #else
                public ValueTask<int> TenAsync() => default;
            #endif
            }
            """;
        var methods = CommandRouteOwnership.Discover([("fixture.cs", source)]).Where(m => m.PublicRoute).Select(m => m.Name).ToArray();
        await Assert.That(methods).Contains("EightAsync");
        await Assert.That(methods).Contains("TenAsync");
    }

    [Test]
    [Arguments("NET")]
    [Arguments("NETCOREAPP")]
    [Arguments("NETCOREAPP1_0_OR_GREATER")]
    [Arguments("NETCOREAPP1_1_OR_GREATER")]
    [Arguments("NETCOREAPP2_0_OR_GREATER")]
    [Arguments("NETCOREAPP2_1_OR_GREATER")]
    [Arguments("NETCOREAPP2_2_OR_GREATER")]
    [Arguments("NETCOREAPP3_0_OR_GREATER")]
    [Arguments("NETCOREAPP3_1_OR_GREATER")]
    [Arguments("NET5_0_OR_GREATER")]
    [Arguments("NET6_0_OR_GREATER")]
    [Arguments("NET7_0_OR_GREATER")]
    [Arguments("NET8_0_OR_GREATER")]
    public async Task SdkCompatibilitySymbolsCannotHideUndeclaredRoutes(string symbol)
    {
        var source = CommandRouteOwnership.Discover([("fixture.cs", $$"""
            public class Routes {
            #if {{symbol}}
                public int Added() => 1;
            #endif
            }
            """)]);
        var errors = CommandRouteOwnership.Validate(source, new([], []));
        await Assert.That(errors).Contains("Undeclared public route: Routes.Added():int [net8.0]");
        await Assert.That(errors).Contains("Undeclared public route: Routes.Added():int [net10.0]");
        var inventory = new CommandRouteOwnership.Inventory([
            new("Routes", "Routes", ["Added():int"], Contract: "Own calls through completion.")], []);
        await Assert.That(CommandRouteOwnership.Validate(source, inventory)).IsEmpty();
    }

    [Test]
    [Arguments("NET8_0", "net8.0")]
    [Arguments("NET10_0", "net10.0")]
    [Arguments("NET9_0_OR_GREATER", "net10.0")]
    [Arguments("NET10_0_OR_GREATER", "net10.0")]
    public async Task SdkTargetSymbolsStayWithinTheirFramework(string symbol, string framework)
    {
        var source = CommandRouteOwnership.Discover([("fixture.cs", $$"""
            public class Routes {
            #if {{symbol}}
                public int Added() => 1;
            #endif
                private void Probe() { }
            }
            """)]);
        await Assert.That(source.Where(m => m.PublicRoute).Single().Framework).IsEqualTo(framework);
        await Assert.That(CommandRouteOwnership.Validate(source, new([], [])))
            .Contains("Undeclared public route: Routes.Added():int [" + framework + "]");
    }

    [Test]
    public async Task SynchronousMethodsOnUnfamiliarTypesRequireClassification()
    {
        var source = Discover("public class NovelSurface { public int Foo() => 1; public bool TryX(int key) => true; }");
        await Assert.That(source.All(m => m.PublicRoute)).IsTrue();
        await Assert.That(CommandRouteOwnership.Validate(source, new([], [])).Length).IsEqualTo(2);
        await Assert.That(source.Any(m => m.Id.StartsWith(".", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task ExplicitNonRoutesRequireExistingPublicMethodsAndReasons()
    {
        var source = Discover("public class Value { public int GetHashCode() => 1; }");
        var inventory = new CommandRouteOwnership.Inventory([], [], [new(source.Single().Id, "Local value comparison; no dispatch.")]);
        await Assert.That(CommandRouteOwnership.Validate(source, inventory)).IsEmpty();
        await Assert.That(CommandRouteOwnership.Validate(source, inventory with { NonRoutes = [new(source.Single().Id, "")] }))
            .Contains("Missing non-route reason: Value.GetHashCode():int [net8.0]");
        await Assert.That(CommandRouteOwnership.Validate([], inventory))
            .Contains("Removed or non-public exclusion: Value.GetHashCode():int");
    }

    [Test]
    public async Task UnrelatedOwnerWithMatchingMethodCannotImplementContract()
    {
        await Assert.That(CommandRouteOwnership.Validate(Discover(Fixture.Replace(" : IExampleCommands", "")), ControlInventory())
            .Any(e => e.StartsWith("Owner does not implement public contract:", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task PrivateLookalikeCannotImplementPublicContract()
    {
        await Assert.That(CommandRouteOwnership.Validate(Discover(Fixture.Replace("public ValueTask<int> GetAsync", "private ValueTask<int> GetAsync")), ControlInventory())
            .Any(e => e.StartsWith("Owner does not implement public contract:", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task StaticLookalikeCannotImplementInstanceContract()
    {
        var source = Fixture.Replace("public ValueTask<int> GetAsync", "public static ValueTask<int> GetAsync");
        await Assert.That(CommandRouteOwnership.Validate(Discover(source), ControlInventory())
            .Any(e => e.StartsWith("Owner does not implement public contract:", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    [Arguments("", false)]
    [Arguments(", IExampleCommands", true)]
    public async Task HidingMethodImplementsOnlyReimplementedContracts(string reimplementation, bool implements)
    {
        var source = Discover("""
            namespace Respire;
            public interface IExampleCommands { ValueTask<int> GetAsync(int key); }
            internal class BaseCommands : IExampleCommands { public ValueTask<int> GetAsync(int key) => default; }
            """ + "internal class ExampleCommands : BaseCommands" + reimplementation
            + " { public new ValueTask<int> GetAsync(int key) => default; }");
        await Assert.That(CommandRouteOwnership.Validate(source, ControlInventory()).Length == 0).IsEqualTo(implements);
    }

    [Test]
    [Arguments("protected")]
    [Arguments("protected internal")]
    [Arguments("private protected")]
    public async Task ProtectedInterfaceMembersAreNotPublicRoutes(string visibility)
    {
        var source = Discover("public interface Commands { " + visibility + " void Probe() { } void Run(); }");
        await Assert.That(source.Single(m => m.Name == "Probe").PublicRoute).IsFalse();
        await Assert.That(source.Single(m => m.Name == "Run").PublicRoute).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PartialRoutesKeepEveryContributingSourceFile(bool reversed)
    {
        (string File, string Source)[] files = [
            ("declaration.cs", "public partial class Routes { public partial int Added(); }"),
            ("implementation.cs", "public partial class Routes { public partial int Added() => 1; }")];
        var member = Discover(reversed ? files.AsEnumerable().Reverse() : files);
        await Assert.That(member.Single().Files!).IsEquivalentTo(new[] { "declaration.cs", "implementation.cs" });
    }

    [Test]
    public async Task ExplicitImplementationIsNotAnImplicitPublicMethod()
    {
        var source = Fixture.Replace("public ValueTask<int> GetAsync", "ValueTask<int> IExampleCommands.GetAsync");
        await Assert.That(CommandRouteOwnership.Validate(Discover(source), ControlInventory())
            .Any(e => e.StartsWith("Missing final owner:", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task FrameworkBranchesKeepVisibilityAndBodiesSeparate()
    {
        var source = CommandRouteOwnership.Discover([("fixture.cs", """
            public class Routes {
            #if NET8_0
                public partial int Foo();
            #else
                private int Foo() => 1;
            #endif
            }
            """)]);
        var eight = source.Single(m => m.Framework == "net8.0");
        var ten = source.Single(m => m.Framework == "net10.0");
        await Assert.That(eight.PublicRoute).IsTrue();
        await Assert.That(eight.HasBody).IsFalse();
        await Assert.That(ten.PublicRoute).IsFalse();
        await Assert.That(ten.HasBody).IsTrue();
        await Assert.That(CommandRouteOwnership.Validate(source, new([], [])))
            .Contains("Undeclared public route: Routes.Foo():int [net8.0]");
    }

    [Test]
    public async Task DefaultInterfaceOwnerMustHaveBodyOnEveryTarget()
    {
        var source = CommandRouteOwnership.Discover([("fixture.cs", """
            namespace Respire;
            public interface IExampleCommands {
            #if NET8_0
                ValueTask<int> GetAsync(int key);
            #else
                ValueTask<int> GetAsync(int key) => default;
            #endif
            }
            """)]);
        var inventory = ControlInventory() with { Surfaces = [new("Respire.IExampleCommands", "Respire.IExampleCommands", [Route], Contract: "Control")] };
        await Assert.That(CommandRouteOwnership.Validate(source, inventory))
            .Contains("Missing final owner: Respire.IExampleCommands." + Route + " => Respire.IExampleCommands." + Route + " [net8.0]");
        await Assert.That(CommandRouteOwnership.Validate(source.Where(m => m.Framework == "net10.0").ToArray(), inventory)).IsEmpty();
    }

    [Test]
    public async Task FrameworkConditionalContractsCannotSupplyAnotherTargetsOwner()
    {
        var source = CommandRouteOwnership.Discover([("fixture.cs", """
            namespace Respire;
            public interface IExampleCommands { ValueTask<int> GetAsync(int key); }
            #if NET10_0
            internal class ExampleCommands : IExampleCommands {
            #else
            internal class ExampleCommands {
            #endif
                public ValueTask<int> GetAsync(int key) => default;
            }
            """)]);
        await Assert.That(CommandRouteOwnership.Validate(source, ControlInventory()))
            .Contains("Owner does not implement public contract: Respire.IExampleCommands." + Route + " => " + Owner + " [net8.0]");
        await Assert.That(CommandRouteOwnership.Validate(source.Where(m => m.Framework == "net10.0").ToArray(), ControlInventory())).IsEmpty();
    }

    [Test]
    public async Task FrameworkConditionalRouteDeclarationsApplyOnlyWherePublic()
    {
        var source = CommandRouteOwnership.Discover([("fixture.cs", """
            public class Routes {
            #if NET8_0
                public int Eight() => 8;
                private int Ten() => 10;
            #else
                public int Ten() => 10;
            #endif
            }
            """)]);
        var inventory = new CommandRouteOwnership.Inventory([
            new("Routes", "Routes", ["Eight():int", "Ten():int"], Contract: "Each target owns its public calls.")], []);
        await Assert.That(CommandRouteOwnership.Validate(source, inventory)).IsEmpty();
    }

    [Test]
    [Arguments("using Respire;", "IExampleCommands", "Implementation")]
    [Arguments("using Commands = Respire.IExampleCommands;", "Commands", "Implementation")]
    [Arguments("using Root = Respire;", "Root.IExampleCommands", "Implementation")]
    [Arguments("", "IExampleCommands", "Respire.Impl")]
    public async Task OwnersResolveImportedAndEnclosingNamespaceContracts(string imports, string contract, string ownerNamespace)
    {
        var source = CommandRouteOwnership.Discover([
            ("contract.cs", "namespace Respire; public interface IExampleCommands { ValueTask<int> GetAsync(int key); }"),
            ("owner.cs", imports + " namespace " + ownerNamespace + "; internal class ExampleCommands : " + contract
                + " { public ValueTask<int> GetAsync(int key) => default; }")]);
        var inventory = ControlInventory() with { Surfaces = [new("Respire.IExampleCommands", ownerNamespace + ".ExampleCommands", [Route], Contract: "Control")] };
        await Assert.That(CommandRouteOwnership.Validate(source, inventory)).IsEmpty();
    }

    [Test]
    public async Task TargetSpecificBoundariesKeepGlobalNameUniqueness()
    {
        var source = CommandRouteOwnership.Discover([("fixture.cs", """
            internal class Routes {
            #if NET8_0
                private void Eight() { }
            #else
                private void Ten() { }
            #endif
            }
            """)]);
        var eight = new CommandRouteOwnership.Boundary("probe", "internal", "Routes.Eight():void", null, "Control");
        var ten = eight with { Member = "Routes.Ten():void" };
        await Assert.That(CommandRouteOwnership.Validate(source, new([], [eight]))).IsEmpty();
        await Assert.That(CommandRouteOwnership.Validate(source, new([], [eight, ten])))
            .Contains("Duplicate boundary: probe");
    }

    private static readonly string[] RequiredBoundaries = [
        "dispatch-final-inspection", "dispatch-owner-start", "typed-dispatch-owner", "string-dispatch-owner", "bytes-dispatch-owner",
        "dispatch-write-outcome-borrower", "dispatch-discarded-reply-snapshot", "dispatch-cancelled-reply-drain",
        "dispatch-connect-borrower", "dispatch-gathered-admission",
        "native-pooled-inspection", "typed-converter-inspection", "string-result-inspection", "byte-result-inspection",
        "immediate-pooled-conversion", "pending-pooled-conversion", "raw-catalog-dispatch",
        "raw-dispatch-helper", "catalog-dispatch-helper", "interpolated-dispatch", "interpolated-helper",
        "connection-string-parsing", "connection-setup", "physical-connect-helper", "cache-producer", "cache-typed-producer",
        "cache-waiter", "cache-typed-waiter", "upload-payload-read", "download-payload-read", "fan-out", "fan-out-target",
        "upload-dispatch-owner", "upload-cluster-borrower", "upload-frame-borrower", "upload-discarded-completion",
        "download-payload-array-read", "download-payload-span-read", "download-payload-byte-read", "download-payload-legacy-read",
        "cache-aside-producer", "deferred-execution", "deferred-inspection", "deferred-awaiter", "transaction-commit",
        "caller-cleanup", "internal-health-probe", "internal-topology-query"
    ];
}
