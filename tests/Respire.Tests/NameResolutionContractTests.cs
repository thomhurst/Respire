using System.Reflection;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class NameResolutionContractTests
{
    [Test]
    public async Task PrefixedViews_ExposePhysicalNamesThroughCapability()
    {
        await using var root = RespireClient.Create(new RespireOptions { Endpoints = [new("unused.invalid")] });
        IRespireClient view = root.WithKeyPrefix("keys:").WithPubSubPrefix("channels:");
        var resolver = (IRespireNameResolver)view;

        await Assert.That(resolver.ResolveKey("item")).IsEqualTo((RespireKey)"keys:item");
        await Assert.That(resolver.ResolveChannel("item")).IsEqualTo((RespireChannel)"channels:item");
    }

    [Test]
    public async Task IntegrationBridge_ForwardsDecoratorCapability()
    {
        await using var root = RespireClient.Create(new RespireOptions { Endpoints = [new("unused.invalid")] });
        IRespireClient view = root.WithKeyPrefix("keys:").WithPubSubPrefix("channels:");
        var decorator = DispatchProxy.Create<IResolvingClient, ResolvingClientProxy>();
        ((ResolvingClientProxy)(object)decorator).Resolver = (IRespireNameResolver)view;
        IRespireClient client = decorator;

        await Assert.That(client.ResolveKey("item")).IsEqualTo((RespireKey)"keys:item");
        await Assert.That(client.ResolveChannel("item")).IsEqualTo((RespireChannel)"channels:item");
    }

    [Test]
    public async Task IntegrationBridge_RejectsMissingCapabilityInsteadOfAssumingUnprefixedNames()
    {
        var client = DispatchProxy.Create<IRespireClient, ResolvingClientProxy>();
        await Assert.That(() => client.ResolveKey("item")).ThrowsExactly<RespireConfigurationException>();
        await Assert.That(() => client.ResolveChannel("item")).ThrowsExactly<RespireConfigurationException>();
    }

    public interface IResolvingClient : IRespireClient, IRespireNameResolver { }

    public class ResolvingClientProxy : DispatchProxy
    {
        public IRespireNameResolver Resolver { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod!.Invoke(Resolver, args);
    }
}
