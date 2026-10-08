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
        internal class ExampleCommands {
            public ValueTask<int> GetAsync(int key) => default;
            private ValueTask<int> RetryAsync(int key) => default;
            private void Probe() { }
        }
        """;
    private const string Route = "GetAsync(int):ValueTask<int>";
    private const string Owner = "Respire.ExampleCommands." + Route;
    private static CommandRouteOwnership.Member[] Discover(string source)
        => CommandRouteOwnership.Discover([("fixture.cs", source)]);
    private static CommandRouteOwnership.Inventory ControlInventory(params CommandRouteOwnership.Boundary[] boundaries)
        => new([new("Respire.IExampleCommands", "Respire.ExampleCommands", [Route], Contract: "Own validation through cleanup.")], boundaries);

    [Test]
    public async Task SourceRoutesHaveDeclaredExecutableFinalOwners()
    {
        var repo = CommandRouteOwnership.FindRepository();
        var files = Directory.EnumerateFiles(Path.Combine(repo, "src", "Respire"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(repo, f).Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj"))
            .Select(f => (File: Path.GetRelativePath(repo, f), Source: File.ReadAllText(f)));
        var source = CommandRouteOwnership.Discover(files);
        using var stream = typeof(CommandRouteOwnershipTests).Assembly.GetManifestResourceStream("Respire.Tests.CommandRouteOwners.json")!;
        var inventory = JsonSerializer.Deserialize<CommandRouteOwnership.Inventory>(stream)!;
        var errors = CommandRouteOwnership.Validate(source, inventory);
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
        var source = Fixture + " internal class OtherCommands { public ValueTask<int> GetAsync(int key) => default; }";
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
            ("second.cs", "namespace Respire; partial class Routes { public ValueTask<int> AddedAsync() => default; }")]);
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
        var methods = Discover(source).Where(m => m.PublicRoute).Select(m => m.Name).ToArray();
        await Assert.That(methods).Contains("EightAsync");
        await Assert.That(methods).Contains("TenAsync");
    }

    private static readonly string[] RequiredBoundaries = [
        "native-pooled-inspection", "typed-converter-inspection", "byte-result-inspection", "raw-catalog-dispatch",
        "raw-dispatch-helper", "catalog-dispatch-helper", "interpolated-dispatch", "interpolated-helper",
        "connection-string-parsing", "connection-setup", "physical-connect-helper", "cache-producer", "cache-typed-producer",
        "cache-waiter", "cache-typed-waiter", "upload-payload-read", "download-payload-read", "fan-out", "fan-out-target",
        "cache-aside-producer", "deferred-execution", "deferred-inspection", "deferred-awaiter", "transaction-commit",
        "caller-cleanup", "internal-health-probe", "internal-topology-query"
    ];
}
