using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Respire.Commands;
using Respire.Infrastructure;
using Respire.Internal;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClusterRetirementTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReplacementDrainsAcceptedWorkAndKeepsNewGeneration(bool dedicated)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commands = 0;
        await using var server = new FakeRespServer(4, FakeRespServer.PongReply)
        {
            SuppressReply = _ => { if (Interlocked.Increment(ref commands) != 1) return false; received.TrySetResult(); return true; },
        };
        await using var client = CreateClient();
        var router = client.Core.Cluster!;
        var endpoint = new RespireEndpoint("127.0.0.1", server.Port);
        Publish(router, endpoint, "old", 1);
        var old = router.GetMultiplexer(endpoint);
        await old.EnsureConnectedAsync();
        var pool = router.GetDedicatedPool(endpoint);
        var connection = dedicated ? await pool.RentAsync(default) : old.GetConnection();
        var pending = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        await received.Task.WaitAsync(Limit);
        Publish(router, endpoint, "new", 2);
        var current = router.GetMultiplexer(endpoint);
        await Assert.That(ReferenceEquals(old, current)).IsFalse();
        await Assert.That(old.IsRetired).IsTrue();
        await Assert.That(connection.IsConnected).IsTrue();
        await Assert.That(router.WaitForRetirementAsync().IsCompleted).IsFalse();
        await Assert.That(ReferenceEquals(pool, router.GetDedicatedPool(endpoint))).IsFalse();
        await current.EnsureConnectedAsync();
        using (var reply = await current.SendAsync(new RawCommand(FakeRespServer.PingFrame)))
            await Assert.That(reply.AsString()).IsEqualTo("PONG");
        await server.SendRawAsync(FakeRespServer.PongReply, dedicated ? 1 : 0);
        using (var reply = await pending.WaitAsync(Limit))
            await Assert.That(reply.AsString()).IsEqualTo("PONG");
        if (dedicated) pool.Return(connection);
        await router.WaitForRetirementAsync().WaitAsync(Limit);
        await Assert.That(connection.IsConnected).IsFalse();
        await Assert.That(ReferenceEquals(current, router.GetMultiplexer(endpoint))).IsTrue();
        await Assert.That(Count(router, "_retiringNodes")).IsEqualTo(0);
        await Assert.That(RetainedNodes(router)).IsEqualTo(2);
    }

    [Test]
    public async Task RepeatedCompletedChurnBoundsIndexesHandlersAndPools()
    {
        await using var server = new FakeRespServer(32, FakeRespServer.PongReply);
        await using var client = CreateClient();
        var router = client.Core.Cluster!;
        var endpoint = new RespireEndpoint("127.0.0.1", server.Port);
        for (var generation = 1; generation <= 12; generation++)
        {
            Publish(router, endpoint, $"node-{generation}", generation);
            await router.WaitForRetirementAsync().WaitAsync(Limit);
            var node = router.GetMultiplexer(endpoint);
            await node.EnsureConnectedAsync();
            var pool = router.GetDedicatedPool(endpoint);
            using (var ready = await node.SendAsync(new RawCommand(FakeRespServer.PingFrame)))
                await Assert.That(ready.AsString()).IsEqualTo("PONG");
            var borrowed = await pool.RentAsync(default);
            using (var ready = await borrowed.SendAsync(new RawCommand(FakeRespServer.PingFrame)))
                await Assert.That(ready.AsString()).IsEqualTo("PONG");
            pool.Return(borrowed);
            await using var correction = router.GetCorrectionLease(node.GetConnection());
            await Assert.That(RetainedNodes(router)).IsEqualTo(2);
            await Assert.That(Count(router, "_nodeStateHandlers")).IsEqualTo(1);
            await Assert.That(Count(router, "_dedicatedPools")).IsEqualTo(1);
            await Assert.That(Count(router, "_correctionPools")).IsEqualTo(1);
            await Assert.That(Count(router, "_correctionStateHandlers")).IsEqualTo(1);
            await Assert.That(Count(router, "_ownedPools")).IsEqualTo(2);
            var identities = Identities(router);
            await Assert.That(identities.NodeIdCount).IsEqualTo(1);
            await Assert.That(identities.ReverseNodeIdCount).IsEqualTo(1);
            await Assert.That(identities.Endpoints.Count()).IsEqualTo(3); // seed, preferred, advertised alias
        }
        await Assert.That(client.Core.Multiplexer.IsRetired).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ClientDisposalAbortsRetiringAcceptedWork(bool dedicated)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(2, FakeRespServer.PongReply)
        {
            SuppressReply = _ => { received.TrySetResult(); return true; },
        };
        await using var client = CreateClient();
        var router = client.Core.Cluster!;
        var endpoint = new RespireEndpoint("127.0.0.1", server.Port);
        Publish(router, endpoint, "old", 1);
        var node = router.GetMultiplexer(endpoint);
        await node.EnsureConnectedAsync();
        var pool = router.GetDedicatedPool(endpoint);
        var connection = dedicated ? await pool.RentAsync(default) : node.GetConnection();
        var pending = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        await received.Task.WaitAsync(Limit);
        Publish(router, endpoint, "new", 2);
        await client.DisposeAsync().AsTask().WaitAsync(Limit);
        await Assert.That(async () => await pending).Throws<RespireConnectionException>();
        await router.WaitForRetirementAsync().WaitAsync(Limit);
        await Assert.That(connection.IsConnected).IsFalse();
    }

    [Test]
    public async Task FailedDedicatedCleanupRemainsOwnedAndFaultsGenerationRetirement()
    {
        await using var server = new FakeRespServer(2, FakeRespServer.PongReply);
        using var logger = new FailingPoolDisconnectLogger();
        await using var client = CreateClient(logger);
        var router = client.Core.Cluster!;
        var endpoint = new RespireEndpoint("127.0.0.1", server.Port);
        Publish(router, endpoint, "old", 1);
        var node = router.GetMultiplexer(endpoint);
        await node.EnsureConnectedAsync();
        using (var ready = await node.SendAsync(new RawCommand(FakeRespServer.PingFrame)))
            await Assert.That(ready.AsString()).IsEqualTo("PONG");
        var pool = router.GetDedicatedPool(endpoint);
        var borrowed = await pool.RentAsync(default);
        using (var ready = await borrowed.SendAsync(new RawCommand(FakeRespServer.PingFrame)))
            await Assert.That(ready.AsString()).IsEqualTo("PONG");
        Publish(router, endpoint, "new", 2);
        var retirement = router.WaitForRetirementAsync();
        pool.Return(borrowed);
        var error = await Assert.That(async () => await retirement.WaitAsync(Limit)).ThrowsExactly<InvalidOperationException>();
        await Assert.That(error).IsSameReferenceAs(logger.Failure);
        await Assert.That(Count(router, "_retiringNodes")).IsEqualTo(1);
        await Assert.That(Count(router, "_ownedPools")).IsEqualTo(1);
        await Assert.That(async () => await client.DisposeAsync().AsTask().WaitAsync(Limit)).ThrowsExactly<InvalidOperationException>();
        await Assert.That(borrowed.IsConnected).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FenceDeadlineIsDistinctFromCallerCancellation(bool cancelCaller)
    {
        var killSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(2, ":42\r\n"u8.ToArray())
        {
            SuppressReply = command =>
            {
                if (command != "CLIENT KILL ID 42") return false;
                killSeen.TrySetResult();
                return true;
            },
        };
        await using var node = RespireConnectionMultiplexer.Create("unresolvable.invalid",
            options: new RespireConnectionOptions { ConnectTimeout = cancelCaller ? Limit : TimeSpan.FromMilliseconds(100) });
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port);
        await connection.EnsureServerClientIdAsync();
        InstallPhysicalConnection(node, connection);
        await connection.DisposeAsync();
        using var cancel = new CancellationTokenSource();
        var fencing = node.FenceRetiredConnectionsAsync(cancel.Token).AsTask();
        await killSeen.Task.WaitAsync(Limit);
        if (cancelCaller)
        {
            cancel.Cancel();
            await Assert.That(async () => await fencing.WaitAsync(Limit)).Throws<OperationCanceledException>();
        }
        else
        {
            var error = await Assert.That(async () => await fencing.WaitAsync(Limit)).ThrowsExactly<RespireTimeoutException>();
            await Assert.That(error!.Message).Contains("CLIENT KILL");
        }
        await Assert.That(node.HasPendingCorrectionFences).IsTrue();
    }

    [Test]
    public async Task LateFenceOfDrainedSocketDoesNotContactReplacementWithSameClientId()
    {
        await using var server = new FakeRespServer(3, ":42\r\n"u8.ToArray());
        await using var client = CreateClient();
        var router = client.Core.Cluster!;
        var endpoint = new RespireEndpoint("127.0.0.1", server.Port);
        Publish(router, endpoint, "old", 1);
        var node = router.GetMultiplexer(endpoint);
        await node.EnsureConnectedAsync();
        var original = node.GetConnection();
        await original.EnsureServerClientIdAsync();
        Publish(router, endpoint, "new", 2);
        await router.WaitForRetirementAsync().WaitAsync(Limit);
        var replacement = router.GetMultiplexer(endpoint);
        await replacement.EnsureConnectedAsync();
        await replacement.GetConnection().EnsureServerClientIdAsync();
        var identity = new RespireClient.TrackedConnectionIdentity(endpoint, 42, Connection: original);
        await client.FenceCorrectionConnectionAsync(identity).AsTask().WaitAsync(Limit);
        await Assert.That(replacement.GetConnection().IsConnected).IsTrue();
        await Assert.That(original.DrainedSuccessfully).IsTrue();
        await Assert.That(server.ReceivedCommands).DoesNotContain("CLIENT KILL ID 42");
        await Assert.That(Count(router, "_correctionPools")).IsEqualTo(0);
        await Assert.That(Count(router, "_ownedPools")).IsEqualTo(0);
    }

    [Test]
    public async Task CorrectionReservationSurvivesTopologyRemovalBeforeRent()
    {
        await using var server = new FakeRespServer(2, FakeRespServer.PongReply);
        await using var client = CreateClient();
        var router = client.Core.Cluster!;
        var endpoint = new RespireEndpoint("127.0.0.1", server.Port);
        Publish(router, endpoint, "old", 1);
        var old = router.GetMultiplexer(endpoint);
        await old.EnsureConnectedAsync();
        // Complete a round trip before retiring: TCP connect can finish before the fake
        // listener accepts, and Windows AcceptEx can fail if that socket closes first.
        using (var ready = await old.SendAsync(new RawCommand(FakeRespServer.PingFrame)))
            await Assert.That(ready.AsString()).IsEqualTo("PONG");
        var lease = router.GetCorrectionLease(old.GetConnection());
        Publish(router, endpoint, "new", 2);
        await router.WaitForRetirementAsync().WaitAsync(Limit);
        var control = await lease.Pool.RentAsync(default);
        using (var reply = await control.SendAsync(new RawCommand(FakeRespServer.PingFrame)))
            await Assert.That(reply.AsString()).IsEqualTo("PONG");
        lease.Pool.Return(control);
        await lease.DisposeAsync();
        await Assert.That(control.IsConnected).IsFalse();
        await Assert.That(Count(router, "_ownedPools")).IsEqualTo(0);
    }

    [Test]
    public async Task PhysicalPeerChangesPrunePoolsButSamePeerReconnectReusesPool()
    {
        await using var firstServer = new FakeRespServer(4, ":42\r\n"u8.ToArray());
        await using var secondServer = new FakeRespServer(2, ":42\r\n"u8.ToArray());
        await using var client = CreateClient();
        var router = client.Core.Cluster!;
        var endpoint = new RespireEndpoint("dns.invalid");
        Publish(router, endpoint, "stable", 1);
        var node = router.GetMultiplexer(endpoint);
        await using var original = await RespireConnection.ConnectAsync("127.0.0.1", firstServer.Port);
        await original.EnsureServerClientIdAsync();
        InstallPhysicalConnection(node, original);
        var firstLease = router.GetCorrectionLease(original);
        var firstPool = firstLease.Pool;
        var control = await firstPool.RentAsync(default);
        using (var ready = await control.SendAsync(new RawCommand(FakeRespServer.PingFrame)))
            await Assert.That(ready.AsInteger()).IsEqualTo(42);
        firstPool.Return(control);
        await firstLease.DisposeAsync();
        await using var reconnected = await RespireConnection.ConnectAsync("127.0.0.1", firstServer.Port);
        await reconnected.EnsureServerClientIdAsync();
        InstallPhysicalConnection(node, reconnected);
        await using (var samePeer = router.GetCorrectionLease(reconnected))
            await Assert.That(ReferenceEquals(samePeer.Pool, firstPool)).IsTrue();
        await using var replacement = await RespireConnection.ConnectAsync("127.0.0.1", secondServer.Port);
        await replacement.EnsureServerClientIdAsync();
        InstallPhysicalConnection(node, replacement);
        var handlers = (Action<int, RespireConnectionStateChange>?)typeof(RespireConnectionMultiplexer)
            .GetField("SlotStateChanged", Private)!.GetValue(node);
        handlers?.Invoke(0, new(endpoint, RespireConnectionState.Connected, null));
        await Assert.That(Count(router, "_correctionPools")).IsEqualTo(0);
        await Assert.That(Count(router, "_correctionStateHandlers")).IsEqualTo(0);
        await using (var newPeer = router.GetCorrectionLease(replacement))
            await Assert.That(ReferenceEquals(newPeer.Pool, firstPool)).IsFalse();
        // Await the already-started cleanup rather than depending on background timing.
        await firstPool.RetireAsync();
        await Assert.That(control.IsConnected).IsFalse();
        await client.FenceCorrectionConnectionAsync(new(endpoint, 42, Connection: original));
        await Assert.That(firstServer.ReceivedCommands).Contains("CLIENT KILL ID 42");
        await Assert.That(secondServer.ReceivedCommands).DoesNotContain("CLIENT KILL ID 42");
        await Assert.That(replacement.IsConnected).IsTrue();
        await Assert.That(Count(router, "_correctionPools")).IsEqualTo(1);
    }

    [Test]
    public async Task RetirementFenceUsesCapturedNetworkPeerInsteadOfResolvingHostname()
    {
        await using var server = new FakeRespServer(2, ":42\r\n"u8.ToArray());
        await using var node = RespireConnectionMultiplexer.Create("unresolvable.invalid");
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port);
        await connection.EnsureServerClientIdAsync();
        InstallPhysicalConnection(node, connection);
        await connection.DisposeAsync();
        await node.RetireAsync().WaitAsync(Limit);
        await Assert.That(server.ReceivedCommands).Contains("CLIENT KILL ID 42");
        await Assert.That(node.HasPendingCorrectionFences).IsFalse();
    }

    [Test]
    public async Task FailedFenceRetainsGenerationUntilAcknowledged()
    {
        var firstSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retrySeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var kills = 0;
        await using var server = new FakeRespServer(4, ":42\r\n"u8.ToArray(), "-ERR try again\r\n"u8.ToArray())
        {
            SuppressReply = command =>
            {
                if (command != "CLIENT KILL ID 42") return false;
                if (Interlocked.Increment(ref kills) == 1) firstSeen.TrySetResult();
                else retrySeen.TrySetResult();
                return true;
            },
        };
        await using var client = CreateClient();
        var router = client.Core.Cluster!;
        var endpoint = new RespireEndpoint("127.0.0.1", server.Port);
        Publish(router, endpoint, "old", 1);
        var old = router.GetMultiplexer(endpoint);
        await old.EnsureConnectedAsync();
        var connection = old.GetConnection();
        await connection.EnsureServerClientIdAsync();
        await connection.DisposeAsync();
        Publish(router, endpoint, "new", 2);
        await firstSeen.Task.WaitAsync(Limit);
        await server.SendRawAsync("-ERR try again\r\n"u8.ToArray(), 1);
        await retrySeen.Task.WaitAsync(Limit);
        await Assert.That(old.HasPendingCorrectionFences).IsTrue();
        await Assert.That(RetainedNodes(router)).IsEqualTo(3);
        await Assert.That(router.WaitForRetirementAsync().IsCompleted).IsFalse();
        await server.SendRawAsync(":1\r\n"u8.ToArray(), 2);
        await router.WaitForRetirementAsync().WaitAsync(Limit);
        await Assert.That(RetainedNodes(router)).IsEqualTo(2);
        await Assert.That(old.HasPendingCorrectionFences).IsFalse();
        // The shared retirement task still contains its first failure after the router's
        // successful fence retry. A later correction must use the now-safe original peer.
        await Assert.That(old.RetireAsync().IsFaulted).IsTrue();
        await client.ExecuteOnAllConnectionsAsync(RespireScript.Create("return 1"), ["key"], [],
            new(endpoint, 42, Connection: connection)).AsTask().WaitAsync(Limit);
        await Assert.That(server.ReceivedCommands).Contains("EVAL return 1 1 key");
        await Assert.That(kills).IsEqualTo(2);
        await Assert.That(Count(router, "_ownedPools")).IsEqualTo(0);
    }

    // Model DNS resolution changes without mutating machine-wide DNS. Every installed
    // connection has a real socket, captured network peer and server-local client identity.
    private static void InstallPhysicalConnection(RespireConnectionMultiplexer node, RespireConnection connection)
    {
        var connections = (RespireConnection?[])typeof(RespireConnectionMultiplexer).GetField("_connections", Private)!.GetValue(node)!;
        connections[0] = connection;
        connection.Multiplexer = node;
    }

    private static RespireClient CreateClient(ILoggerFactory? loggerFactory = null) => RespireClient.Create(new RespireOptions
    {
        UseCluster = true, Connections = 1, Endpoints = { new RespireEndpoint("seed.invalid") },
        LoggerFactory = loggerFactory,
    });

    private sealed class FailingPoolDisconnectLogger : ILoggerFactory, ILogger
    {
        internal readonly InvalidOperationException Failure = new("Test dedicated disconnect failure.");
        public ILogger CreateLogger(string categoryName) => categoryName.Contains(".Blocking.", StringComparison.Ordinal) ? this : NullLogger.Instance;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Debug && formatter(state, exception).StartsWith("Disconnected from", StringComparison.Ordinal))
                throw Failure;
        }
    }

    private static ClusterNodeIdentityIndex Identities(ClusterRouter router)
        => (ClusterNodeIdentityIndex)typeof(ClusterRouter).GetField("_identities", Private)!.GetValue(router)!;

    private static int RetainedNodes(ClusterRouter router)
    {
        lock (router.NodeStateGate) return Identities(router).All.Count();
    }

    private static int Count(ClusterRouter router, string field)
    {
        lock (router.NodeStateGate)
        {
            var value = typeof(ClusterRouter).GetField(field, Private)!.GetValue(router)!;
            return (int)value.GetType().GetProperty("Count")!.GetValue(value)!;
        }
    }

    private static void Publish(ClusterRouter router, RespireEndpoint endpoint, string id, long generation)
    {
        var version = (long)typeof(ClusterRouter).GetField("_topologyVersion", Private)!.GetValue(router)!;
        List<ClusterTopologyRange> ranges = [new(0, 16383, endpoint, id, [new("alias.invalid", endpoint.Port)])];
        typeof(ClusterRouter).GetMethod("ApplyTopology", Private)!.Invoke(router, [ranges, version, generation]);
    }
}

