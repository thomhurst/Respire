using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClientCoreTests
{
    [Test]
    public async Task SubscriberRecovery_WaitsForCommandRecoveryBeforePublishingConnected()
    {
        await using var core = new ClientCore(new RespireOptions());
        var states = new List<RespireConnectionState>();
        core.ConnectionStateChanged += states.Add;

        core.NotifyCommandStateChanged(0, RespireConnectionState.Reconnecting);
        core.NotifySubscriptionStateChanged(RespireConnectionState.Reconnecting);
        core.NotifySubscriptionStateChanged(RespireConnectionState.Connected);

        await Assert.That(states).IsEquivalentTo([RespireConnectionState.Reconnecting]);

        core.NotifyCommandStateChanged(0, RespireConnectionState.Connected);

        await Assert.That(states).IsEquivalentTo(
            [RespireConnectionState.Reconnecting, RespireConnectionState.Connected]);
    }

    [Test]
    public async Task CommandRecovery_WaitsForSubscriberRecoveryBeforePublishingConnected()
    {
        await using var core = new ClientCore(new RespireOptions());
        var states = new List<RespireConnectionState>();
        core.ConnectionStateChanged += states.Add;

        core.NotifySubscriptionStateChanged(RespireConnectionState.Reconnecting);
        core.NotifyCommandStateChanged(0, RespireConnectionState.Reconnecting);
        core.NotifyCommandStateChanged(0, RespireConnectionState.Connected);

        await Assert.That(states).IsEquivalentTo([RespireConnectionState.Reconnecting]);

        core.NotifySubscriptionStateChanged(RespireConnectionState.Connected);

        await Assert.That(states).IsEquivalentTo(
            [RespireConnectionState.Reconnecting, RespireConnectionState.Connected]);
    }

    [Test]
    public async Task CommandRecovery_WaitsForEveryReconnectingSlot()
    {
        await using var core = new ClientCore(new RespireOptions { Connections = 2 });
        var states = new List<RespireConnectionState>();
        core.ConnectionStateChanged += states.Add;

        core.NotifyCommandStateChanged(0, RespireConnectionState.Reconnecting);
        core.NotifyCommandStateChanged(1, RespireConnectionState.Reconnecting);
        core.NotifyCommandStateChanged(0, RespireConnectionState.Connected);

        await Assert.That(states).IsEquivalentTo([RespireConnectionState.Reconnecting]);

        core.NotifyCommandStateChanged(1, RespireConnectionState.Connected);

        await Assert.That(states).IsEquivalentTo(
            [RespireConnectionState.Reconnecting, RespireConnectionState.Connected]);
    }

    [Test]
    public async Task ClusterRecovery_WaitsForEveryReconnectingNode()
    {
        await using var core = new ClientCore(new RespireOptions { Cluster = true });
        var secondNode = core.Cluster!.GetMultiplexer(new RespireEndpoint("127.0.0.1", 6380));
        var states = new List<RespireConnectionState>();
        core.ConnectionStateChanged += states.Add;

        core.NotifyCommandStateChanged(core.Multiplexer, 0, RespireConnectionState.Reconnecting);
        core.NotifyCommandStateChanged(secondNode, 0, RespireConnectionState.Reconnecting);
        core.NotifyCommandStateChanged(core.Multiplexer, 0, RespireConnectionState.Connected);

        await Assert.That(states).IsEquivalentTo([RespireConnectionState.Reconnecting]);

        core.NotifyCommandStateChanged(secondNode, 0, RespireConnectionState.Connected);

        await Assert.That(states).IsEquivalentTo(
            [RespireConnectionState.Reconnecting, RespireConnectionState.Connected]);
    }

    [Test]
    public async Task ConcurrentTransitions_ArePublishedInAggregateOrder()
    {
        await using var core = new ClientCore(new RespireOptions());
        var states = new List<RespireConnectionState>();
        using var connectedStarted = new ManualResetEventSlim();
        using var releaseConnected = new ManualResetEventSlim();
        core.ConnectionStateChanged += state =>
        {
            if (state == RespireConnectionState.Connected)
            {
                connectedStarted.Set();
                releaseConnected.Wait(TimeSpan.FromSeconds(5));
            }

            lock (states)
            {
                states.Add(state);
            }
        };
        core.NotifyCommandStateChanged(0, RespireConnectionState.Reconnecting);

        var commandRecovery = Task.Run(
            () => core.NotifyCommandStateChanged(0, RespireConnectionState.Connected));
        await Assert.That(await Task.Run(() => connectedStarted.Wait(TimeSpan.FromSeconds(5)))).IsTrue();
        core.NotifySubscriptionStateChanged(RespireConnectionState.Reconnecting);
        releaseConnected.Set();
        await commandRecovery;

        await Assert.That(states).Count().IsEqualTo(3);
        await Assert.That(states[^1]).IsEqualTo(RespireConnectionState.Reconnecting);
    }
}
