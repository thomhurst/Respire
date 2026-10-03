using System.Diagnostics.Metrics;
using System.Text;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClusterNotificationRoutingTests
{
    private static readonly byte[] Hello = "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray();
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AllPrimariesPatternWaitsForEveryAckAndMergesMessages(bool resp3)
    {
        await using var first = new FakeRespServer(20);
        await using var second = new FakeRespServer(20);
        await using var third = new FakeRespServer(20);
        var topology = Topology(first.Port, second.Port, third.Port);
        Configure(first, topology, resp3);
        Configure(second, topology, resp3);
        Configure(third, topology, resp3);
        await using var client = RespireClient.Create(new RespireOptions
        {
            UseCluster = true,
            Protocol = resp3 ? RespProtocol.Resp3 : RespProtocol.Resp2,
            Connections = 1,
            Endpoints = [new("127.0.0.1", first.Port)],
        });

        var descriptor = RespireChannel.KeySpacePrefix("tenant:", 0);
        var thirdAckPending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        third.SuppressReply = command =>
        {
            if (!command.StartsWith("PSUBSCRIBE ", StringComparison.Ordinal)) return false;
            thirdAckPending.TrySetResult();
            return true;
        };
        var activation = client.SubscribeAsync(descriptor).AsTask();
        await thirdAckPending.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(activation.IsCompleted).IsFalse();
        var ackCommandIndex = third.ReceivedCommands.ToList()
            .FindIndex(command => command == $"PSUBSCRIBE {descriptor}");
        await third.SendRawAsync(Confirmation("psubscribe", descriptor.ToString(), resp3),
            third.ReceivedConnectionIds[ackCommandIndex]);
        await using var subscription = await activation.WaitAsync(TimeSpan.FromSeconds(10));
        foreach (var server in new[] { first, second, third })
        {
            await Assert.That(server.ReceivedCommands).Contains($"PSUBSCRIBE {descriptor}");
        }

        foreach (var server in new[] { first, second, third })
        {
            var commandIndex = server.ReceivedCommands.ToList().FindIndex(command => command == $"PSUBSCRIBE {descriptor}");
            var connectionId = server.ReceivedConnectionIds[commandIndex];
            await server.SendRawAsync(Data(descriptor, resp3, "set"), connectionId);
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var reader = subscription.GetAsyncEnumerator(deadline.Token);
        for (var index = 0; index < 3; index++)
        {
            await Assert.That(await reader.MoveNextAsync()).IsTrue();
            await Assert.That(reader.Current.TryParseKeyNotification(out var notification)).IsTrue();
            await Assert.That(notification.Type).IsEqualTo(RespireKeyNotificationType.Set);
        }
    }

    [Test]
    public async Task ExactKeyUsesOnlySlotOwnerAndOrdinaryPubSubStaysSingleConnection()
    {
        await using var first = new FakeRespServer(20);
        await using var second = new FakeRespServer(20);
        await using var third = new FakeRespServer(20);
        var topology = Topology(first.Port, second.Port, third.Port);
        Configure(first, topology, resp3: false);
        Configure(second, topology, resp3: false);
        Configure(third, topology, resp3: false);
        await using var client = RespireClient.Create(new RespireOptions
        {
            UseCluster = true,
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            Endpoints = [new("127.0.0.1", first.Port)],
        });

        var key = Enumerable.Range(0, 100_000).Select(static index => $"tenant:{index}")
            .First(static value => ClusterHash.GetSlot(value) is >= 5461 and <= 10922);
        var descriptor = RespireChannel.KeySpaceSingleKey(key, 0);
        await using var exact = await client.SubscribeAsync(descriptor).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await using var ordinary = await client.SubscribeAsync("application-channel").AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(first.ReceivedCommands).DoesNotContain($"SUBSCRIBE {descriptor}");
        await Assert.That(second.ReceivedCommands).Contains($"SUBSCRIBE {descriptor}");
        await Assert.That(third.ReceivedCommands).DoesNotContain($"SUBSCRIBE {descriptor}");
        var ordinaryCount = new[] { first, second, third }
            .Sum(static server => server.ReceivedCommands.Count(command => command == "SUBSCRIBE application-channel"));
        await Assert.That(ordinaryCount).IsEqualTo(1);
    }

    [Test]
    public async Task ValkeyClusterNotificationsSupportNonZeroDatabase()
    {
        await using var first = new FakeRespServer(20);
        await using var second = new FakeRespServer(20);
        var topology = Topology(first.Port, second.Port);
        Configure(first, topology, resp3: false);
        Configure(second, topology, resp3: false);
        await using var client = RespireClient.Create(new RespireOptions
        {
            UseCluster = true,
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            Database = 1,
            Endpoints = [new("127.0.0.1", first.Port)],
        });

        var key = Enumerable.Range(0, 100_000).Select(static index => $"tenant:{index}")
            .First(static value => ClusterHash.GetSlot(value) is >= 8192 and <= 16383);
        var descriptor = RespireChannel.KeySpaceSingleKey(key, 1);
        await using var subscription = await client.SubscribeAsync(descriptor).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(first.ReceivedCommands).DoesNotContain($"SUBSCRIBE {descriptor}");
        await Assert.That(second.ReceivedCommands).Contains($"SUBSCRIBE {descriptor}");
    }

    [Test]
    public async Task SharedNodeRouteUnsubscribesOnlyAfterLastLogicalSubscriber()
    {
        await using var first = new FakeRespServer(20);
        await using var second = new FakeRespServer(20);
        var topology = Topology(first.Port, second.Port);
        Configure(first, () => topology, resp3: false);
        Configure(second, () => topology, resp3: false);
        await using var client = CreateClusterClient(first.Port, resp3: false);
        var descriptor = RespireChannel.KeyEvent(RespireKeyNotificationType.Set, 0);

        var firstSubscription = await client.SubscribeAsync(descriptor).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        var secondSubscription = await client.SubscribeAsync(descriptor).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await using (firstSubscription)
        await using (secondSubscription)
        {
            foreach (var server in new[] { first, second })
                await Assert.That(server.ReceivedCommands.Count(command => command == $"SUBSCRIBE {descriptor}"))
                    .IsEqualTo(1);

            await firstSubscription.DisposeAsync();
            foreach (var server in new[] { first, second })
                await Assert.That(server.ReceivedCommands).DoesNotContain($"UNSUBSCRIBE {descriptor}");

            await secondSubscription.DisposeAsync();
            foreach (var server in new[] { first, second })
                await Assert.That(server.ReceivedCommands).Contains($"UNSUBSCRIBE {descriptor}");
        }
    }

    [Test]
    public async Task TopologyChangeAddsNewPrimaryBeforeRemovingRetiredPrimary()
    {
        await using var first = new FakeRespServer(20);
        await using var second = new FakeRespServer(20);
        await using var third = new FakeRespServer(20);
        var topology = Topology(first.Port, second.Port);
        var thirdSubscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstUnsubscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Configure(first, () => topology, resp3: false, command =>
        {
            if (command.StartsWith("PUNSUBSCRIBE ", StringComparison.Ordinal)) firstUnsubscribed.TrySetResult();
        });
        Configure(second, () => topology, resp3: false);
        Configure(third, () => topology, resp3: false, command =>
        {
            if (command.StartsWith("PSUBSCRIBE ", StringComparison.Ordinal)) thirdSubscribed.TrySetResult();
        });
        await using var client = CreateClusterClient(first.Port, resp3: false);
        var descriptor = RespireChannel.KeySpacePrefix("tenant:", 0);
        await using var subscription = await client.SubscribeAsync(descriptor).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        topology = Topology(second.Port, third.Port);
        _ = await client.Core.Cluster!.GetMasterConnectionsAsync(CancellationToken.None, discovery: null)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await Task.WhenAll(thirdSubscribed.Task, firstUnsubscribed.Task).WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(third.ReceivedCommands).Contains($"PSUBSCRIBE {descriptor}");
        await Assert.That(first.ReceivedCommands).Contains($"PUNSUBSCRIBE {descriptor}");
        await Assert.That(second.ReceivedCommands.Count(command => command == $"PSUBSCRIBE {descriptor}"))
            .IsEqualTo(1);
    }

    [Test]
    public async Task IncompleteTopologyDropsRemovedPrimaryFromNotificationCoverage()
    {
        await using var first = new FakeRespServer(20);
        await using var second = new FakeRespServer(20);
        var topology = Topology(first.Port, second.Port);
        var firstUnsubscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Configure(first, () => topology, resp3: false, command =>
        {
            if (command.StartsWith("PUNSUBSCRIBE ", StringComparison.Ordinal)) firstUnsubscribed.TrySetResult();
        });
        Configure(second, () => topology, resp3: false);
        await using var client = CreateClusterClient(first.Port, resp3: false);
        var descriptor = RespireChannel.KeySpacePrefix("tenant:", 0);
        await using var subscription = await client.SubscribeAsync(descriptor).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        topology = PartialTopology(second.Port);
        try
        {
            _ = await client.Core.Cluster!.GetMasterConnectionsAsync(CancellationToken.None, discovery: null)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (RespireConnectionException)
        {
            // An incomplete slot map is published for routing, then rejected for cluster-wide commands.
        }

        await firstUnsubscribed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(first.ReceivedCommands).Contains($"PUNSUBSCRIBE {descriptor}");
        await Assert.That(second.ReceivedCommands.Count(command => command == $"PSUBSCRIBE {descriptor}"))
            .IsEqualTo(1);
    }

    [Test]
    public async Task PointUpdateDoesNotRestoreZeroSlotPrimaryToAllPrimaryCoverage()
    {
        await using var first = new FakeRespServer(20);
        await using var second = new FakeRespServer(20);
        await using var third = new FakeRespServer(20);
        var topology = Topology(first.Port, second.Port);
        var firstUnsubscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Configure(first, () => topology, resp3: false, command =>
        {
            if (command.StartsWith("PUNSUBSCRIBE ", StringComparison.Ordinal)) firstUnsubscribed.TrySetResult();
        });
        Configure(second, () => topology, resp3: false);
        Configure(third, () => topology, resp3: false);
        await using var client = CreateClusterClient(first.Port, resp3: false);
        var descriptor = RespireChannel.KeySpacePrefix("tenant:", 0);
        await using var subscription = await client.SubscribeAsync(descriptor).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        topology = PartialTopology(second.Port);
        try
        {
            _ = await client.Core.Cluster!.GetMasterConnectionsAsync(CancellationToken.None, discovery: null)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (RespireConnectionException)
        {
            // Partial discovery is published before cluster-wide discovery reports the missing slots.
        }
        await firstUnsubscribed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var router = client.Core.Cluster!;
        router.SetSlotOwner(0, router.GetMultiplexer(new("127.0.0.1", third.Port)));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!third.ReceivedCommands.Contains($"PSUBSCRIBE {descriptor}")) await Task.Delay(10, deadline.Token);

        await Assert.That(first.ReceivedCommands.Count(command => command == $"PSUBSCRIBE {descriptor}")).IsEqualTo(1);
    }

    [Test]
    public async Task ExactKeyMovesToNewOwnerAfterTopologyRefresh()
    {
        await using var first = new FakeRespServer(20);
        await using var second = new FakeRespServer(20);
        var topology = SinglePrimaryTopology(first.Port);
        var movedSubscription = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldUnsubscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Configure(first, () => topology, resp3: false, command =>
        {
            if (command.StartsWith("UNSUBSCRIBE ", StringComparison.Ordinal)) oldUnsubscribed.TrySetResult();
        });
        Configure(second, () => topology, resp3: false, command =>
        {
            if (command.StartsWith("SUBSCRIBE __keyspace", StringComparison.Ordinal)) movedSubscription.TrySetResult();
        });
        await using var client = CreateClusterClient(first.Port, resp3: false);
        var descriptor = RespireChannel.KeySpaceSingleKey("tenant:key", 0);
        await using var subscription = await client.SubscribeAsync(descriptor).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        topology = SinglePrimaryTopology(second.Port);
        _ = await client.Core.Cluster!.GetMasterConnectionsAsync(CancellationToken.None, discovery: null)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await Task.WhenAll(movedSubscription.Task, oldUnsubscribed.Task).WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(second.ReceivedCommands).Contains($"SUBSCRIBE {descriptor}");
        await Assert.That(first.ReceivedCommands).Contains($"UNSUBSCRIBE {descriptor}");
    }

    [Test]
    public async Task MultiKeySubscriptionReconcilesOwnersWhenEndpointSetStaysSame()
    {
        await using var first = new FakeRespServer(20);
        await using var second = new FakeRespServer(20);
        var topology = Topology(first.Port, second.Port);
        Configure(first, () => topology, resp3: false);
        Configure(second, () => topology, resp3: false);
        await using var client = CreateClusterClient(first.Port, resp3: false);
        var firstKey = Enumerable.Range(0, 100_000).Select(static index => $"tenant:{index}")
            .First(static value => ClusterHash.GetSlot(value) <= 8191);
        var secondKey = Enumerable.Range(0, 100_000).Select(static index => $"tenant:{index}")
            .First(static value => ClusterHash.GetSlot(value) >= 8192);
        var firstChannel = RespireChannel.KeySpaceSingleKey(firstKey, 0);
        var secondChannel = RespireChannel.KeySpaceSingleKey(secondKey, 0);
        await using var subscription = await client.SubscribeAsync(new RespireChannel[] { firstChannel, secondChannel }, CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        topology = Topology(second.Port, first.Port);
        _ = await client.Core.Cluster!.GetMasterConnectionsAsync(CancellationToken.None, discovery: null)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!first.ReceivedCommands.Contains($"SUBSCRIBE {secondChannel}")
            || !second.ReceivedCommands.Contains($"SUBSCRIBE {firstChannel}")
            || !first.ReceivedCommands.Contains($"UNSUBSCRIBE {firstChannel}")
            || !second.ReceivedCommands.Contains($"UNSUBSCRIBE {secondChannel}"))
            await Task.Delay(10, deadline.Token);
        await Assert.That(first.ReceivedCommands).Contains($"UNSUBSCRIBE {firstChannel}");
        await Assert.That(second.ReceivedCommands).Contains($"UNSUBSCRIBE {secondChannel}");
        var firstAdded = first.ReceivedCommands.ToList().FindIndex(command => command == $"SUBSCRIBE {secondChannel}");
        var firstRemoved = first.ReceivedCommands.ToList().FindIndex(command => command == $"UNSUBSCRIBE {firstChannel}");
        var secondAdded = second.ReceivedCommands.ToList().FindIndex(command => command == $"SUBSCRIBE {firstChannel}");
        var secondRemoved = second.ReceivedCommands.ToList().FindIndex(command => command == $"UNSUBSCRIBE {secondChannel}");
        await Assert.That(firstAdded < firstRemoved && secondAdded < secondRemoved).IsTrue()
            .Because($"Each endpoint must add its new route before removing its old route. "
                + $"First: {string.Join("; ", first.ReceivedCommands)}. Second: {string.Join("; ", second.ReceivedCommands)}");
    }

    [Test]
    public async Task OnePrimaryReconnectsWithoutStoppingHealthyPrimaryDelivery()
    {
        using var telemetryListener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Name is "respire.connection.reconnect.attempt" or "respire.pubsub.delivery.gaps")
                    meterListener.EnableMeasurementEvents(instrument);
            },
        };
        telemetryListener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            if (tags.ToArray().Any(tag => tag.Key == "respire.connection.source" && Equals(tag.Value, "pubsub")))
                throw new InvalidOperationException("Injected reconnect telemetry failure.");
            if (tags.ToArray().Any(tag => tag.Key == "respire.subscription.gap.reason"))
                throw new InvalidOperationException("Injected delivery-gap telemetry failure.");
        });
        telemetryListener.Start();
        await using var first = new FakeRespServer(20);
        await using var second = new FakeRespServer(20);
        await using var third = new FakeRespServer(20);
        var topology = Topology(first.Port, second.Port, third.Port);
        var reconnectSubscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdSubscribeCount = 0;
        Configure(first, topology, resp3: false);
        Configure(second, topology, resp3: false);
        Configure(third, () => topology, resp3: false, command =>
        {
            if (command.StartsWith("PSUBSCRIBE ", StringComparison.Ordinal)
                && Interlocked.Increment(ref thirdSubscribeCount) == 2)
                reconnectSubscribed.TrySetResult();
        });
        await using var client = CreateClusterClient(first.Port, resp3: false, reconnectPolicy: new RespireReconnectPolicy
        {
            InitialDelay = TimeSpan.FromMilliseconds(1),
            MaxDelay = TimeSpan.FromMilliseconds(1),
            JitterRatio = 0,
            MaxAttempts = 1,
        });
        var descriptor = RespireChannel.KeySpacePrefix("tenant:", 0);
        await using var subscription = await client.SubscribeAsync(descriptor).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        var gapObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var endpointReconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sawReconnect = 0;
        subscription.DeliveryGap += _ => gapObserved.TrySetResult();
        client.ConnectionStateChanged += change =>
        {
            if (change.Endpoint.Port != third.Port) return;
            if (change.State == RespireConnectionState.Reconnecting) Interlocked.Exchange(ref sawReconnect, 1);
            else if (change.State == RespireConnectionState.Connected && Volatile.Read(ref sawReconnect) != 0)
                endpointReconnected.TrySetResult();
        };
        var initialIndex = third.ReceivedCommands.ToList().FindIndex(command => command == $"PSUBSCRIBE {descriptor}");
        third.CloseConnection(third.ReceivedConnectionIds[initialIndex]);

        foreach (var server in new[] { first, second })
        {
            var index = server.ReceivedCommands.ToList().FindIndex(command => command == $"PSUBSCRIBE {descriptor}");
            await server.SendRawAsync(Data(descriptor, resp3: false, "set"), server.ReceivedConnectionIds[index]);
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var reader = subscription.GetAsyncEnumerator(deadline.Token);
        for (var index = 0; index < 2; index++)
        {
            await Assert.That(await reader.MoveNextAsync()).IsTrue();
            if (reader.Current.Kind == RespireMessageKind.Gap) index--;
            else await Assert.That(reader.Current.TryParseKeyNotification(out _)).IsTrue();
        }

        await reconnectSubscribed.Task.WaitAsync(deadline.Token);
        await gapObserved.Task.WaitAsync(deadline.Token);
        await endpointReconnected.Task.WaitAsync(deadline.Token);
        var reconnectIndex = third.ReceivedCommands.ToList().FindLastIndex(command => command == $"PSUBSCRIBE {descriptor}");
        await third.SendRawAsync(Data(descriptor, resp3: false, "set"), third.ReceivedConnectionIds[reconnectIndex]);
        while (!reader.Current.TryParseKeyNotification(out _))
            await Assert.That(await reader.MoveNextAsync()).IsTrue();
        await Assert.That(reader.Current.TryParseKeyNotification(out var restored)).IsTrue();
        await Assert.That(restored.Type).IsEqualTo(RespireKeyNotificationType.Set);
    }

    [Test]
    [NotInParallel] // The 100 ms recovery deadline must not compete with other wire fixtures during setup.
    public async Task ClusterNotificationRecoveryHonorsReconnectAttemptLimit()
    {
        await using var server = new FakeRespServer(20);
        var exhaustionMeasurements = 0;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Name == "respire.connection.reconnect.exhausted")
                    meterListener.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            var values = tags.ToArray();
            if (values.Any(tag => tag.Key == "respire.connection.source" && Equals(tag.Value, "pubsub"))
                && values.Any(tag => tag.Key == "server.port" && Equals(tag.Value, server.Port)))
                Interlocked.Increment(ref exhaustionMeasurements);
        });
        listener.Start();
        Configure(server, SinglePrimaryTopology(server.Port), resp3: false);
        await using var client = RespireClient.Create(new RespireOptions
        {
            UseCluster = true,
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            ConnectTimeout = TimeSpan.FromMilliseconds(100),
            CommandTimeout = TimeSpan.FromMilliseconds(100),
            ReconnectPolicy = new RespireReconnectPolicy
            {
                InitialDelay = TimeSpan.FromMilliseconds(1),
                MaxDelay = TimeSpan.FromMilliseconds(1),
                JitterRatio = 0,
                MaxAttempts = 1,
            },
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        var subscription = await client.SubscribeAsync(RespireChannel.KeyEvent(RespireKeyNotificationType.Set, 0))
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        var subscribeIndex = server.ReceivedCommands.ToList()
            .FindIndex(command => command.StartsWith("SUBSCRIBE ", StringComparison.Ordinal));
        server.SuppressReply = command => command.StartsWith("SUBSCRIBE ", StringComparison.Ordinal);
        server.CloseConnection(server.ReceivedConnectionIds[subscribeIndex]);

        await Assert.That(await subscription.Completion.WaitAsync(TimeSpan.FromSeconds(5)))
            .IsEqualTo(RespireSubscriptionEndReason.ReconnectExhausted);
        await Assert.That(Volatile.Read(ref exhaustionMeasurements)).IsEqualTo(1);

        // Reconciliation leaves an exhausted primary alone, but an explicit subscription
        // connects to it again once it is reachable.
        server.SuppressReply = null;
        await using var retry = await client.SubscribeAsync(RespireChannel.KeyEvent(RespireKeyNotificationType.Set, 0))
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("SUBSCRIBE ", StringComparison.Ordinal)))
            .IsEqualTo(3);
        await Assert.That(retry.Completion.IsCompleted).IsFalse();
    }

    [Test]
    public async Task CancellationDuringFinalEndpointAckRollsBackAndAllowsRetry()
    {
        await using var first = new FakeRespServer(20);
        await using var second = new FakeRespServer(20);
        await using var third = new FakeRespServer(20);
        var topology = Topology(first.Port, second.Port, third.Port);
        Configure(first, topology, resp3: false);
        Configure(second, topology, resp3: false);
        Configure(third, topology, resp3: false);
        await using var client = CreateClusterClient(first.Port, resp3: false);
        var pendingAck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var suppress = true;
        third.SuppressReply = command =>
        {
            if (!suppress || !command.StartsWith("PSUBSCRIBE ", StringComparison.Ordinal)) return false;
            pendingAck.TrySetResult();
            return true;
        };
        var descriptor = RespireChannel.KeySpacePrefix("tenant:", 0);
        using var cancellation = new CancellationTokenSource();
        var activation = client.SubscribeAsync(descriptor, cancellation.Token).AsTask();
        await pendingAck.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.That(async () => await activation.WaitAsync(TimeSpan.FromSeconds(10)))
            .Throws<OperationCanceledException>();

        suppress = false;
        await using var retry = await client.SubscribeAsync(descriptor).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        foreach (var server in new[] { first, second, third })
            await Assert.That(server.ReceivedCommands.Count(command => command == $"PSUBSCRIBE {descriptor}"))
                .IsEqualTo(2);
    }

    [Test]
    public async Task ClientDisposalInterruptsPendingClusterNotificationAck()
    {
        await using var first = new FakeRespServer(20);
        await using var second = new FakeRespServer(20);
        var topology = Topology(first.Port, second.Port);
        Configure(first, topology, resp3: false);
        Configure(second, topology, resp3: false);
        var client = CreateClusterClient(first.Port, resp3: false);
        var pendingAck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        second.SuppressReply = command =>
        {
            if (!command.StartsWith("PSUBSCRIBE ", StringComparison.Ordinal)) return false;
            pendingAck.TrySetResult();
            return true;
        };

        var activation = client.SubscribeAsync(RespireChannel.KeySpacePrefix("tenant:", 0)).AsTask();
        await pendingAck.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(async () => await activation.WaitAsync(TimeSpan.FromSeconds(5)))
            .Throws<RespireConnectionException>();
    }

    [Test]
    public async Task ClientDisposalInterruptsPendingCandidateReplay()
    {
        await using var first = new FakeRespServer(20);
        await using var second = new FakeRespServer(20);
        var topology = Topology(first.Port, second.Port);
        Configure(first, topology, resp3: false);
        Configure(second, topology, resp3: false);
        var reconnecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var suppressReplay = false;
        var descriptor = RespireChannel.KeySpacePrefix("tenant:", 0);
        second.ReplyOverride = (_, command) =>
        {
            if (command == "HELLO 3") return Hello;
            if (command == "INFO SERVER") return ClusterDatabaseTests.Info();
            if (command == "CLUSTER SLOTS") return topology;
            if (command.StartsWith("PSUBSCRIBE ", StringComparison.Ordinal))
                return Confirmation("psubscribe", command[11..], resp3: false);
            return FakeRespServer.OkReply;
        };
        second.SuppressReply = command =>
        {
            var suppress = Volatile.Read(ref suppressReplay) && command == $"PSUBSCRIBE {descriptor}";
            if (suppress) replayStarted.TrySetResult();
            return suppress;
        };
        var options = new RespireOptions
        {
            UseCluster = true,
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            CommandTimeout = TimeSpan.FromMinutes(1),
            ReconnectPolicy = new RespireReconnectPolicy
            {
                InitialDelay = TimeSpan.FromSeconds(30),
                MaxDelay = TimeSpan.FromSeconds(30),
                JitterRatio = 0,
                MaxAttempts = 2,
            },
            Endpoints = [new("127.0.0.1", first.Port)],
        };
        var client = RespireClient.Create(options);
        client.ConnectionStateChanged += change =>
        {
            if (change.Endpoint.Port == second.Port && change.State == RespireConnectionState.Reconnecting)
                reconnecting.TrySetResult();
        };
        await using var subscription = await client.SubscribeAsync(descriptor).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10));
        var subscribeIndex = second.ReceivedCommands.ToList()
            .FindIndex(command => command == $"PSUBSCRIBE {descriptor}");
        var connectionId = second.ReceivedConnectionIds[subscribeIndex];

        Volatile.Write(ref suppressReplay, true);
        second.CloseConnection(connectionId);
        await reconnecting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var activation = client.SubscribeAsync(descriptor).AsTask();
        await replayStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(async () => await activation.WaitAsync(TimeSpan.FromSeconds(5))).Throws<Exception>();
    }

    [Test]
    public async Task RollbackKeepsAcknowledgedSharedConnectionsOpen()
    {
        await using var first = new FakeRespServer(20);
        await using var second = new FakeRespServer(20);
        await using var third = new FakeRespServer(20);
        var topology = Topology(first.Port, second.Port, third.Port);
        Configure(first, topology, resp3: false);
        Configure(second, topology, resp3: false);
        Configure(third, topology, resp3: false);
        await using var client = CreateClusterClient(first.Port, resp3: false);
        var stableDescriptor = RespireChannel.KeySpacePrefix("stable:", 0);
        await using var stable = await client.SubscribeAsync(stableDescriptor).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        var stableGap = new TaskCompletionSource<RespireSubscriptionGap>(TaskCreationOptions.RunContinuationsAsynchronously);
        stable.DeliveryGap += gap => stableGap.TrySetResult(gap);
        var pendingAck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        third.SuppressReply = command =>
        {
            if (!command.StartsWith("PSUBSCRIBE __keyspace@0__:tenant:*", StringComparison.Ordinal)) return false;
            pendingAck.TrySetResult();
            return true;
        };
        using var cancellation = new CancellationTokenSource();
        var activation = client.SubscribeAsync(RespireChannel.KeySpacePrefix("tenant:", 0), cancellation.Token).AsTask();
        await pendingAck.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.That(async () => await activation.WaitAsync(TimeSpan.FromSeconds(10)))
            .Throws<OperationCanceledException>();

        foreach (var server in new[] { first, second })
            await Assert.That(server.ReceivedCommands.Count(command => command == $"PSUBSCRIBE {stableDescriptor}"))
                .IsEqualTo(1);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (third.ReceivedCommands.Count(command => command == $"PSUBSCRIBE {stableDescriptor}") < 2)
            await Task.Delay(10, deadline.Token);
        // Closing the shared socket interrupts the surviving subscription, which must learn of it.
        var gap = await stableGap.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(gap.Reason).IsEqualTo(RespireSubscriptionGapReason.Reconnect);
        await Assert.That(stable.Completion.IsCompleted).IsFalse();
    }

    [Test]
    public async Task ClearingOneSlotOwnerKeepsAllPrimaryRoutes()
    {
        await using var first = new FakeRespServer(20);
        await using var second = new FakeRespServer(20);
        var topology = Topology(first.Port, second.Port);
        Configure(first, topology, resp3: false);
        Configure(second, topology, resp3: false);
        await using var client = CreateClusterClient(first.Port, resp3: false);
        var descriptor = RespireChannel.KeySpacePrefix("tenant:", 0);
        await using var subscription = await client.SubscribeAsync(descriptor).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        var router = client.Core.Cluster!;
        router.ClearSlotOwner(0, router.GetMultiplexer(new("127.0.0.1", first.Port)));
        // The topology callback queues its reconciliation on the control gate synchronously.
        // A later subscription waits behind it, so once that returns the pass has finished.
        await using var barrier = await client.SubscribeAsync(RespireChannel.KeyEvent(RespireKeyNotificationType.Set, 0))
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        foreach (var server in new[] { first, second })
            await Assert.That(server.ReceivedCommands).DoesNotContain($"PUNSUBSCRIBE {descriptor}");
    }

    [Test]
    public async Task TopologyReconciliationHonorsReconnectAttemptLimit()
    {
        await using var first = new FakeRespServer(20);
        await using var second = new FakeRespServer(20);
        var topology = SinglePrimaryTopology(first.Port);
        Configure(first, () => topology, resp3: false);
        Configure(second, () => topology, resp3: false);
        var configured = second.ReplyOverride!;
        second.ReplyOverride = (connectionId, command) => command.StartsWith("PSUBSCRIBE ", StringComparison.Ordinal)
            ? "-NOPERM denied\r\n"u8.ToArray()
            : configured(connectionId, command);
        await using var client = CreateClusterClient(first.Port, resp3: false, new RespireReconnectPolicy
        {
            InitialDelay = TimeSpan.FromMilliseconds(1),
            MaxDelay = TimeSpan.FromMilliseconds(1),
            JitterRatio = 0,
            MaxAttempts = 2,
        });
        var descriptor = RespireChannel.KeySpacePrefix("tenant:", 0);
        var subscription = await client.SubscribeAsync(descriptor).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        topology = Topology(first.Port, second.Port);
        _ = await client.Core.Cluster!.GetMasterConnectionsAsync(CancellationToken.None, discovery: null)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(await subscription.Completion.WaitAsync(TimeSpan.FromSeconds(10)))
            .IsEqualTo(RespireSubscriptionEndReason.ReconnectExhausted);
        await Assert.That(second.ReceivedCommands.Count(command => command == $"PSUBSCRIBE {descriptor}"))
            .IsEqualTo(2);
    }

    [Test]
    public async Task ReconciliationAttemptsAreCountedPerFailingEndpoint()
    {
        await using var first = new FakeRespServer(20);
        await using var second = new FakeRespServer(20);
        await using var third = new FakeRespServer(20);
        var topology = SinglePrimaryTopology(first.Port);
        Configure(first, () => topology, resp3: false);
        var secondRejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        foreach (var (server, rejected) in new (FakeRespServer, TaskCompletionSource?)[] { (second, secondRejected), (third, null) })
        {
            Configure(server, () => topology, resp3: false);
            var configured = server.ReplyOverride!;
            server.ReplyOverride = (connectionId, command) =>
            {
                if (!command.StartsWith("PSUBSCRIBE ", StringComparison.Ordinal)) return configured(connectionId, command);
                rejected?.TrySetResult();
                return "-NOPERM denied\r\n"u8.ToArray();
            };
        }
        await using var client = CreateClusterClient(first.Port, resp3: false, new RespireReconnectPolicy
        {
            InitialDelay = TimeSpan.FromMilliseconds(200),
            MaxDelay = TimeSpan.FromMilliseconds(200),
            JitterRatio = 0,
            MaxAttempts = 2,
        });
        var descriptor = RespireChannel.KeySpacePrefix("tenant:", 0);
        var subscription = await client.SubscribeAsync(descriptor).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        // One failure against the second primary, then a newer topology replaces it.
        topology = Topology(first.Port, second.Port);
        _ = await client.Core.Cluster!.GetMasterConnectionsAsync(CancellationToken.None, discovery: null)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await secondRejected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        topology = Topology(first.Port, third.Port);
        _ = await client.Core.Cluster!.GetMasterConnectionsAsync(CancellationToken.None, discovery: null)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(await subscription.Completion.WaitAsync(TimeSpan.FromSeconds(10)))
            .IsEqualTo(RespireSubscriptionEndReason.ReconnectExhausted);
        await Assert.That(second.ReceivedCommands.Count(command => command == $"PSUBSCRIBE {descriptor}"))
            .IsEqualTo(1);
        await Assert.That(third.ReceivedCommands.Count(command => command == $"PSUBSCRIBE {descriptor}"))
            .IsEqualTo(2);
    }

    [Test]
    public async Task ReconciliationExhaustionKeepsSharedPrimaryRecoverable()
    {
        await using var first = new FakeRespServer(20);
        await using var second = new FakeRespServer(20);
        var topology = SinglePrimaryTopology(first.Port);
        Configure(first, () => topology, resp3: false);
        var stableDescriptor = RespireChannel.KeySpacePrefix("stable:", 0);
        var tenantDescriptor = RespireChannel.KeySpacePrefix("tenant:", 0);
        var stableReplays = 0;
        var stableReplayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Configure(second, () => topology, resp3: false, command =>
        {
            if (command == $"PSUBSCRIBE {stableDescriptor}" && Interlocked.Increment(ref stableReplays) == 2)
                stableReplayed.TrySetResult();
        });
        second.SuppressReply = command => command == $"PSUBSCRIBE {tenantDescriptor}";
        await using var client = RespireClient.Create(new RespireOptions
        {
            UseCluster = true,
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            CommandTimeout = TimeSpan.FromMilliseconds(200),
            ReconnectPolicy = new RespireReconnectPolicy
            {
                InitialDelay = TimeSpan.FromMilliseconds(1),
                MaxDelay = TimeSpan.FromMilliseconds(1),
                JitterRatio = 0,
                MaxAttempts = 1,
            },
            Endpoints = [new("127.0.0.1", first.Port)],
        });
        var stable = await client.SubscribeAsync(stableDescriptor).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        var tenant = await client.SubscribeAsync(tenantDescriptor).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        // Both subscriptions gain the second primary. The tenant SUBSCRIBE times out, which
        // closes the socket the stable subscription shares, and exhausts only the tenant one.
        topology = Topology(first.Port, second.Port);
        _ = await client.Core.Cluster!.GetMasterConnectionsAsync(CancellationToken.None, discovery: null)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(await tenant.Completion.WaitAsync(TimeSpan.FromSeconds(10)))
            .IsEqualTo(RespireSubscriptionEndReason.ReconnectExhausted);
        await Assert.That(client.Core.Hub!.HasClusterNotificationReconciliationState(tenant)).IsFalse();
        await stableReplayed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(stable.Completion.IsCompleted).IsFalse();
        await stable.DisposeAsync();
    }

    [Test]
    public async Task TopologyReconciliationRetriesReportReconnectingUntilRecovered()
    {
        await using var first = new FakeRespServer(20);
        await using var second = new FakeRespServer(20);
        var topology = SinglePrimaryTopology(first.Port);
        Configure(first, () => topology, resp3: false);
        Configure(second, () => topology, resp3: false);
        var descriptor = RespireChannel.KeySpacePrefix("tenant:", 0);
        var suppress = true;
        second.SuppressReply = command => suppress && command == $"PSUBSCRIBE {descriptor}";
        await using var client = RespireClient.Create(new RespireOptions
        {
            UseCluster = true,
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            CommandTimeout = TimeSpan.FromMilliseconds(200),
            Endpoints = [new("127.0.0.1", first.Port)],
        });
        var reconnecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += change =>
        {
            if (change.Endpoint.Port != second.Port || change.ReconnectSource != RespireReconnectSource.PubSub) return;
            if (change.State == RespireConnectionState.Reconnecting) reconnecting.TrySetResult();
            else if (change.State == RespireConnectionState.Connected && reconnecting.Task.IsCompleted)
                recovered.TrySetResult();
        };
        await using var subscription = await client.SubscribeAsync(descriptor).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        topology = Topology(first.Port, second.Port);
        _ = await client.Core.Cluster!.GetMasterConnectionsAsync(CancellationToken.None, discovery: null)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await reconnecting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(subscription.Completion.IsCompleted).IsFalse();

        suppress = false;
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task DisposingSubscriptionClearsItsPendingReconciliationRetry()
    {
        await using var first = new FakeRespServer(20);
        await using var second = new FakeRespServer(20);
        var topology = SinglePrimaryTopology(first.Port);
        Configure(first, () => topology, resp3: false);
        Configure(second, () => topology, resp3: false);
        var descriptor = RespireChannel.KeySpacePrefix("tenant:", 0);
        second.SuppressReply = command => command == $"PSUBSCRIBE {descriptor}";
        await using var client = RespireClient.Create(new RespireOptions
        {
            UseCluster = true,
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            CommandTimeout = TimeSpan.FromMilliseconds(200),
            ReconnectPolicy = new RespireReconnectPolicy
            {
                InitialDelay = TimeSpan.FromHours(1),
                MaxDelay = TimeSpan.FromHours(1),
                JitterRatio = 0,
            },
            Endpoints = [new("127.0.0.1", first.Port)],
        });
        var reconnecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += change =>
        {
            if (change.Endpoint.Port != second.Port || change.ReconnectSource != RespireReconnectSource.PubSub) return;
            if (change.State == RespireConnectionState.Reconnecting) reconnecting.TrySetResult();
            else if (change.State == RespireConnectionState.Connected && reconnecting.Task.IsCompleted)
                recovered.TrySetResult();
        };
        var subscription = await client.SubscribeAsync(descriptor).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        topology = Topology(first.Port, second.Port);
        _ = await client.Core.Cluster!.GetMasterConnectionsAsync(CancellationToken.None, discovery: null)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await reconnecting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await subscription.DisposeAsync();
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task ServerRejectionClearsEarlierReconciliationOutageState()
    {
        await using var first = new FakeRespServer(20);
        await using var second = new FakeRespServer(20);
        var topology = SinglePrimaryTopology(first.Port);
        Configure(first, () => topology, resp3: false);
        Configure(second, () => topology, resp3: false);
        var descriptor = RespireChannel.KeySpacePrefix("tenant:", 0);
        var suppress = true;
        var reject = false;
        second.SuppressReply = command => suppress && command == $"PSUBSCRIBE {descriptor}";
        var configuredReply = second.ReplyOverride!;
        var rejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        second.ReplyOverride = (connectionId, command) =>
        {
            if (reject && command == $"PSUBSCRIBE {descriptor}")
            {
                rejected.TrySetResult();
                return "-NOPERM denied\r\n"u8.ToArray();
            }
            return configuredReply(connectionId, command);
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            UseCluster = true,
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            CommandTimeout = TimeSpan.FromMilliseconds(200),
            ReconnectPolicy = new RespireReconnectPolicy
            {
                InitialDelay = TimeSpan.FromSeconds(2),
                MaxDelay = TimeSpan.FromSeconds(2),
                JitterRatio = 0,
            },
            Endpoints = [new("127.0.0.1", first.Port)],
        });
        var reconnecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += change =>
        {
            if (change.Endpoint.Port != second.Port || change.ReconnectSource != RespireReconnectSource.PubSub) return;
            if (change.State == RespireConnectionState.Reconnecting) reconnecting.TrySetResult();
        };
        await using var subscription = await client.SubscribeAsync(descriptor).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        topology = Topology(first.Port, second.Port);
        _ = await client.Core.Cluster!.GetMasterConnectionsAsync(CancellationToken.None, discovery: null)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await reconnecting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        suppress = false;
        reject = true;
        await rejected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var hub = client.Core.Hub!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (hub.IsClusterNotificationEndpointRetrying(new("127.0.0.1", second.Port)))
            await Task.Delay(10, deadline.Token);
    }

    [Test]
    public async Task NodeReplayRejectionEndsOnlyTheRejectedSubscription()
    {
        await using var server = new FakeRespServer(20);
        var stableDescriptor = RespireChannel.KeySpacePrefix("stable:", 0);
        var tenantDescriptor = RespireChannel.KeySpacePrefix("tenant:", 0);
        var stableSubscribes = 0;
        var stableReplayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Configure(server, SinglePrimaryTopology(server.Port), resp3: false);
        var configured = server.ReplyOverride!;
        var reject = false;
        server.ReplyOverride = (connectionId, command) =>
        {
            if (command == $"PSUBSCRIBE {stableDescriptor}" && Interlocked.Increment(ref stableSubscribes) == 2)
                stableReplayed.TrySetResult();
            return reject && command == $"PSUBSCRIBE {tenantDescriptor}"
                ? "-NOPERM denied\r\n"u8.ToArray()
                : configured(connectionId, command);
        };
        await using var client = CreateClusterClient(server.Port, resp3: false, new RespireReconnectPolicy
        {
            InitialDelay = TimeSpan.FromMilliseconds(1),
            MaxDelay = TimeSpan.FromMilliseconds(1),
            JitterRatio = 0,
            MaxAttempts = 2,
        });
        var stable = await client.SubscribeAsync(stableDescriptor).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        var tenant = await client.SubscribeAsync(tenantDescriptor).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        reject = true;
        var index = server.ReceivedCommands.ToList().FindIndex(command => command == $"PSUBSCRIBE {stableDescriptor}");
        server.CloseConnection(server.ReceivedConnectionIds[index]);

        await Assert.That(await tenant.Completion.WaitAsync(TimeSpan.FromSeconds(10)))
            .IsEqualTo(RespireSubscriptionEndReason.ReconnectExhausted);
        await stableReplayed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(stable.Completion.IsCompleted).IsFalse();
        await stable.DisposeAsync();
    }

    [Test]
    public async Task NewRouteRejectionKeepsSharedNotificationSocketOpen()
    {
        await using var first = new FakeRespServer(20);
        await using var second = new FakeRespServer(20);
        var topology = Topology(first.Port, second.Port);
        Configure(first, () => topology, resp3: false);
        Configure(second, () => topology, resp3: false);
        var stableDescriptor = RespireChannel.KeyEvent(RespireKeyNotificationType.Set, 0);
        var key = Enumerable.Range(0, 100_000).Select(static index => $"tenant:{index}")
            .First(static value => ClusterHash.GetSlot(value) >= 8192);
        var movedDescriptor = RespireChannel.KeySpaceSingleKey(key, 0);
        var stableSubscribes = 0;
        var rejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reject = false;
        var configured = first.ReplyOverride!;
        first.ReplyOverride = (connectionId, command) =>
        {
            if (command == $"SUBSCRIBE {stableDescriptor}") Interlocked.Increment(ref stableSubscribes);
            if (reject && command == $"SUBSCRIBE {movedDescriptor}")
            {
                rejected.TrySetResult();
                return "-NOPERM denied\r\n"u8.ToArray();
            }
            return configured(connectionId, command);
        };
        await using var client = CreateClusterClient(first.Port, resp3: false);
        await using var stable = await client.SubscribeAsync(stableDescriptor).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await using var moved = await client.SubscribeAsync(movedDescriptor).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        var stableSubscribeCount = Volatile.Read(ref stableSubscribes);
        var stableCommand = first.ReceivedCommands.ToList().FindIndex(command => command == $"SUBSCRIBE {stableDescriptor}");
        var sharedConnection = first.ReceivedConnectionIds[stableCommand];
        Volatile.Write(ref reject, true);

        topology = Topology(second.Port, first.Port);
        _ = await client.Core.Cluster!.GetMasterConnectionsAsync(CancellationToken.None, discovery: null)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await rejected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(400);
        await Assert.That(Volatile.Read(ref stableSubscribes)).IsEqualTo(stableSubscribeCount);

        var message = Encoding.ASCII.GetBytes(
            $"*3\r\n$7\r\nmessage\r\n${stableDescriptor.ToString().Length}\r\n{stableDescriptor}\r\n$3\r\nkey\r\n");
        await first.SendRawAsync(message, sharedConnection);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var reader = stable.GetAsyncEnumerator(timeout.Token);
        await Assert.That(await reader.MoveNextAsync()).IsTrue();
        await Assert.That(reader.Current.Channel).IsEqualTo(stableDescriptor);
    }

    [Test]
    public async Task ActivationRejectionKeepsSharedNotificationSocketOpen()
    {
        await using var server = new FakeRespServer(20);
        var topology = SinglePrimaryTopology(server.Port);
        Configure(server, () => topology, resp3: false);
        var stableDescriptor = RespireChannel.KeyEvent(RespireKeyNotificationType.Set, 0);
        var key = Enumerable.Range(0, 100_000).Select(static index => $"tenant:{index}")
            .First(static value => ClusterHash.GetSlot(value) >= 8192);
        var rejectedDescriptor = RespireChannel.KeySpaceSingleKey(key, 0);
        var stableSubscribes = 0;
        var reject = false;
        var configured = server.ReplyOverride!;
        server.ReplyOverride = (connectionId, command) =>
        {
            if (command == $"SUBSCRIBE {stableDescriptor}") Interlocked.Increment(ref stableSubscribes);
            if (reject && command == $"SUBSCRIBE {rejectedDescriptor}")
                return "-NOPERM denied\r\n"u8.ToArray();
            return configured(connectionId, command);
        };
        await using var client = CreateClusterClient(server.Port, resp3: false);
        await using var stable = await client.SubscribeAsync(stableDescriptor).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        var stableSubscribeCount = Volatile.Read(ref stableSubscribes);
        var stableCommand = server.ReceivedCommands.ToList().FindIndex(command => command == $"SUBSCRIBE {stableDescriptor}");
        var sharedConnection = server.ReceivedConnectionIds[stableCommand];
        Volatile.Write(ref reject, true);

        await Assert.That(async () => await client.SubscribeAsync(rejectedDescriptor))
            .Throws<RespireServerException>();
        await Task.Delay(250);
        await Assert.That(Volatile.Read(ref stableSubscribes)).IsEqualTo(stableSubscribeCount);
        var message = Encoding.ASCII.GetBytes(
            $"*3\r\n$7\r\nmessage\r\n${stableDescriptor.ToString().Length}\r\n{stableDescriptor}\r\n$3\r\nkey\r\n");
        await server.SendRawAsync(message, sharedConnection);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var reader = stable.GetAsyncEnumerator(timeout.Token);
        await Assert.That(await reader.MoveNextAsync()).IsTrue();
        await Assert.That(reader.Current.Channel).IsEqualTo(stableDescriptor);
    }

    [Test]
    public async Task RejectedOnlyRouteKeepsItsSubscriptionInReconciliation()
    {
        await using var server = new FakeRespServer(20);
        var descriptor = RespireChannel.KeySpacePrefix("tenant:", 0);
        Configure(server, SinglePrimaryTopology(server.Port), resp3: false);
        var configured = server.ReplyOverride!;
        var reject = false;
        var rejections = 0;
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ReplyOverride = (connectionId, command) =>
        {
            if (command != $"PSUBSCRIBE {descriptor}" || !Volatile.Read(ref reject)) return configured(connectionId, command);
            // Reject the replay and the first reconciliation attempt, then accept the route again.
            if (Interlocked.Increment(ref rejections) <= 2) return "-NOPERM denied\r\n"u8.ToArray();
            accepted.TrySetResult();
            return configured(connectionId, command);
        };
        await using var client = CreateClusterClient(server.Port, resp3: false, new RespireReconnectPolicy
        {
            InitialDelay = TimeSpan.FromMilliseconds(1),
            MaxDelay = TimeSpan.FromMilliseconds(1),
            JitterRatio = 0,
        });
        await using var subscription = await client.SubscribeAsync(descriptor).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        var gapPublished = new TaskCompletionSource<RespireSubscriptionGap>(TaskCreationOptions.RunContinuationsAsynchronously);
        subscription.DeliveryGap += gap => gapPublished.TrySetResult(gap);

        Volatile.Write(ref reject, true);
        var index = server.ReceivedCommands.ToList().FindIndex(command => command == $"PSUBSCRIBE {descriptor}");
        server.CloseConnection(server.ReceivedConnectionIds[index]);

        // The subscription's only route was rejected, yet reconciliation must still retry it.
        await accepted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var gap = await gapPublished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(gap.Reason).IsEqualTo(RespireSubscriptionGapReason.Reconnect);
        await Assert.That(subscription.Completion.IsCompleted).IsFalse();
    }

    [Test]
    public async Task ReplayRejectionsCoalesceFullReconciliationForSameTopology()
    {
        await using var first = new FakeRespServer(20);
        await using var second = new FakeRespServer(20);
        var topology = Topology(first.Port, second.Port);
        Configure(first, () => topology, resp3: false);
        Configure(second, () => topology, resp3: false);
        var descriptor = RespireChannel.KeySpacePrefix("tenant:", 0);
        var reject = false;
        var firstRejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        foreach (var (server, rejected) in new[] { (first, firstRejected), (second, secondRejected) })
        {
            var configured = server.ReplyOverride!;
            server.ReplyOverride = (connectionId, command) =>
            {
                if (Volatile.Read(ref reject) && command == $"PSUBSCRIBE {descriptor}")
                {
                    rejected.TrySetResult();
                    return "-NOPERM denied\r\n"u8.ToArray();
                }
                return configured(connectionId, command);
            };
        }
        await using var client = CreateClusterClient(first.Port, resp3: false, new RespireReconnectPolicy
        {
            InitialDelay = TimeSpan.FromSeconds(2),
            MaxDelay = TimeSpan.FromSeconds(2),
            JitterRatio = 0,
            MaxAttempts = 2,
        });
        var subscription = await client.SubscribeAsync(descriptor).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        var firstCommand = first.ReceivedCommands.ToList().FindIndex(command => command == $"PSUBSCRIBE {descriptor}");
        var secondCommand = second.ReceivedCommands.ToList().FindIndex(command => command == $"PSUBSCRIBE {descriptor}");
        var firstConnection = first.ReceivedConnectionIds[firstCommand];
        var secondConnection = second.ReceivedConnectionIds[secondCommand];

        Volatile.Write(ref reject, true);
        first.CloseConnection(firstConnection);
        second.CloseConnection(secondConnection);
        await Task.WhenAll(firstRejected.Task, secondRejected.Task).WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(300);

        await Assert.That(subscription.Completion.IsCompleted).IsFalse();
        await subscription.DisposeAsync();
    }

    [Test]
    public async Task ActivationAppliesTopologyChangePublishedBeforeItsFinalAck()
    {
        await using var first = new FakeRespServer(20);
        await using var second = new FakeRespServer(20);
        await using var third = new FakeRespServer(20);
        var topology = Topology(first.Port, second.Port);
        Configure(first, () => topology, resp3: false);
        Configure(second, () => topology, resp3: false);
        Configure(third, () => topology, resp3: false);
        await using var client = CreateClusterClient(first.Port, resp3: false);
        var descriptor = RespireChannel.KeySpacePrefix("tenant:", 0);
        var secondAckPending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        second.SuppressReply = command =>
        {
            if (command != $"PSUBSCRIBE {descriptor}") return false;
            secondAckPending.TrySetResult();
            return true;
        };
        var activation = client.SubscribeAsync(descriptor).AsTask();
        await secondAckPending.Task.WaitAsync(TimeSpan.FromSeconds(10));

        topology = Topology(first.Port, second.Port, third.Port);
        _ = await client.Core.Cluster!.GetMasterConnectionsAsync(CancellationToken.None, discovery: null)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        var ackIndex = second.ReceivedCommands.ToList().FindIndex(command => command == $"PSUBSCRIBE {descriptor}");
        await second.SendRawAsync(Confirmation("psubscribe", descriptor.ToString(), resp3: false),
            second.ReceivedConnectionIds[ackIndex]);

        await using var subscription = await activation.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(third.ReceivedCommands).Contains($"PSUBSCRIBE {descriptor}");
    }

    [Test]
    public async Task RecoveredEndpointStaysReconnectingUntilFailedReconciliationRouteIsAcknowledged()
    {
        await using var first = new FakeRespServer(20);
        await using var second = new FakeRespServer(20);
        var topology = SinglePrimaryTopology(first.Port);
        Configure(first, () => topology, resp3: false);
        var stableDescriptor = RespireChannel.KeySpacePrefix("stable:", 0);
        var tenantDescriptor = RespireChannel.KeySpacePrefix("tenant:", 0);
        var stableSubscribes = 0;
        var tenantSubscribes = 0;
        var stableReplayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tenantRetried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Configure(second, () => topology, resp3: false, command =>
        {
            if (command == $"PSUBSCRIBE {stableDescriptor}" && Interlocked.Increment(ref stableSubscribes) == 2)
                stableReplayed.TrySetResult();
        });
        var suppress = true;
        // Suppressed commands never reach the reply override, so count them here.
        second.SuppressReply = command =>
        {
            if (command != $"PSUBSCRIBE {tenantDescriptor}" || !Volatile.Read(ref suppress)) return false;
            if (Interlocked.Increment(ref tenantSubscribes) == 3) tenantRetried.TrySetResult();
            return true;
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            UseCluster = true,
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            CommandTimeout = TimeSpan.FromMilliseconds(200),
            Endpoints = [new("127.0.0.1", first.Port)],
        });
        await using var stable = await client.SubscribeAsync(stableDescriptor).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await using var tenant = await client.SubscribeAsync(tenantDescriptor).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        var reconnecting = 0;
        var connectedWhileRouteMissing = 0;
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += change =>
        {
            if (change.Endpoint.Port != second.Port || change.ReconnectSource != RespireReconnectSource.PubSub) return;
            if (change.State == RespireConnectionState.Reconnecting) Interlocked.Exchange(ref reconnecting, 1);
            else if (change.State == RespireConnectionState.Connected && Volatile.Read(ref reconnecting) != 0)
            {
                if (Volatile.Read(ref suppress)) Interlocked.Exchange(ref connectedWhileRouteMissing, 1);
                else recovered.TrySetResult();
            }
        };

        // Both subscriptions gain the second primary. The tenant SUBSCRIBE times out on the
        // socket the stable route shares, so that socket is closed and replays stable only.
        topology = Topology(first.Port, second.Port);
        _ = await client.Core.Cluster!.GetMasterConnectionsAsync(CancellationToken.None, discovery: null)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await stableReplayed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await tenantRetried.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(Volatile.Read(ref reconnecting)).IsEqualTo(1);
        await Assert.That(Volatile.Read(ref connectedWhileRouteMissing)).IsEqualTo(0);

        Volatile.Write(ref suppress, false);
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(tenant.Completion.IsCompleted).IsFalse();
        await Assert.That(stable.Completion.IsCompleted).IsFalse();
    }

    [Test]
    public async Task RecreatedNodeDoesNotHideEarlierRouteReconnectState()
    {
        await using var first = new FakeRespServer(20);
        await using var second = new FakeRespServer(20);
        var topology = SinglePrimaryTopology(first.Port);
        Configure(first, () => topology, resp3: false);
        Configure(second, () => topology, resp3: false);
        var stableDescriptor = RespireChannel.KeySpacePrefix("stable:", 0);
        var laterDescriptor = RespireChannel.KeySpacePrefix("later:", 0);
        var stableAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var laterAdded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stableAttempts = 0;
        second.SuppressReply = command =>
        {
            if (command != $"PSUBSCRIBE {stableDescriptor}") return false;
            if (Interlocked.Increment(ref stableAttempts) == 1)
            {
                stableAttempt.TrySetResult();
                return true;
            }
            return false;
        };
        var configured = second.ReplyOverride!;
        second.ReplyOverride = (connectionId, command) =>
        {
            if (command == $"PSUBSCRIBE {laterDescriptor}") laterAdded.TrySetResult();
            return configured(connectionId, command);
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            UseCluster = true,
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            CommandTimeout = TimeSpan.FromMilliseconds(200),
            ReconnectPolicy = new RespireReconnectPolicy
            {
                InitialDelay = TimeSpan.FromSeconds(10),
                MaxDelay = TimeSpan.FromSeconds(10),
                JitterRatio = 0,
                MaxAttempts = 2,
            },
            Endpoints = [new("127.0.0.1", first.Port)],
        });
        var clock = new NotificationRecoveryClock();
        await using var hub = new SubscriptionHub(client.Core, clock);
        await using var stable = await hub.SubscribeAsync(
            SubscriptionKind.Pattern, [stableDescriptor], new(), CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await using var later = await hub.SubscribeAsync(
            SubscriptionKind.Pattern, [laterDescriptor], new(), CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        var reconnecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += change =>
        {
            if (change.Endpoint.Port != second.Port || change.ReconnectSource != RespireReconnectSource.PubSub) return;
            if (change.State == RespireConnectionState.Reconnecting) reconnecting.TrySetResult();
            else if (change.State == RespireConnectionState.Connected && reconnecting.Task.IsCompleted)
                recovered.TrySetResult();
        };
        topology = Topology(first.Port, second.Port);
        hub.NotifyTopologyChanged(1,
            [new RespireEndpoint("127.0.0.1", first.Port), new RespireEndpoint("127.0.0.1", second.Port)],
            authoritative: true);

        await stableAttempt.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await laterAdded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // A different endpoint can schedule a fake-clock retry first. Wait for this
        // endpoint's state publication before consuming its recovery delay.
        await reconnecting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var retry = await clock.NextAsync();
        await Assert.That(stable.Completion.IsCompleted).IsFalse();
        await Assert.That(later.Completion.IsCompleted).IsFalse();

        retry.Fire();
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(stable.Completion.IsCompleted).IsFalse();
        await Assert.That(later.Completion.IsCompleted).IsFalse();
    }

    [Test]
    public async Task ReplayRejectionStillReportsGapForReplayedRoutes()
    {
        await using var server = new FakeRespServer(20);
        var stableDescriptor = RespireChannel.KeySpacePrefix("stable:", 0);
        var tenantDescriptor = RespireChannel.KeySpacePrefix("tenant:", 0);
        Configure(server, SinglePrimaryTopology(server.Port), resp3: false);
        var configured = server.ReplyOverride!;
        var reject = false;
        server.ReplyOverride = (connectionId, command) =>
            Volatile.Read(ref reject) && command == $"PSUBSCRIBE {tenantDescriptor}"
                ? "-NOPERM denied\r\n"u8.ToArray()
                : configured(connectionId, command);
        // The default policy retries the rejected route without limit, so the gap for the
        // replayed route must not wait for it.
        await using var client = CreateClusterClient(server.Port, resp3: false);
        await using var subscription = await client.SubscribeAsync(
                new RespireChannel[] { stableDescriptor, tenantDescriptor }, CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        var gapPublished = new TaskCompletionSource<RespireSubscriptionGap>(TaskCreationOptions.RunContinuationsAsynchronously);
        subscription.DeliveryGap += gap => gapPublished.TrySetResult(gap);

        Volatile.Write(ref reject, true);
        var index = server.ReceivedCommands.ToList().FindIndex(command => command == $"PSUBSCRIBE {stableDescriptor}");
        server.CloseConnection(server.ReceivedConnectionIds[index]);

        var gap = await gapPublished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(gap.Reason).IsEqualTo(RespireSubscriptionGapReason.Reconnect);
        await Assert.That(subscription.Completion.IsCompleted).IsFalse();
    }

    [Test]
    public async Task ReplayReconciliationDoesNotConsumeOtherSubscriptionsRetryBudget()
    {
        await using var first = new FakeRespServer(20);
        await using var second = new FakeRespServer(20);
        var topology = SinglePrimaryTopology(first.Port);
        Configure(first, () => topology, resp3: false);
        var delayedDescriptor = RespireChannel.KeySpacePrefix("delayed:", 0);
        var rejectedDescriptor = RespireChannel.KeySpacePrefix("rejected:", 0);
        var delayedAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rejectedAdded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replayRejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rejectDelayed = true;
        var rejectReplay = false;
        Configure(second, () => topology, resp3: false);
        second.SuppressReply = command =>
        {
            if (command != $"PSUBSCRIBE {delayedDescriptor}" || !Volatile.Read(ref rejectDelayed)) return false;
            delayedAttempt.TrySetResult();
            return true;
        };
        var configured = second.ReplyOverride!;
        second.ReplyOverride = (connectionId, command) =>
        {
            if (Volatile.Read(ref rejectReplay) && command == $"PSUBSCRIBE {rejectedDescriptor}")
            {
                replayRejected.TrySetResult();
                return "-NOPERM denied\r\n"u8.ToArray();
            }
            if (command == $"PSUBSCRIBE {rejectedDescriptor}") rejectedAdded.TrySetResult();
            return configured(connectionId, command);
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            UseCluster = true,
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            CommandTimeout = TimeSpan.FromMilliseconds(200),
            ReconnectPolicy = new RespireReconnectPolicy
            {
                InitialDelay = TimeSpan.FromSeconds(10),
                MaxDelay = TimeSpan.FromSeconds(10),
                JitterRatio = 0,
                MaxAttempts = 2,
            },
            Endpoints = [new("127.0.0.1", first.Port)],
        });
        var clock = new NotificationRecoveryClock();
        await using var hub = new SubscriptionHub(client.Core, clock);
        await using var delayed = await hub.SubscribeAsync(
            SubscriptionKind.Pattern, [delayedDescriptor], new(), CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await using var rejected = await hub.SubscribeAsync(
            SubscriptionKind.Pattern, [rejectedDescriptor], new(), CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        topology = Topology(first.Port, second.Port);
        _ = await client.Core.Cluster!.GetMasterConnectionsAsync(CancellationToken.None, discovery: null)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        hub.NotifyTopologyChanged(1,
            [new RespireEndpoint("127.0.0.1", first.Port), new RespireEndpoint("127.0.0.1", second.Port)],
            authoritative: true);
        await delayedAttempt.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await rejectedAdded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _ = await clock.NextAsync(); // Keep the delayed subscription retry pending.
        await Assert.That(second.ReceivedCommands.Count(command => command == $"PSUBSCRIBE {rejectedDescriptor}")).IsEqualTo(1);

        Volatile.Write(ref rejectDelayed, false);
        Volatile.Write(ref rejectReplay, true);
        var rejectedConnectionIndex = second.ReceivedCommands.ToList()
            .FindLastIndex(command => command == $"PSUBSCRIBE {rejectedDescriptor}");
        second.CloseConnection(second.ReceivedConnectionIds[rejectedConnectionIndex]);
        (await clock.NextAsync()).Fire();
        await replayRejected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(500);

        await Assert.That(delayed.Completion.IsCompleted).IsFalse();
        await Assert.That(second.ReceivedCommands.Count(command => command == $"PSUBSCRIBE {delayedDescriptor}")).IsEqualTo(1);
    }

    private sealed class NotificationRecoveryClock : TimeProvider
    {
        private readonly global::System.Threading.Channels.Channel<NotificationRecoveryTimer> _timers =
            global::System.Threading.Channels.Channel.CreateUnbounded<NotificationRecoveryTimer>();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new NotificationRecoveryTimer(callback, state);
            _timers.Writer.TryWrite(timer);
            return timer;
        }

        internal Task<NotificationRecoveryTimer> NextAsync()
            => _timers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
    }

    private sealed class NotificationRecoveryTimer(TimerCallback callback, object? state) : ITimer
    {
        private int _finished;
        internal void Fire() { if (Interlocked.Exchange(ref _finished, 1) == 0) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period) => Volatile.Read(ref _finished) == 0;
        public void Dispose() => Interlocked.Exchange(ref _finished, 1);
        public ValueTask DisposeAsync() { Dispose(); return default; }
    }

    [Test]
    public async Task AllPrimaryActivationSubscribesPrimaryAddedSinceCachedTopology()
    {
        await using var first = new FakeRespServer(20);
        await using var second = new FakeRespServer(20);
        await using var third = new FakeRespServer(20);
        var topology = Topology(first.Port, second.Port);
        Configure(first, () => Volatile.Read(ref topology), resp3: false);
        Configure(second, () => Volatile.Read(ref topology), resp3: false);
        Configure(third, () => Volatile.Read(ref topology), resp3: false);
        await using var client = CreateClusterClient(first.Port, resp3: false);
        // The first subscription leaves a complete cached slot map behind.
        await using var firstSubscription = await client.SubscribeAsync(RespireChannel.KeySpacePrefix("tenant:", 0))
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        // The cluster gains a primary, and nothing has told the client yet.
        Volatile.Write(ref topology, Topology(first.Port, second.Port, third.Port));
        var descriptor = RespireChannel.KeyEvent(RespireKeyNotificationType.Set, 0);
        await using var secondSubscription = await client.SubscribeAsync(descriptor).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        foreach (var server in new[] { first, second, third })
            await Assert.That(server.ReceivedCommands).Contains($"SUBSCRIBE {descriptor}");
    }

    [Test]
    public async Task NodeExhaustionIsNotReportedAsRecoveredByAPendingReconciliationRetry()
    {
        await using var first = new FakeRespServer(20);
        await using var second = new FakeRespServer(20);
        var topology = Topology(first.Port, second.Port);
        Configure(first, () => topology, resp3: false);
        Configure(second, () => topology, resp3: false);
        await using var client = RespireClient.Create(new RespireOptions
        {
            UseCluster = true,
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            // Long enough for a loaded test host, short enough to exhaust the replay quickly.
            ConnectTimeout = TimeSpan.FromMilliseconds(500),
            CommandTimeout = TimeSpan.FromMilliseconds(500),
            ReconnectPolicy = new RespireReconnectPolicy
            {
                InitialDelay = TimeSpan.FromMilliseconds(1),
                MaxDelay = TimeSpan.FromMilliseconds(1),
                JitterRatio = 0,
                MaxAttempts = 1,
            },
            Endpoints = [new("127.0.0.1", first.Port)],
        });
        var descriptor = RespireChannel.KeyEvent(RespireKeyNotificationType.Set, 0);
        var subscription = await client.SubscribeAsync(descriptor).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        var secondEndpoint = new RespireEndpoint("127.0.0.1", second.Port);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connectedAfterExhaustion = 0;
        client.ConnectionStateChanged += change =>
        {
            if (change.Endpoint.Port != second.Port || change.ReconnectSource != RespireReconnectSource.PubSub) return;
            if (change.State == RespireConnectionState.Disconnected) disconnected.TrySetResult();
            else if (change.State == RespireConnectionState.Connected && disconnected.Task.IsCompleted)
                Interlocked.Exchange(ref connectedAfterExhaustion, 1);
        };

        // Activation refreshed the topology, which queued a reconciliation pass behind it. A
        // subscription on the first primary waits for that pass on the control gate.
        var keys = Enumerable.Range(0, 100_000).Select(static index => $"tenant:{index}")
            .Where(static value => ClusterHash.GetSlot(value) is > 0 and <= 8191).Take(2).ToArray();
        await using var settled = await client.SubscribeAsync(RespireChannel.KeySpaceSingleKey(keys[0], 0))
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        // A reconciliation route on the second primary failed earlier and is still waiting
        // for its retry when the node's own recovery runs out of attempts.
        var hub = client.Core.Hub!;
        hub.MarkClusterNotificationEndpointRetrying(secondEndpoint);
        await Assert.That(hub.IsClusterNotificationEndpointRetrying(secondEndpoint)).IsTrue();
        second.SuppressReply = command => command.StartsWith("SUBSCRIBE ", StringComparison.Ordinal);
        var subscribeIndex = second.ReceivedCommands.ToList().FindIndex(command => command == $"SUBSCRIBE {descriptor}");
        second.CloseConnection(second.ReceivedConnectionIds[subscribeIndex]);
        await Assert.That(await subscription.Completion.WaitAsync(TimeSpan.FromSeconds(10)))
            .IsEqualTo(RespireSubscriptionEndReason.ReconnectExhausted);
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(hub.IsClusterNotificationEndpointRetrying(secondEndpoint)).IsFalse();

        // A later reconciliation pass must not turn the terminal Disconnected into Connected.
        var router = client.Core.Cluster!;
        router.ClearSlotOwner(0, router.GetMultiplexer(new("127.0.0.1", first.Port)));
        // The pass is queued on the control gate synchronously, so a later subscription on the
        // healthy primary waits for it. A slot other than the cleared one keeps it there.
        await using var barrier = await client.SubscribeAsync(RespireChannel.KeySpaceSingleKey(keys[1], 0))
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(second.ReceivedCommands).DoesNotContain($"SUBSCRIBE {RespireChannel.KeySpaceSingleKey(keys[1], 0)}");
        await Assert.That(Volatile.Read(ref connectedAfterExhaustion)).IsEqualTo(0);
    }

    [Test]
    [NotInParallel] // The shared no-GC measurement boundary is process-wide.
    public async Task UnroutedNotificationFrameAllocatesNothing()
    {
        await using var server = new FakeRespServer(20);
        Configure(server, SinglePrimaryTopology(server.Port), resp3: false);
        await using var client = CreateClusterClient(server.Port, resp3: false);
        await using var subscription = await client.SubscribeAsync(RespireChannel.KeyEvent(RespireKeyNotificationType.Set, 0))
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        var hub = client.Core.Hub!;
        var endpoint = new RespireEndpoint("127.0.0.1", server.Port);
        var channel = "__keyevent@0__:del"u8.ToArray();
        var payload = "tenant:key"u8.ToArray();
        _ = MeasureUnroutedDelivery(hub, endpoint, channel, payload, allocate: false, iterations: 100);
        _ = MeasureUnroutedDelivery(hub, endpoint, channel, payload, allocate: true, iterations: 100);
        // Keep concurrent GC and surrounding async/assertion allocations outside the counter
        // interval. See docs/ALLOCATION_MEASUREMENT.md for this shared boundary.
        var (measured, control) = AllocationMeasurement.WithoutConcurrentGc(() =>
            (MeasureUnroutedDelivery(hub, endpoint, channel, payload, allocate: false, iterations: 1000),
                MeasureUnroutedDelivery(hub, endpoint, channel, payload, allocate: true, iterations: 1000)));
        await Assert.That(measured.Delivered).IsEqualTo(1000);
        await Assert.That(control.Delivered).IsEqualTo(1000);
        await Assert.That(measured.Allocated).IsEqualTo(0L);
        await Assert.That(control.Allocated).IsGreaterThanOrEqualTo(1000L * 37);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (long Allocated, int Delivered) MeasureUnroutedDelivery(
        SubscriptionHub hub, RespireEndpoint endpoint, byte[] channel, byte[] payload, bool allocate, int iterations)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var delivered = 0;
        for (var index = 0; index < iterations; index++)
        {
            if (hub.TryDeliverClusterNotification(endpoint, channel, payload)) delivered++;
            if (allocate) GC.KeepAlive(AllocateDeliveryControl());
        }
        return (GC.GetAllocatedBytesForCurrentThread() - before, delivered);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static object AllocateDeliveryControl() => new byte[37];

    [Test]
    public async Task ConcurrentSubscriptionsAndTopologyFlipsReleaseEveryRoute()
    {
        await using var first = new FakeRespServer(20);
        await using var second = new FakeRespServer(20);
        await using var third = new FakeRespServer(20);
        var topology = Topology(first.Port, second.Port);
        Configure(first, () => Volatile.Read(ref topology), resp3: false);
        Configure(second, () => Volatile.Read(ref topology), resp3: false);
        Configure(third, () => Volatile.Read(ref topology), resp3: false);
        await using var client = CreateClusterClient(first.Port, resp3: false);
        var key = Enumerable.Range(0, 100_000).Select(static index => $"tenant:{index}")
            .First(static value => ClusterHash.GetSlot(value) <= 8191);
        RespireChannel[] descriptors =
        [
            RespireChannel.KeySpacePrefix("tenant:", 0),
            RespireChannel.KeyEvent(RespireKeyNotificationType.Set, 0),
            RespireChannel.KeySpaceSingleKey(key, 0),
        ];
        List<RespireSubscription> live = [];
        try
        {
            for (var iteration = 0; iteration < 6; iteration++)
            {
                var subscribing = Enumerable.Range(0, 4)
                    .Select(index => client.SubscribeAsync(descriptors[(iteration + index) % descriptors.Length]).AsTask())
                    .ToArray();
                Volatile.Write(ref topology, iteration % 2 == 0
                    ? Topology(second.Port, third.Port)
                    : Topology(first.Port, second.Port));
                var refresh = client.Core.Cluster!.GetMasterConnectionsAsync(CancellationToken.None, discovery: null).AsTask();
                live.AddRange(await Task.WhenAll(subscribing).WaitAsync(TimeSpan.FromSeconds(20)));
                _ = await refresh.WaitAsync(TimeSpan.FromSeconds(10));
                // Drop half of the subscriptions while later topology passes may still run.
                foreach (var subscription in live.Take(live.Count / 2).ToArray())
                {
                    await subscription.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                    live.Remove(subscription);
                }
            }
            foreach (var subscription in live)
                await Assert.That(subscription.Completion.IsCompleted).IsFalse();
        }
        finally
        {
            foreach (var subscription in live)
                await subscription.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }

        // Every route, node and coverage entry is released once the last subscription ends.
        var hub = client.Core.Hub!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (hub.ClusterNotificationNodeCount != 0 || hub.ClusterNotificationCoverageCount != 0)
            await Task.Delay(10, deadline.Token);
    }

    private static void Configure(FakeRespServer server, byte[] topology, bool resp3)
        => Configure(server, () => topology, resp3);

    private static void Configure(FakeRespServer server, Func<byte[]> topology, bool resp3, Action<string>? onCommand = null)
    {
        server.ReplyOverride = (_, command) =>
        {
            onCommand?.Invoke(command);
            if (command == "HELLO 3") return Hello;
            if (command == "INFO SERVER") return ClusterDatabaseTests.Info();
            if (command == "CLUSTER SLOTS") return topology();
            if (command.StartsWith("SUBSCRIBE ", StringComparison.Ordinal))
                return Confirmation("subscribe", command[10..], resp3);
            if (command.StartsWith("PSUBSCRIBE ", StringComparison.Ordinal))
                return Confirmation("psubscribe", command[11..], resp3);
            if (command.StartsWith("UNSUBSCRIBE ", StringComparison.Ordinal))
                return Confirmation("unsubscribe", command[12..], resp3);
            if (command.StartsWith("PUNSUBSCRIBE ", StringComparison.Ordinal))
                return Confirmation("punsubscribe", command[13..], resp3);
            return FakeRespServer.OkReply;
        };
    }

    private static byte[] Topology(int first, int second, int third)
        => Encoding.ASCII.GetBytes($"*3\r\n*3\r\n:0\r\n:5460\r\n*2\r\n$9\r\n127.0.0.1\r\n:{first}\r\n"
            + $"*3\r\n:5461\r\n:10922\r\n*2\r\n$9\r\n127.0.0.1\r\n:{second}\r\n"
            + $"*3\r\n:10923\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{third}\r\n");

    private static byte[] Topology(int first, int second)
        => Encoding.ASCII.GetBytes($"*2\r\n*3\r\n:0\r\n:8191\r\n*2\r\n$9\r\n127.0.0.1\r\n:{first}\r\n"
            + $"*3\r\n:8192\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{second}\r\n");

    private static byte[] SinglePrimaryTopology(int port)
        => Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{port}\r\n");

    private static byte[] PartialTopology(int port)
        => Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:8192\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{port}\r\n");

    private static RespireClient CreateClusterClient(
        int port, bool resp3, RespireReconnectPolicy? reconnectPolicy = null)
        => RespireClient.Create(new RespireOptions
        {
            UseCluster = true,
            Protocol = resp3 ? RespProtocol.Resp3 : RespProtocol.Resp2,
            Connections = 1,
            ReconnectPolicy = reconnectPolicy,
            Endpoints = [new("127.0.0.1", port)],
        });

    private static byte[] Confirmation(string verb, string channel, bool resp3)
        => Encoding.ASCII.GetBytes($"{(resp3 ? '>' : '*')}3\r\n${verb.Length}\r\n{verb}\r\n${Encoding.UTF8.GetByteCount(channel)}\r\n{channel}\r\n:1\r\n");

    private static byte[] Data(RespireChannel descriptor, bool resp3, string payload)
    {
        var channel = "__keyspace@0__:tenant:key";
        return Encoding.ASCII.GetBytes($"{(resp3 ? '>' : '*')}4\r\n$8\r\npmessage\r\n${Encoding.UTF8.GetByteCount(descriptor.ToString())}\r\n{descriptor}\r\n${channel.Length}\r\n{channel}\r\n${payload.Length}\r\n{payload}\r\n");
    }
}
