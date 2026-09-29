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
    [Arguments(typeof(ISortedSetCommands), false)]
    [Arguments(typeof(IBatchSortedSetCommands), false)]
    [Arguments(typeof(IStreamCommands), false)]
    [Arguments(typeof(IStringCommands), true)]
    [Arguments(typeof(IBatchStringCommands), true)]
    public async Task CommandContracts_RequireImplementations(Type contract, bool onlyGenericMethods)
    {
        // Non-generic string aliases still have functional forwarding bodies.
        // These operations must instead be implemented explicitly by every client or decorator.
        var defaults = contract.GetMethods()
            .Where(method => (!onlyGenericMethods || method.IsGenericMethod) && !method.IsAbstract)
            .Select(method => method.ToString())
            .ToArray();

        await Assert.That(defaults).IsEmpty();
    }
}
