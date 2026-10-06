using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class FacetInterfaceContractTests
{
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
    public async Task CommandContracts_RequireImplementations(Type contract)
    {
        var defaults = contract.GetMethods()
            .Where(method => !method.IsAbstract)
            .Select(method => method.ToString())
            .ToArray();

        await Assert.That(defaults).IsEmpty();
    }
}
