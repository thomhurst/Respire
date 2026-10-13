using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class FacetInterfaceContractTests
{
    [Test]
    public async Task PublicInterfaces_HaveNoUnsupportedDefaults()
    {
        var sourceRoot = Path.Combine(CommandRouteOwnership.FindRepository(), "src");
        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (Path.GetRelativePath(sourceRoot, file).Split(Path.DirectorySeparatorChar)
                .Any(part => part is "bin" or "obj")) continue;
            var source = File.ReadAllText(file);
            if (!source.Contains("interface") || !source.Contains("NotSupportedException")) continue;
            var root = CSharpSyntaxTree.ParseText(source).GetRoot();
            foreach (var contract in root.DescendantNodes().OfType<InterfaceDeclarationSyntax>()
                .Where(contract => contract.Modifiers.Any(SyntaxKind.PublicKeyword)))
            {
                if (contract.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
                    .Any(creation => creation.Type.ToString().Split('.').Last() == nameof(NotSupportedException)))
                    violations.Add($"{Path.GetRelativePath(sourceRoot, file)}: {contract.Identifier}");
            }
        }

        await Assert.That(violations).IsEmpty();
    }

    [Test]
    [Arguments(typeof(IRespireClient), "get_Arrays")]
    [Arguments(typeof(IRespireClient), "get_Functions")]
    [Arguments(typeof(IRespireClient), "GetOrSetAsync")]
    [Arguments(typeof(IRespireClient), "WithKeyPrefix")]
    [Arguments(typeof(IRespireClient), "WithPubSubPrefix")]
    [Arguments(typeof(IRespireCommandQueue), "get_Server")]
    [Arguments(typeof(IRespireCommandQueue), "get_Arrays")]
    [Arguments(typeof(IRespireCommandQueue), "get_Functions")]
    [Arguments(typeof(IBatchScriptCommands), "Load")]
    [Arguments(typeof(IBatchScriptCommands), "Exists")]
    [Arguments(typeof(IBatchScriptCommands), "Flush")]
    [Arguments(typeof(IScriptCommands), "FlushAsync")]
    [Arguments(typeof(IKeyCommands), "ScanValkeyClusterPageAsync")]
    [Arguments(typeof(IBatchKeyCommands), "ScanValkeyClusterPage")]
    public async Task Capabilities_RequireExplicitImplementations(Type contract, string name)
    {
        var methods = contract.GetMethods().Where(method => method.Name == name).ToArray();
        await Assert.That(methods).IsNotEmpty();
        await Assert.That(methods.All(method => method.IsAbstract)).IsTrue();
    }

    [Test]
    public async Task ScriptCacheExistsCore_RequiresImplementation()
    {
        var method = typeof(IScriptCommands).GetMethod(nameof(IScriptCommands.ExistsAsync),
            [typeof(ReadOnlySpan<string>), typeof(CancellationToken)])!;
        await Assert.That(method.IsAbstract).IsTrue();
    }

    [Test]
    public async Task ClientContract_DoesNotExposePhysicalNameResolution()
    {
        await Assert.That(typeof(IRespireClient).GetMethod("ResolveKey")).IsNull();
        await Assert.That(typeof(IRespireClient).GetMethod("ResolveChannel")).IsNull();
    }

    [Test]
    public async Task SubscriptionOptions_RequireExplicitImplementations()
    {
        var methods = typeof(IRespireClient).GetMethods()
            .Where(method => method.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(RespireSubscriptionOptions)))
            .ToArray();

        await Assert.That(methods).IsNotEmpty();
        await Assert.That(methods.All(method => method.IsAbstract)).IsTrue();
    }

    [Test]
    [Arguments(typeof(IServerCommands))]
    [Arguments(typeof(IBatchServerCommands))]
    [Arguments(typeof(IHashCommands))]
    [Arguments(typeof(IArrayCommands))]
    [Arguments(typeof(IBatchArrayCommands))]
    [Arguments(typeof(IBatchHashCommands))]
    [Arguments(typeof(ISortedSetCommands))]
    [Arguments(typeof(IBatchSortedSetCommands))]
    [Arguments(typeof(IStreamCommands))]
    [Arguments(typeof(IStringCommands))]
    [Arguments(typeof(IBatchStringCommands))]
    [Arguments(typeof(IRespireNameResolver))]
    [Arguments(typeof(IRespireClientSideCache))]
    [Arguments(typeof(IRespireCommandQueue))]
    [Arguments(typeof(IBatchScriptCommands))]
    [Arguments(typeof(IBatchKeyCommands))]
    public async Task CommandContracts_RequireImplementations(Type contract)
    {
        var defaults = contract.GetMethods()
            .Where(method => !method.IsAbstract)
            .Select(method => method.ToString())
            .ToArray();

        await Assert.That(defaults).IsEmpty();
    }
}
