using System.Collections.Concurrent;
using System.Text;
using Respire.Internal;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public partial class ReadDedicatedRoutingTests
{
    [Test]
    public async Task FixtureStartupWaitsForSentinelRevalidation()
    {
        await using var primary = Node("primary", false);
        await using var sentinel = Sentinel(primary, () => []);
        var discoveries = 0;
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sentinel.SuppressReply = command =>
        {
            if (!command.StartsWith("SENTINEL GET-MASTER-ADDR-BY-NAME ")
                || Interlocked.Increment(ref discoveries) == 1) return false;
            blocked.TrySetResult();
            return true;
        };
        await using var client = await RespireClient.ConnectAsync(Options(sentinel, [], RespireReadFrom.Nearest)
            with { SentinelPrimaryName = "primary" });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await blocked.Task.WaitAsync(deadline.Token);
        while (client.Core.Sentinel!.SubscribedSentinelCount == 0) await Task.Delay(5, deadline.Token);
        var ready = SentinelTestSetup.WaitForStartupAsync(client);
        try { await Assert.That(ready.IsCompleted).IsFalse(); }
        finally
        {
            var index = sentinel.ReceivedCommands.ToList()
                .FindLastIndex(command => command.StartsWith("SENTINEL GET-MASTER-ADDR-BY-NAME "));
            var connection = sentinel.ReceivedConnectionIds[index];
            sentinel.SuppressReply = null;
            await sentinel.SendRawAsync(sentinel.ReplyOverride!(connection, sentinel.ReceivedCommands[index])!, connection);
        }
        await ready.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(client.Core.Sentinel!.SubscribedSentinelCount).IsEqualTo(1);
    }

    [Test]
    public async Task NearestCoolsPrimaryWhenDiscoveryFailsBeforePoolAcquisition()
    {
        await using var primary = Node("primary", false);
        await using var replica = Node("replica", true);
        await using var sentinel = Sentinel(primary, () => [replica]);
        await using var client = await RespireClient.ConnectAsync(Options(sentinel, [], RespireReadFrom.Nearest)
            with { SentinelPrimaryName = "primary" });
        await SentinelTestSetup.WaitForStartupAsync(client);
        var router = client.Core.ReadRouter;
        await router.RefreshNowAsync(default);
        var generation = client.Core.Sentinel!.Current!;
        router.NearestLatency = new ReadLatencySampler<RespireConnection>(
            (connection, _) => ValueTask.FromResult(connection.Port == primary.Port ? 1L : 10L), () => 0L);
        var selected = await router.SelectAsync(RespireReadFrom.Nearest, default);
        await Assert.That(selected.Primary).IsSameReferenceAs(generation.Multiplexer);
        var originalReply = sentinel.ReplyOverride!;
        var discoveries = 0;
        sentinel.ReplyOverride = (id, command) =>
        {
            if (!command.StartsWith("SENTINEL GET-MASTER-ADDR-BY-NAME ")) return originalReply(id, command);
            Interlocked.Increment(ref discoveries);
            return "*-1\r\n"u8.ToArray();
        };
        // Place retirement exactly between endpoint selection and primary lease acquisition.
        // The primary helper must rediscover before it can return a pool.
        generation.TryRetire();
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var first = await router.TryRentPrimaryReadAsync(caller.Token, default, allowFallback: true,
            preferredZone: null, nearestPrimary: selected.Primary);
        await Assert.That(first.Pool).IsNull();
        await Assert.That(first.Failure).IsTypeOf<RespireConnectionException>();
        await Assert.That(discoveries).IsGreaterThan(0);
        await Assert.That(router.NearestLatency.CanConnect(generation.Multiplexer)).IsFalse();
        var previousDiscoveries = discoveries;
        var second = await router.RentDedicatedConnectionAsync(RespireReadFrom.Nearest, caller.Token);
        second.Pool.Return(second.Connection);
        await Assert.That(second.IsReplica).IsTrue();
        await Assert.That(discoveries).IsEqualTo(previousDiscoveries);
    }

    [Test]
    [Arguments(RespireReadFrom.ReplicaPreferred, true)]
    [Arguments(RespireReadFrom.AzAffinity, true)]
    [Arguments(RespireReadFrom.AzAffinityReplicasAndPrimary, true)]
    [Arguments(RespireReadFrom.Nearest, true)]
    [Arguments(RespireReadFrom.Nearest, false)]
    public async Task ZeroCooldownDoesNotRetryFailedDedicatedEndpoints(RespireReadFrom policy, bool allReplicasFail)
    {
        await using var primary = Node("primary", false);
        await using var first = Node("first", true);
        await using var second = Node("second", true);
        var connections = new ConcurrentDictionary<int, int>();
        var attempts = new ConcurrentDictionary<int, int>();
        await using var client = await RespireClient.ConnectAsync(Options(primary, [first, second], policy) with
        {
            ReplicaRefreshInterval = TimeSpan.Zero,
            ClientAvailabilityZone = ReadFallbackPolicy.UsesAvailabilityZone(policy) ? "local" : null,
            TestingStreamFactory = OpenStreamAsync,
        });
        client.Core.ReadRouter.NearestLatency = new ReadLatencySampler<RespireConnection>(
            (connection, _) => ValueTask.FromResult(connection.Port == first.Port ? 1L
                : connection.Port == second.Port ? 10L : 100L), () => 0L);
        using var reply = await client.ExecuteAsync(RespireCommands.Stream.XREAD,
            ["BLOCK", 1, "STREAMS", "key", "0"]).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(reply.AsString()).IsEqualTo(allReplicasFail ? "primary" : "second");
        await Assert.That(attempts[first.Port]).IsEqualTo(1);
        await Assert.That(attempts[second.Port]).IsEqualTo(1);

        async ValueTask<Stream> OpenStreamAsync(string host, int port, CancellationToken token)
        {
            if (port != primary.Port && connections.AddOrUpdate(port, 1, (_, count) => count + 1) > 1)
            {
                attempts.AddOrUpdate(port, 1, (_, count) => count + 1);
                if (allReplicasFail || port == first.Port)
                    throw new RespireConnectionException("Dedicated candidate unavailable.");
            }
            return await OpenSocketAsync(host, port, token);
        }
    }

    [Test]
    public async Task NearestUsesReplicasDiscoveredDuringFailedPrimaryRental()
    {
        await using var primary = Node("primary", false);
        await using var replica = Node("replica", true);
        await using var sentinel = Sentinel(primary, () => [replica]);
        var discoveryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sentinel.SuppressReply = command =>
        {
            if (!command.StartsWith("SENTINEL REPLICAS ")) return false;
            discoveryStarted.TrySetResult();
            return true;
        };
        var primaryConnections = 0;
        var rentalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failRental = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var client = await RespireClient.ConnectAsync(Options(sentinel, [], RespireReadFrom.Nearest) with
        {
            SentinelPrimaryName = "primary", TestingStreamFactory = OpenStreamAsync,
        });
        var router = client.Core.ReadRouter;
        router.NearestLatency = new ReadLatencySampler<RespireConnection>(
            (connection, _) => ValueTask.FromResult(connection.Port == primary.Port ? 1L : 10L), () => 0L);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var read = client.ExecuteAsync(RespireCommands.Stream.XREAD,
            ["BLOCK", 1, "STREAMS", "key", "0"], cancellationToken: deadline.Token).AsTask();
        await discoveryStarted.Task.WaitAsync(deadline.Token);
        await rentalStarted.Task.WaitAsync(deadline.Token);
        sentinel.SuppressReply = null;
        var index = sentinel.ReceivedCommands.ToList().FindIndex(command => command.StartsWith("SENTINEL REPLICAS "));
        await sentinel.SendRawAsync(Encoding.ASCII.GetBytes(ReplicaReply([replica])), sentinel.ReceivedConnectionIds[index]);
        await router.RefreshNowAsync(deadline.Token);
        failRental.TrySetResult();
        using var reply = await read.WaitAsync(deadline.Token);
        await Assert.That(reply.AsString()).IsEqualTo("replica");
        await Assert.That(primaryConnections).IsEqualTo(2);

        async ValueTask<Stream> OpenStreamAsync(string host, int port, CancellationToken token)
        {
            if (port == primary.Port && Interlocked.Increment(ref primaryConnections) > 1)
            {
                rentalStarted.TrySetResult();
                await failRental.Task.WaitAsync(token);
                throw new RespireConnectionException("Dedicated primary unavailable after replica discovery.");
            }
            return await OpenSocketAsync(host, port, token);
        }
    }
}
