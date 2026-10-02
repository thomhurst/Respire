using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClientCoreTests
{
    [Test]
    public async Task ShardedPrimariesRetainIndependentHealthUntilEachRecovers()
    {
        await using var core = new ClientCore(new RespireOptions());
        var first = new RespireEndpoint("first", 6379);
        var second = new RespireEndpoint("second", 6379);
        var changes = new List<RespireConnectionStateChange>();
        core.ConnectionStateChanged += changes.Add;
        core.NotifySubscriptionStateChanged(new(first, RespireConnectionState.Reconnecting, null), clusterSharded: true);
        core.NotifySubscriptionStateChanged(new(second, RespireConnectionState.Reconnecting, null), clusterSharded: true);
        await Assert.That(changes.Any(change => change.State == RespireConnectionState.Connected)).IsFalse();
        core.NotifySubscriptionStateChanged(new(second, RespireConnectionState.Connected, null), clusterSharded: true);
        await Assert.That(changes.Count(change => change.Endpoint == first)).IsEqualTo(1);
        core.NotifySubscriptionStateChanged(new(first, RespireConnectionState.Disconnected, null), clusterSharded: true);
        core.NotifySubscriptionStateChanged(new(second, RespireConnectionState.Reconnecting, null), clusterSharded: true);
        await Assert.That(changes.Where(change => change.Endpoint == first).Select(change => change.State))
            .IsEquivalentTo([RespireConnectionState.Reconnecting, RespireConnectionState.Disconnected]);
        core.NotifySubscriptionStateChanged(new(first, RespireConnectionState.Connected, null), clusterSharded: true);
        await Assert.That(changes.Last().Endpoint).IsEqualTo(first);
        await Assert.That(changes.Last().State).IsEqualTo(RespireConnectionState.Connected);
    }

    [Test]
    public async Task RecoveredShardedEndpointReceivesTerminalStateOnDisposal()
    {
        // An ASK target is not an active Cluster command endpoint, so disposal must learn
        // about it from sharded subscription health even after that endpoint recovered.
        var core = new ClientCore(new RespireOptions());
        var target = new RespireEndpoint("ask-target", 7000);
        var changes = new List<RespireConnectionStateChange>();
        core.ConnectionStateChanged += changes.Add;
        core.NotifySubscriptionStateChanged(new(target, RespireConnectionState.Reconnecting, null), clusterSharded: true);
        core.NotifySubscriptionStateChanged(new(target, RespireConnectionState.Connected, null), clusterSharded: true);
        await core.DisposeAsync();
        await Assert.That(changes.Where(change => change.Endpoint == target).Select(change => change.State))
            .IsEquivalentTo([RespireConnectionState.Reconnecting, RespireConnectionState.Connected, RespireConnectionState.Disconnected]);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PubSubGroupsCannotOverwriteEachOthersFailure(bool failedSharded)
    {
        await using var core = new ClientCore(new RespireOptions());
        var endpoint = core.Options.PrimaryEndpoint;
        var changes = new List<RespireConnectionStateChange>();
        core.ConnectionStateChanged += changes.Add;
        core.NotifySubscriptionStateChanged(new(endpoint, RespireConnectionState.Disconnected, null), failedSharded);
        core.NotifySubscriptionStateChanged(new(endpoint, RespireConnectionState.Reconnecting, null), !failedSharded);
        core.NotifySubscriptionStateChanged(new(endpoint, RespireConnectionState.Connected, null), !failedSharded);
        await Assert.That(changes.Select(change => change.State)).IsEquivalentTo([RespireConnectionState.Disconnected]);
        core.NotifySubscriptionStateChanged(new(endpoint, RespireConnectionState.Connected, null), failedSharded);
        await Assert.That(changes.Select(change => change.State))
            .IsEquivalentTo([RespireConnectionState.Disconnected, RespireConnectionState.Connected]);
    }

    [Test]
    public async Task StateChange_IncludesSourceEndpointAndError()
    {
        await using var core = new ClientCore(new RespireOptions { Protocol = RespProtocol.Resp2, UseCluster = true });
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
    public async Task DisposeAsync_DisconnectsActualSubscriptionEndpoint()
    {
        var core = new ClientCore(new RespireOptions { Protocol = RespProtocol.Resp2, UseCluster = true });
        var subscriptionEndpoint = new RespireEndpoint("127.0.0.1", 6380);
        var changes = new List<RespireConnectionStateChange>();
        core.ConnectionStateChanged += changes.Add;
        core.NotifySubscriptionStateChanged(new RespireConnectionStateChange(
            subscriptionEndpoint, RespireConnectionState.Reconnecting, null));

        await core.DisposeAsync();

        await Assert.That(changes.Select(change => (change.Endpoint, change.State))).IsEquivalentTo(
            [
                (subscriptionEndpoint, RespireConnectionState.Reconnecting),
                (subscriptionEndpoint, RespireConnectionState.Disconnected),
                (core.Options.PrimaryEndpoint, RespireConnectionState.Disconnected),
            ]);
    }

    [Test]
    public async Task DisposeAsync_DisconnectsEveryActiveClusterEndpoint()
    {
        var core = new ClientCore(new RespireOptions { Protocol = RespProtocol.Resp2, UseCluster = true });
        var primaryEndpoint = core.Options.PrimaryEndpoint;
        var secondEndpoint = new RespireEndpoint("127.0.0.1", 6380);
        var secondNode = core.Cluster!.GetMultiplexer(secondEndpoint);
        var changes = new List<RespireConnectionStateChange>();
        core.ConnectionStateChanged += changes.Add;
        core.NotifyCommandStateChanged(secondNode, 0, RespireConnectionState.Reconnecting);
        core.NotifyCommandStateChanged(secondNode, 0, RespireConnectionState.Connected);
        changes.Clear();

        await core.DisposeAsync();

        await Assert.That(changes.Select(change => (change.Endpoint, change.State))).IsEquivalentTo(
            [
                (primaryEndpoint, RespireConnectionState.Disconnected),
                (secondEndpoint, RespireConnectionState.Disconnected),
            ]);
    }

    [Test]
    public async Task DisposeAsync_DisconnectsRetainedClusterNotificationEndpoint()
    {
        var core = new ClientCore(new RespireOptions { Protocol = RespProtocol.Resp2, UseCluster = true });
        var endpoint = new RespireEndpoint("retained-primary", 7000);
        var changes = new List<RespireConnectionStateChange>();
        var reconnecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        core.ConnectionStateChanged += change =>
        {
            changes.Add(change);
            if (change.Endpoint == endpoint && change.State == RespireConnectionState.Reconnecting)
                reconnecting.TrySetResult();
        };
        core.NotifyClusterSubscriptionStateChanged(new(endpoint, RespireConnectionState.Reconnecting, null));
        await reconnecting.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await core.DisposeAsync();

        await Assert.That(changes.Any(change =>
            change.Endpoint == endpoint && change.State == RespireConnectionState.Disconnected)).IsTrue();
    }

    [Test]
    public async Task DisposeEvent_ObservesClientAsDisconnected()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        var client = await FakeRespServer.ConnectClientAsync(server.Port);
        bool? isConnectedDuringEvent = null;
        client.ConnectionStateChanged += change =>
        {
            if (change.State == RespireConnectionState.Disconnected)
            {
                isConnectedDuringEvent = client.IsConnected;
            }
        };

        await client.DisposeAsync();

        await Assert.That(isConnectedDuringEvent).IsFalse();
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
    public async Task SubscriberState_UsesReportedEndpoint()
    {
        await using var core = new ClientCore(new RespireOptions { Protocol = RespProtocol.Resp2, UseCluster = true });
        var endpoint = new RespireEndpoint("127.0.0.1", 6380);
        var changes = new List<RespireConnectionStateChange>();
        core.ConnectionStateChanged += changes.Add;

        core.NotifySubscriptionStateChanged(new RespireConnectionStateChange(
            endpoint, RespireConnectionState.Reconnecting, null));
        core.NotifySubscriptionStateChanged(new RespireConnectionStateChange(
            endpoint, RespireConnectionState.Connected, null));

        await Assert.That(changes.Select(change => (change.Endpoint, change.State))).IsEquivalentTo(
            [
                (endpoint, RespireConnectionState.Reconnecting),
                (endpoint, RespireConnectionState.Connected),
            ]);
    }

    [Test]
    public async Task SubscriberRecoveryToNewEndpoint_ClearsPreviousEndpoint()
    {
        await using var core = new ClientCore(new RespireOptions { Protocol = RespProtocol.Resp2, UseCluster = true });
        var previousEndpoint = core.Options.PrimaryEndpoint;
        var replacementEndpoint = new RespireEndpoint("127.0.0.1", 6380);
        var changes = new List<RespireConnectionStateChange>();
        core.ConnectionStateChanged += changes.Add;

        core.NotifySubscriptionStateChanged(new RespireConnectionStateChange(
            previousEndpoint, RespireConnectionState.Reconnecting, null));
        core.NotifySubscriptionStateChanged(new RespireConnectionStateChange(
            replacementEndpoint, RespireConnectionState.Connected, null));

        await Assert.That(changes.Select(change => (change.Endpoint, change.State))).IsEquivalentTo(
            [
                (previousEndpoint, RespireConnectionState.Reconnecting),
                (previousEndpoint, RespireConnectionState.Connected),
            ]);
    }

    [Test]
    public async Task CommandRecovery_WaitsForEveryReconnectingSlot()
    {
        await using var core = new ClientCore(new RespireOptions { Protocol = RespProtocol.Resp2, Connections = 2 });
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
        await using var core = new ClientCore(new RespireOptions { Protocol = RespProtocol.Resp2, UseCluster = true });
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
