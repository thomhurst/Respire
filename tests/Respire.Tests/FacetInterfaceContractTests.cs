using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class FacetInterfaceContractTests
{
    private static readonly HashSet<(Type Contract, string Name)> ForwardingAliases =
    [
        (typeof(IStringCommands), nameof(IStringCommands.GetAndDeleteAsync)),
        (typeof(IStringCommands), nameof(IStringCommands.GetAndExpireAsync)),
        (typeof(IBatchStringCommands), nameof(IBatchStringCommands.GetAndDelete)),
        (typeof(IBatchStringCommands), nameof(IBatchStringCommands.GetAndExpire)),
    ];

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
    [Arguments(typeof(ISortedSetCommands))]
    [Arguments(typeof(IBatchSortedSetCommands))]
    [Arguments(typeof(IStreamCommands))]
    [Arguments(typeof(IStringCommands))]
    [Arguments(typeof(IBatchStringCommands))]
    public async Task CommandContracts_RequireImplementations(Type contract)
    {
        // Only these named, non-generic aliases may retain functional forwarding bodies.
        var defaults = contract.GetMethods()
            .Where(method => !method.IsAbstract &&
                (method.IsGenericMethod || !ForwardingAliases.Contains((contract, method.Name))))
            .Select(method => method.ToString())
            .ToArray();

        await Assert.That(defaults).IsEmpty();
    }
}
