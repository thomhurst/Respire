using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClientCoreTests
{
    [Test]
    public async Task StateChange_IncludesSourceEndpointAndError()
    {
        await using var core = new ClientCore(new RespireOptions { Cluster = true });
        var endpoint = new RespireEndpoint("127.0.0.1", 6380);
        var node = core.Cluster!.GetMultiplexer(endpoint);
        var changes = new List<RespireConnectionStateChange>();
        core.ConnectionStateChanged += changes.Add;
        var error = new RespireConnectionException("connection lost");

        core.NotifyCommandStateChanged(node, 0, RespireConnectionState.Reconnecting, error);

        await Assert.That(changes).Count().IsEqualTo(1);
        await Assert.That(changes[0].Endpoint).IsEqualTo(endpoint);
        await Assert.That(changes[0].State).IsEqualTo(RespireConnectionState.Reconnecting);
        await Assert.That(changes[0].Error).IsSameReferenceAs(error);
    }

    [Test]
    public async Task ReconnectFailure_PublishesDisconnectedWithError()
    {
        await using var core = new ClientCore(new RespireOptions());
        var changes = new List<RespireConnectionStateChange>();
        core.ConnectionStateChanged += changes.Add;
        var error = new RespireConnectionException("reconnect failed");

        core.NotifyCommandStateChanged(0, RespireConnectionState.Reconnecting);
        core.NotifyCommandStateChanged(0, RespireConnectionState.Disconnected, error);

        await Assert.That(changes.Select(change => change.State)).IsEquivalentTo(
            [RespireConnectionState.Reconnecting, RespireConnectionState.Disconnected]);
        await Assert.That(changes[^1].Error).IsSameReferenceAs(error);
    }

    [Test]
    public async Task DisposeAsync_PublishesDisconnected()
    {
        var core = new ClientCore(new RespireOptions());
        var changes = new List<RespireConnectionStateChange>();
        core.ConnectionStateChanged += changes.Add;

        await core.DisposeAsync();

        await Assert.That(changes).Count().IsEqualTo(1);
        await Assert.That(changes[0].Endpoint).IsEqualTo(core.Options.PrimaryEndpoint);
        await Assert.That(changes[0].State).IsEqualTo(RespireConnectionState.Disconnected);
        await Assert.That(changes[0].Error).IsNull();
    }

    [Test]
    public async Task SubscriberRecovery_WaitsForCommandRecoveryBeforePublishingConnected()
    {
        await using var core = new ClientCore(new RespireOptions());
        var states = new List<RespireConnectionState>();
        core.ConnectionStateChanged += change => states.Add(change.State);

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
        core.ConnectionStateChanged += change => states.Add(change.State);

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
        core.ConnectionStateChanged += change => states.Add(change.State);

        core.NotifyCommandStateChanged(0, RespireConnectionState.Reconnecting);
        core.NotifyCommandStateChanged(1, RespireConnectionState.Reconnecting);
        core.NotifyCommandStateChanged(0, RespireConnectionState.Connected);

        await Assert.That(states).IsEquivalentTo([RespireConnectionState.Reconnecting]);

        core.NotifyCommandStateChanged(1, RespireConnectionState.Connected);

        await Assert.That(states).IsEquivalentTo(
            [RespireConnectionState.Reconnecting, RespireConnectionState.Connected]);
    }

    [Test]
    public async Task ClusterRecovery_PublishesEachEndpointTransition()
    {
        await using var core = new ClientCore(new RespireOptions { Cluster = true });
        var primaryEndpoint = core.Options.PrimaryEndpoint;
        var secondEndpoint = new RespireEndpoint("127.0.0.1", 6380);
        var secondNode = core.Cluster!.GetMultiplexer(secondEndpoint);
        var changes = new List<RespireConnectionStateChange>();
        core.ConnectionStateChanged += changes.Add;

        core.NotifyCommandStateChanged(core.Multiplexer, 0, RespireConnectionState.Reconnecting);
        core.NotifyCommandStateChanged(secondNode, 0, RespireConnectionState.Reconnecting);
        core.NotifyCommandStateChanged(core.Multiplexer, 0, RespireConnectionState.Connected);

        await Assert.That(changes.Select(change => (change.Endpoint, change.State))).IsEquivalentTo(
            [
                (primaryEndpoint, RespireConnectionState.Reconnecting),
                (secondEndpoint, RespireConnectionState.Reconnecting),
                (primaryEndpoint, RespireConnectionState.Connected),
            ]);

        core.NotifyCommandStateChanged(secondNode, 0, RespireConnectionState.Connected);

        await Assert.That(changes.Select(change => (change.Endpoint, change.State))).IsEquivalentTo(
            [
                (primaryEndpoint, RespireConnectionState.Reconnecting),
                (secondEndpoint, RespireConnectionState.Reconnecting),
                (primaryEndpoint, RespireConnectionState.Connected),
                (secondEndpoint, RespireConnectionState.Connected),
            ]);
    }

    [Test]
    public async Task ConcurrentTransitions_ArePublishedInAggregateOrder()
    {
        await using var core = new ClientCore(new RespireOptions());
        var states = new List<RespireConnectionState>();
        using var connectedStarted = new ManualResetEventSlim();
        using var releaseConnected = new ManualResetEventSlim();
        core.ConnectionStateChanged += change =>
        {
            var state = change.State;
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
