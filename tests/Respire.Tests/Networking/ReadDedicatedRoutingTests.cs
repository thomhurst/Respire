using System.Text;
using System.Runtime.CompilerServices;
using Respire.Internal;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public partial class ReadDedicatedRoutingTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LazyPrimaryDeadlineFallsBackUnlessCallerCancels(bool cancelCaller)
    {
        await using var primary = Node("primary", false);
        await using var replica = Node("replica", true);
        var connecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var client = RespireClient.Create(Options(primary, [replica], RespireReadFrom.PrimaryPreferred) with
        {
            TestingStreamFactory = OpenStreamAsync,
            ConnectTimeout = TimeSpan.FromMilliseconds(200),
        });
        using var caller = new CancellationTokenSource();
        var read = client.ExecuteAsync(RespireCommands.Stream.XREAD,
            ["BLOCK", 1, "STREAMS", "key", "0"], cancellationToken: caller.Token).AsTask();
        await connecting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (cancelCaller)
        {
            caller.Cancel();
            var error = await Assert.That(async () => await read).Throws<OperationCanceledException>();
            await Assert.That(error!.CancellationToken).IsEqualTo(caller.Token);
            await Assert.That(replica.CommandsSeen).IsEqualTo(0);
        }
        else
        {
            using var reply = await read.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(reply.AsString()).IsEqualTo("replica");
            await Assert.That(caller.IsCancellationRequested).IsFalse();
        }

        async ValueTask<Stream> OpenStreamAsync(string host, int port, CancellationToken token)
        {
            if (port == primary.Port)
            {
                connecting.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return await OpenSocketAsync(host, port, token);
        }
    }

    [Test]
    [Arguments(RespireReadFrom.Replica, false)]
    [Arguments(RespireReadFrom.ReplicaPreferred, false)]
    [Arguments(RespireReadFrom.Nearest, false)]
    [Arguments(RespireReadFrom.Replica, true)]
    [Arguments(RespireReadFrom.ReplicaPreferred, true)]
    [Arguments(RespireReadFrom.Nearest, true)]
    public async Task DedicatedAcquisitionTriesEveryReplica(RespireReadFrom policy, bool allReplicasFail)
    {
        await using var primary = Node("primary", false);
        var replicas = Enumerable.Range(0, 8).Select(_ => Node("replica", true)).ToArray();
        var connections = new System.Collections.Concurrent.ConcurrentDictionary<int, int>();
        var attempts = 0;
        try
        {
            await using var client = await RespireClient.ConnectAsync(Options(primary, replicas, policy) with
            {
                TestingStreamFactory = OpenStreamAsync,
            });
            client.Core.ReadRouter.FailedReplicaCooldown = TimeSpan.FromMinutes(1);
            client.Core.ReadRouter.NearestLatency = new ReadLatencySampler<RespireConnection>(
                (connection, _) => ValueTask.FromResult(connection.Port == primary.Port ? 100L : 1L), () => 0L);
            Task<RespireResult> ReadAsync() => client.ExecuteAsync(RespireCommands.Stream.XREAD,
                ["BLOCK", 1, "STREAMS", "key", "0"]).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            if (allReplicasFail && policy == RespireReadFrom.Replica)
                await Assert.That(async () => await ReadAsync()).Throws<RespireConnectionException>();
            else
            {
                using var reply = await ReadAsync();
                await Assert.That(reply.AsString()).IsEqualTo(allReplicasFail ? "primary" : "replica");
            }
            await Assert.That(attempts).IsEqualTo(8);
        }
        finally
        {
            foreach (var replica in replicas) await replica.DisposeAsync();
        }

        async ValueTask<Stream> OpenStreamAsync(string host, int port, CancellationToken token)
        {
            if (port != primary.Port && connections.AddOrUpdate(port, 1, (_, count) => count + 1) > 1)
            {
                var attempt = Interlocked.Increment(ref attempts);
                if (allReplicasFail || attempt < 8) throw new RespireConnectionException("Dedicated handshake unavailable.");
            }
            return await OpenSocketAsync(host, port, token);
        }
    }

    [Test]
    public async Task NearestCoolsThePrimaryWhoseReplacementPoolFailed()
    {
        await using var oldPrimary = Node("old", false);
        await using var newPrimary = Node("new", false);
        await using var replica = Node("replica", true);
        var currentPrimary = oldPrimary;
        await using var sentinel = Sentinel(oldPrimary, () => [replica]);
        var originalReply = sentinel.ReplyOverride!;
        sentinel.ReplyOverride = (id, command) => command.StartsWith("SENTINEL GET-MASTER-ADDR-BY-NAME ")
            ? Encoding.ASCII.GetBytes($"*2\r\n$9\r\n127.0.0.1\r\n${currentPrimary.Port.ToString().Length}\r\n{currentPrimary.Port}\r\n")
            : originalReply(id, command);
        var oldConnections = 0;
        var newConnections = 0;
        var oldRental = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var client = await RespireClient.ConnectAsync(Options(sentinel, [], RespireReadFrom.Nearest) with
        {
            SentinelPrimaryName = "primary", TestingStreamFactory = OpenStreamAsync,
            ConnectTimeout = TimeSpan.FromSeconds(2),
        });
        var router = client.Core.ReadRouter;
        router.NearestLatency = new ReadLatencySampler<RespireConnection>(
            (connection, _) => ValueTask.FromResult(connection.Port == replica.Port ? 100L : 1L), () => 0L);
        await router.RefreshNowAsync(default);
        var previous = client.Core.Sentinel!.Current!;
        var previousPool = previous.Pool;
        var read = client.ExecuteAsync(RespireCommands.Stream.XREAD,
            ["BLOCK", 1, "STREAMS", "key", "0"]).AsTask();
        await oldRental.Task.WaitAsync(TimeSpan.FromSeconds(5));
        currentPrimary = newPrimary;
        previous.TryRetire();
        var current = await client.Core.Sentinel.GetGenerationAsync(default);
        var retirement = previousPool.RetireAsync().AsTask();
        releaseOld.TrySetResult();
        using var reply = await read.WaitAsync(TimeSpan.FromSeconds(10));
        await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(reply.AsString()).IsEqualTo("replica");
        await Assert.That(newConnections).IsEqualTo(2); // One shared connection and one failed dedicated attempt.
        await Assert.That(router.NearestLatency.CanConnect(current.Multiplexer)).IsFalse();
        await Assert.That(router.NearestLatency.CanConnect(previous.Multiplexer)).IsTrue();

        async ValueTask<Stream> OpenStreamAsync(string host, int port, CancellationToken token)
        {
            if (port == oldPrimary.Port && Interlocked.Increment(ref oldConnections) > 1)
            {
                oldRental.TrySetResult();
                await releaseOld.Task.WaitAsync(token);
                throw new OperationCanceledException("The selected pool retired before admission.");
            }
            if (port == newPrimary.Port && Interlocked.Increment(ref newConnections) > 1)
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return await OpenSocketAsync(host, port, token);
        }
    }

    private static async ValueTask<Stream> OpenSocketAsync(string host, int port, CancellationToken token)
    {
        var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(host, port, token);
            return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
        }
        catch { socket.Dispose(); throw; }
    }

    [Test]
    [Arguments(true, RespireReadFrom.Replica, "replica")]
    [Arguments(true, RespireReadFrom.ReplicaPreferred, "replica")]
    [Arguments(false, RespireReadFrom.PrimaryPreferred, "primary")]
    [Arguments(true, RespireReadFrom.PrimaryPreferred, "primary")]
    [Arguments(false, RespireReadFrom.Primary, "primary")]
    [Arguments(true, RespireReadFrom.Primary, "primary")]
    [Arguments(false, RespireReadFrom.Replica, "replica")]
    [Arguments(false, RespireReadFrom.ReplicaPreferred, "replica")]
    public async Task BlockingStandaloneReadSelectsEndpoint(bool sentinelMode, RespireReadFrom policy, string expected)
    {
        await using var primary = Node("primary", false);
        await using var replica = Node("replica", true);
        await using var sentinel = Sentinel(primary, () => [replica]);
        var options = Options(sentinelMode ? sentinel : primary, sentinelMode ? [] : [replica], policy)
            with { SentinelPrimaryName = sentinelMode ? "primary" : null };
        await using var client = await RespireClient.ConnectAsync(options);
        for (var index = 0; index < 3; index++)
        {
            using var reply = await client.ExecuteAsync(RespireCommands.Stream.XREAD, ["BLOCK", 1, "STREAMS", "key", "0"]);
            await Assert.That(reply.AsString()).IsEqualTo(expected);
        }
        var selected = expected == "replica" ? replica : primary;
        var other = expected == "replica" ? primary : replica;
        var readIds = selected.ReceivedCommands.Select((command, index) => (command, index))
            .Where(item => item.command.StartsWith("XREAD "))
            .Select(item => selected.ReceivedConnectionIds[item.index]).Distinct().ToArray();
        await Assert.That(readIds.Length).IsEqualTo(1); // Successful blocking reads reuse their dedicated lease.
        await Assert.That(other.ReceivedCommands.Any(command => command.StartsWith("XREAD "))).IsFalse();
        await Assert.That(replica.ReceivedCommands.Contains("READONLY")).IsFalse();
        using (await client.ExecuteAsync(RespireCommands.List.BLPOP, "key", 1)) { }
        await Assert.That(primary.ReceivedCommands.Count(command => command.StartsWith("BLPOP "))).IsEqualTo(1);
        await Assert.That(replica.ReceivedCommands.Any(command => command.StartsWith("BLPOP "))).IsFalse();
    }

    [Test]
    [Arguments(false, false, false)]
    [Arguments(false, true, false)]
    [Arguments(true, false, false)]
    [Arguments(true, true, false)]
    [Arguments(false, false, true)]
    [Arguments(true, false, true)]
    public async Task BlockingStandaloneFallbackSwitchesRoleOnlyOnce(bool primaryFirst, bool bothFail, bool fallbackDeadline)
    {
        await using var primary = Node("primary", false);
        await using var replica = Node("replica", true);
        var first = primaryFirst ? primary : replica;
        var second = primaryFirst ? replica : primary;
        var fallbackConnections = 0;
        RejectReads(first);
        if (bothFail) RejectReads(second);
        await using var client = await RespireClient.ConnectAsync(Options(primary, [replica],
            primaryFirst ? RespireReadFrom.PrimaryPreferred : RespireReadFrom.ReplicaPreferred) with { TestingStreamFactory = fallbackDeadline ? OpenStreamAsync : null });
        if (bothFail || fallbackDeadline)
        {
            var error = await Assert.That(async () => await client.ExecuteAsync(RespireCommands.Stream.XREAD,
                ["BLOCK", 1, "STREAMS", "key", "0"])).Throws<RespireServerException>();
            await Assert.That(error!.Message).Contains("LOADING unavailable");
        }
        else
        {
            using var reply = await client.ExecuteAsync(RespireCommands.Stream.XREAD, ["BLOCK", 1, "STREAMS", "key", "0"]);
            await Assert.That(reply.AsString()).IsEqualTo(primaryFirst ? "replica" : "primary");
        }
        await Assert.That(first.ReceivedCommands.Count(command => command.StartsWith("XREAD "))).IsEqualTo(1);
        await Assert.That(second.ReceivedCommands.Count(command => command.StartsWith("XREAD "))).IsEqualTo(fallbackDeadline ? 0 : 1);

        async ValueTask<Stream> OpenStreamAsync(string host, int port, CancellationToken token)
        {
            if (port == second.Port && Interlocked.Increment(ref fallbackConnections) > 1)
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(host, port, token);
                return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        }

        static void RejectReads(FakeRespServer node)
        {
            var previous = node.ReplyOverride!;
            node.ReplyOverride = (id, command) => command.StartsWith("XREAD ")
                ? "-LOADING unavailable\r\n"u8.ToArray() : previous(id, command);
        }
    }

    [Test]
    [Arguments(RespireReadFrom.PrimaryPreferred)]
    public async Task DedicatedPrimaryHandshakeFailureFallsBackToReplica(RespireReadFrom policy)
    {
        await using var primary = Node("primary", false);
        await using var replica = Node("replica", true);
        var previous = primary.ReplyOverride!;
        primary.ReplyOverride = (id, command) =>
        {
            if (id > 0 && command == "HELLO 3") primary.CloseConnection(id);
            return previous(id, command);
        };
        await using var client = await RespireClient.ConnectAsync(Options(primary, [replica], policy)
            with { Protocol = RespProtocol.Resp3 });
        using var reply = await client.ExecuteAsync(RespireCommands.Stream.XREAD, ["BLOCK", 1, "STREAMS", "key", "0"])
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(reply.AsString()).IsEqualTo("replica");
        await Assert.That(primary.ReceivedCommands.Any(command => command.StartsWith("XREAD "))).IsFalse();
        await Assert.That(client.Core.Multiplexer.GetConnection().IsConnected).IsTrue();
    }

    [Test]
    // Failures: remote close, independent connect deadline, and caller cancellation.
    [Arguments(false, true, 0)]
    [Arguments(true, true, 0)]
    [Arguments(false, true, 1)]
    [Arguments(true, true, 1)]
    [Arguments(false, true, 2)]
    [Arguments(true, true, 2)]
    [Arguments(false, false, 0)]
    [Arguments(true, false, 0)]
    [Arguments(false, false, 1)]
    [Arguments(true, false, 1)]
    [Arguments(false, false, 2)]
    [Arguments(true, false, 2)]
    public async Task NearestDedicatedFailureReselectsByLatencyAndRecovers(bool useSentinel, bool failPrimary, int failure)
    {
        await using var primary = Node("primary", false);
        await using var replica = Node("replica", true);
        await using var fast = Node("fast", true);
        await using var sentinel = Sentinel(primary, () => [replica, fast]);
        var failedNode = failPrimary ? primary : replica;
        var failDedicated = true;
        var failedNodeConnections = 0;
        var connecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previous = failedNode.ReplyOverride!;
        failedNode.ReplyOverride = (id, command) =>
        {
            if (failure == 0 && id > 0 && command == "HELLO 3" && Volatile.Read(ref failDedicated)) failedNode.CloseConnection(id);
            return previous(id, command);
        };
        await using var client = await RespireClient.ConnectAsync(
            Options(useSentinel ? sentinel : primary, useSentinel ? [] : [replica, fast], RespireReadFrom.Nearest)
            with
            {
                Protocol = RespProtocol.Resp3, SentinelPrimaryName = useSentinel ? "primary" : null,
                TestingStreamFactory = failure == 0 ? null : OpenStreamAsync,
            });
        long now = 0;
        var router = client.Core.ReadRouter;
        router.NearestLatency = new ReadLatencySampler<RespireConnection>((connection, _) =>
            ValueTask.FromResult(connection.Port == failedNode.Port ? 1L : connection.Port == fast.Port ? 10L : 100L),
            () => Volatile.Read(ref now));
        if (useSentinel) await router.RefreshNowAsync(default);
        var selected = await router.SelectAsync(RespireReadFrom.Nearest, default);
        await Assert.That(selected.Connection.Port).IsEqualTo(failedNode.Port);
        using var caller = new CancellationTokenSource();
        var read = ReadAsync(caller.Token);
        if (failure == 2)
        {
            await connecting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            caller.Cancel();
            var error = await Assert.That(async () => await read).Throws<OperationCanceledException>();
            await Assert.That(error!.CancellationToken).IsEqualTo(caller.Token);
        }
        else
        {
            using var reply = await read;
            await Assert.That(reply.AsString()).IsEqualTo("fast");
            await Assert.That(caller.IsCancellationRequested).IsFalse();
        }
        await Assert.That(primary.ReceivedCommands.Any(command => command.StartsWith("XREAD "))).IsFalse();
        await Assert.That(failedNode.ReceivedCommands.Count(command => command == "HELLO 3")).IsEqualTo(failure == 0 ? 2 : 1);
        if (failure != 0) await Assert.That(failedNodeConnections).IsEqualTo(2);
        await Assert.That(replica.ReceivedCommands.Any(command => command.StartsWith("XREAD "))).IsFalse();
        await Assert.That(fast.ReceivedCommands.Count(command => command.StartsWith("XREAD "))).IsEqualTo(failure == 2 ? 0 : 1);
        // A failed Sentinel socket invalidates its generation. A connection attempt canceled
        // before transport creation leaves the existing shared connection healthy.
        if (useSentinel && failPrimary && failure == 0) await Assert.That(client.Core.Sentinel!.Current!.IsRetired).IsTrue();
        else await Assert.That(selected.Connection.IsConnected).IsTrue();
        if (selected.Primary is { } primaryOwner)
            await Assert.That(router.NearestLatency.CanConnect(primaryOwner)).IsEqualTo(failure == 2);
        else await Assert.That(selected.Replica!.IsCoolingDown).IsEqualTo(failure != 2);

        Volatile.Write(ref failDedicated, false);
        Volatile.Write(ref now, ReadLatencySampler<RespireConnection>.IntervalMilliseconds);
        router.FailedReplicaCooldown = TimeSpan.Zero;
        using (var reply = await ReadAsync()) await Assert.That(reply.AsString()).IsEqualTo(failPrimary ? "primary" : "replica");
        await Assert.That(failedNode.ReceivedCommands.Count(command => command.StartsWith("XREAD "))).IsEqualTo(1);
        await Assert.That(fast.ReceivedCommands.Count(command => command.StartsWith("XREAD "))).IsEqualTo(failure == 2 ? 0 : 1);

        Task<RespireResult> ReadAsync(CancellationToken token = default) => client.ExecuteAsync(RespireCommands.Stream.XREAD,
            ["BLOCK", 1, "STREAMS", "key", "0"], cancellationToken: token).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        async ValueTask<Stream> OpenStreamAsync(string host, int port, CancellationToken token)
        {
            if (port == failedNode.Port && Interlocked.Increment(ref failedNodeConnections) > 1 && Volatile.Read(ref failDedicated))
            {
                connecting.TrySetResult();
                // This token includes the transport's independent ConnectTimeout. The caller
                // control cancels only after acquisition is blocked at the same boundary.
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(host, port, token);
                return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        }
    }

    [Test]
    [Arguments(RespireReadFrom.PrimaryPreferred, false, false)]
    [Arguments(RespireReadFrom.PrimaryPreferred, false, true)]
    [Arguments(RespireReadFrom.PrimaryPreferred, true, false)]
    [Arguments(RespireReadFrom.PrimaryPreferred, true, true)]
    [Arguments(RespireReadFrom.ReplicaPreferred, false, false)]
    [Arguments(RespireReadFrom.ReplicaPreferred, false, true)]
    [Arguments(RespireReadFrom.ReplicaPreferred, true, false)]
    [Arguments(RespireReadFrom.ReplicaPreferred, true, true)]
    [Arguments(RespireReadFrom.Replica, false, false)]
    [Arguments(RespireReadFrom.Replica, false, true)]
    [Arguments(RespireReadFrom.Replica, true, false)]
    [Arguments(RespireReadFrom.Replica, true, true)]
    public async Task DedicatedConnectDeadlineFallsBackForEveryEligiblePolicy(RespireReadFrom policy, bool useSentinel, bool cancelCaller)
    {
        await using var primary = Node("primary", false);
        await using var first = Node("first", true);
        await using var second = Node("second", true);
        await using var sentinel = Sentinel(primary, () => [first, second]);
        var connections = new System.Collections.Concurrent.ConcurrentDictionary<int, int>();
        var failedPort = 0;
        var connecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var client = await RespireClient.ConnectAsync(
            Options(useSentinel ? sentinel : primary, useSentinel ? [] : [first, second], policy) with
            {
                Protocol = RespProtocol.Resp3, SentinelPrimaryName = useSentinel ? "primary" : null,
                TestingStreamFactory = OpenStreamAsync,
            });
        using var caller = new CancellationTokenSource();
        var read = client.ExecuteAsync(RespireCommands.Stream.XREAD,
            ["BLOCK", 1, "STREAMS", "key", "0"], cancellationToken: caller.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        if (cancelCaller)
        {
            await connecting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            caller.Cancel();
            var error = await Assert.That(async () => await read).Throws<OperationCanceledException>();
            await Assert.That(error!.CancellationToken).IsEqualTo(caller.Token);
            await Assert.That(primary.ReceivedCommands.Concat(first.ReceivedCommands).Concat(second.ReceivedCommands)
                .Any(command => command.StartsWith("XREAD "))).IsFalse();
            return;
        }
        using var reply = await read;
        await Assert.That(caller.IsCancellationRequested).IsFalse();
        await Assert.That(failedPort).IsNotEqualTo(0);
        await Assert.That(primary.ReceivedCommands.Any(command => command.StartsWith("XREAD "))).IsFalse();
        var failed = new[] { primary, first, second }.Single(node => node.Port == failedPort);
        await Assert.That(failed.ReceivedCommands.Any(command => command.StartsWith("XREAD "))).IsFalse();
        await Assert.That(first.ReceivedCommands.Concat(second.ReceivedCommands)
            .Count(command => command.StartsWith("XREAD "))).IsEqualTo(1);
        await Assert.That(reply.AsString()).IsEqualTo(first.ReceivedCommands.Any(command => command.StartsWith("XREAD "))
            ? "first" : "second");

        async ValueTask<Stream> OpenStreamAsync(string host, int port, CancellationToken token)
        {
            if (port != sentinel.Port && connections.AddOrUpdate(port, 1, (_, count) => count + 1) == 2
                && Interlocked.CompareExchange(ref failedPort, port, 0) == 0)
            {
                connecting.TrySetResult();
                // The deadline case keeps the caller live; its control cancels at this boundary.
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(host, port, token);
                return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        }
    }

    [Test]
    [Arguments(RespireReadFrom.PrimaryPreferred)]
    public async Task FailedDedicatedPrimaryWithNoSentinelReplicasPreservesConnectionError(RespireReadFrom policy)
    {
        await using var primary = Node("primary", false);
        await using var sentinel = Sentinel(primary, () => []);
        var previous = primary.ReplyOverride!;
        primary.ReplyOverride = (id, command) =>
        {
            if (id > 0 && command == "HELLO 3") primary.CloseConnection(id);
            return previous(id, command);
        };
        await using var client = await RespireClient.ConnectAsync(Options(sentinel, [], policy)
            with { Protocol = RespProtocol.Resp3, SentinelPrimaryName = "primary" });
        var error = await Assert.That(async () => await client.ExecuteAsync(RespireCommands.Stream.XREAD,
                ["BLOCK", 1, "STREAMS", "key", "0"]).AsTask().WaitAsync(TimeSpan.FromSeconds(5)))
            .Throws<RespireConnectionException>();
        await Assert.That(error!.Message.Contains("No eligible read replicas", StringComparison.Ordinal)).IsFalse();
        await Assert.That(primary.ReceivedCommands.Count(command => command == "HELLO 3")).IsGreaterThan(1);
        await Assert.That(primary.ReceivedCommands.Any(command => command.StartsWith("XREAD "))).IsFalse();
    }

    // Shared routing matrix: both policies x ASK/MOVED x direct/role-fallback x ordinary/batch/stream.
    // Each row must keep the local socket and send once per visited endpoint. The reverse role
    // direction is covered by ReplicaRoleFallbackRetainsZoneAfterMovedRefresh: a narrowed replica
    // read never returns to the primary. PinnedCursorKeepsLocalPhysicalSocket covers both policies
    // and shared/per-enumeration pins without changing the server that owns the cursor.
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BlockingReplicaValidatesDedicatedSocketRole(bool strict)
    {
        await using var primary = Node("primary", false);
        await using var replica = Node("replica", true);
        var previous = replica.ReplyOverride!;
        replica.ReplyOverride = (id, command) => id > 0 && command == "ROLE"
            ? "*3\r\n$6\r\nmaster\r\n:0\r\n*0\r\n"u8.ToArray() : previous(id, command);
        await using var client = await RespireClient.ConnectAsync(Options(primary, [replica],
            strict ? RespireReadFrom.Replica : RespireReadFrom.ReplicaPreferred));
        if (strict)
            await Assert.That(async () => await client.ExecuteAsync(RespireCommands.Stream.XREAD,
                ["BLOCK", 1, "STREAMS", "key", "0"])).Throws<RespireConnectionException>();
        else
        {
            using var reply = await client.ExecuteAsync(RespireCommands.Stream.XREAD, ["BLOCK", 1, "STREAMS", "key", "0"]);
            await Assert.That(reply.AsString()).IsEqualTo("primary");
        }
        await Assert.That(replica.ReceivedCommands.Any(command => command.StartsWith("XREAD "))).IsFalse();
    }

    [Test]
    public async Task BlockingReplicaKeepsMultiplexedReadsFreeAndHonorsCallerCancellation()
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var primary = Node("primary", false);
        await using var replica = Node("replica", true);
        replica.SuppressReply = command =>
        {
            if (!command.StartsWith("XREAD ")) return false;
            arrived.TrySetResult();
            return true;
        };
        await using var client = await RespireClient.ConnectAsync(Options(primary, [replica],
            RespireReadFrom.ReplicaPreferred) with { CommandTimeout = TimeSpan.FromMilliseconds(500) });
        using var caller = new CancellationTokenSource();
        var pending = client.ExecuteAsync(RespireCommands.Stream.XREAD,
            ["BLOCK", 0, "STREAMS", "key", "0"], cancellationToken: caller.Token).AsTask();
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(750);
        await Assert.That(pending.IsCompleted).IsFalse();
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("replica");
        caller.Cancel();
        var error = await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)))
            .Throws<OperationCanceledException>();
        await Assert.That(error!.CancellationToken).IsEqualTo(caller.Token);
        replica.SuppressReply = null;
        using var next = await client.ExecuteAsync(RespireCommands.Stream.XREAD, ["BLOCK", 1, "STREAMS", "key", "0"]);
        await Assert.That(next.AsString()).IsEqualTo("replica");
        var ids = replica.ReceivedCommands.Select((command, index) => (command, index))
            .Where(item => item.command.StartsWith("XREAD "))
            .Select(item => replica.ReceivedConnectionIds[item.index]).ToArray();
        await Assert.That(ids.Length).IsEqualTo(2);
        await Assert.That(ids[0] != ids[1]).IsTrue(); // Cancellation discards the blocked socket.
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RemovedReplicaDrainsBlockingLeaseUnlessClientDisposes(bool disposeClient)
    {
        await using var primary = Node("primary", false);
        await using var replica = Node("replica", true);
        FakeRespServer[] replicas = [replica];
        await using var sentinel = Sentinel(primary, () => Volatile.Read(ref replicas));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel, [], RespireReadFrom.ReplicaPreferred)
            with { SentinelPrimaryName = "primary" });
        var lease = await client.Core.ReadRouter.RentDedicatedConnectionAsync(RespireReadFrom.ReplicaPreferred, CancellationToken.None);
        lease.Pool.Return(lease.Connection);
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        replica.SuppressReply = command =>
        {
            if (!command.StartsWith("XREAD ")) return false;
            arrived.TrySetResult();
            return true;
        };
        var pending = client.ExecuteAsync(RespireCommands.Stream.XREAD, ["BLOCK", 0, "STREAMS", "key", "0"]).AsTask();
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Volatile.Write(ref replicas, []);
        await client.Core.ReadRouter.RefreshNowAsync(CancellationToken.None);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!lease.Pool.IsStopping) await Task.Delay(5, limit.Token);
        await Assert.That(pending.IsCompleted).IsFalse();
        if (disposeClient)
        {
            await client.DisposeAsync();
            await Assert.That(async () => await pending.WaitAsync(limit.Token)).Throws<RespireConnectionException>();
        }
        else
        {
            var index = replica.ReceivedCommands.ToList().FindIndex(command => command.StartsWith("XREAD "));
            await replica.SendRawAsync(Bulk("accepted"), replica.ReceivedConnectionIds[index]);
            using var reply = await pending.WaitAsync(limit.Token);
            await Assert.That(reply.AsString()).IsEqualTo("accepted");
            await lease.Pool.RetireAsync().AsTask().WaitAsync(limit.Token);
        }
        await Assert.That(lease.Connection.IsConnected).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DedicatedReplicaRoleWaitEndsOnCancellationOrDisposal(bool disposeClient)
    {
        await using var primary = Node("primary", false);
        await using var replica = Node("replica", true);
        var arrived = GateDedicatedRole(replica);
        await using var client = await RespireClient.ConnectAsync(Options(primary, [replica], RespireReadFrom.ReplicaPreferred)
            with { CommandTimeout = null });
        using var caller = new CancellationTokenSource();
        var pending = client.ExecuteAsync(RespireCommands.Stream.XREAD,
            ["BLOCK", 0, "STREAMS", "key", "0"], cancellationToken: caller.Token).AsTask();
        await arrived.WaitAsync(TimeSpan.FromSeconds(5));
        if (disposeClient)
        {
            await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5))).Throws<ObjectDisposedException>();
        }
        else
        {
            caller.Cancel();
            var error = await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)))
                .Throws<OperationCanceledException>();
            await Assert.That(error!.CancellationToken).IsEqualTo(caller.Token);
        }
        await Assert.That(replica.ReceivedCommands.Any(command => command.StartsWith("XREAD "))).IsFalse();
    }

    [Test]
    public async Task RemovingReplicaDuringDedicatedRoleValidationReselects()
    {
        await using var primary = Node("primary", false);
        await using var replica = Node("replica", true);
        FakeRespServer[] replicas = [replica];
        await using var sentinel = Sentinel(primary, () => Volatile.Read(ref replicas));
        var arrived = GateDedicatedRole(replica);
        await using var client = await RespireClient.ConnectAsync(Options(sentinel, [], RespireReadFrom.ReplicaPreferred)
            with { SentinelPrimaryName = "primary" });
        var pending = client.ExecuteAsync(RespireCommands.Stream.XREAD, ["BLOCK", 1, "STREAMS", "key", "0"]).AsTask();
        await arrived.WaitAsync(TimeSpan.FromSeconds(5));
        Volatile.Write(ref replicas, []);
        await client.Core.ReadRouter.RefreshNowAsync(CancellationToken.None);
        var index = replica.ReceivedCommands.ToList().FindLastIndex(command => command == "ROLE");
        await replica.SendRawAsync(replica.ReplyOverride!(replica.ReceivedConnectionIds[index], "ROLE")!, replica.ReceivedConnectionIds[index]);
        using var reply = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(reply.AsString()).IsEqualTo("primary");
        await Assert.That(replica.ReceivedCommands.Any(command => command.StartsWith("XREAD "))).IsFalse();
    }

    private static Task GateDedicatedRole(FakeRespServer replica)
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var roles = 0;
        replica.SuppressReply = command =>
        {
            if (command != "ROLE" || Interlocked.Increment(ref roles) != 2) return false;
            arrived.TrySetResult();
            return true;
        };
        return arrived.Task;
    }

    [Test, NotInParallel]
    public async Task WarmBlockingReplicaRentalAllocatesNothing()
    {
        await using var primary = Node("primary", false);
        await using var replica = Node("replica", true);
        await using var client = await RespireClient.ConnectAsync(Options(primary, [replica], RespireReadFrom.ReplicaPreferred));
        var router = client.Core.ReadRouter;
        router.RoleRevalidationInterval = TimeSpan.FromDays(1);
        var first = await router.RentDedicatedConnectionAsync(RespireReadFrom.ReplicaPreferred, CancellationToken.None);
        first.Pool.Return(first.Connection);
        for (var index = 0; index < 20; index++) { MeasureDedicatedRental(router, false); MeasureDedicatedRental(router, true); }
        var measured = AllocationMeasurement.WithoutConcurrentGc(() =>
            (Actual: MeasureDedicatedRental(router, false), Control: MeasureDedicatedRental(router, true)));
        await Assert.That(measured.Actual).IsEqualTo(0L);
        await Assert.That(measured.Control).IsGreaterThanOrEqualTo(37_000L);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureDedicatedRental(ReadEndpointRouter router, bool control)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1_000; index++)
        {
            var pending = router.RentDedicatedConnectionAsync(RespireReadFrom.ReplicaPreferred, CancellationToken.None);
            if (!pending.IsCompletedSuccessfully) throw new InvalidOperationException("Warm rental performed asynchronous work.");
            var lease = pending.Result;
            lease.Pool.Return(lease.Connection);
            if (control) GC.KeepAlive(new byte[37]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static FakeRespServer Sentinel(FakeRespServer primary, Func<FakeRespServer[]> replicas) => new(16)
    {
        ReplyOverride = (_, command) => command.StartsWith("SENTINEL GET-MASTER-ADDR-BY-NAME ")
            ? Encoding.ASCII.GetBytes($"*2\r\n$9\r\n127.0.0.1\r\n${primary.Port.ToString().Length}\r\n{primary.Port}\r\n")
            : command.StartsWith("SENTINEL REPLICAS ")
                ? Encoding.ASCII.GetBytes(ReplicaReply(replicas())) : "*0\r\n"u8.ToArray(),
    };

    private static string ReplicaReply(FakeRespServer[] replicas)
        => $"*{replicas.Length}\r\n" + string.Concat(replicas.Select(replica =>
            $"*6\r\n+ip\r\n+127.0.0.1\r\n+port\r\n+{replica.Port}\r\n+flags\r\n+slave\r\n"));

    private static RespireOptions Options(FakeRespServer primary, FakeRespServer[] replicas, RespireReadFrom policy)
        => new()
        {
            Endpoints = [new("127.0.0.1", primary.Port)],
            ReplicaEndpoints = replicas.Select(node => new RespireEndpoint("127.0.0.1", node.Port)).ToArray(),
            Protocol = RespProtocol.Resp2, Connections = 1,
            ReadFrom = policy,
            ConnectTimeout = TimeSpan.FromSeconds(2), CommandTimeout = TimeSpan.FromSeconds(5),
        };

    private static FakeRespServer Node(string name, bool replica)
        => new(8)
        {
            ReplyOverride = (_, command) => command switch
            {
                "HELLO 3" => "%1\r\n+proto\r\n:3\r\n"u8.ToArray(),
                "ROLE" => replica ? "*5\r\n$5\r\nslave\r\n$9\r\n127.0.0.1\r\n:6379\r\n$9\r\nconnected\r\n:0\r\n"u8.ToArray()
                    : "*3\r\n$6\r\nmaster\r\n:0\r\n*0\r\n"u8.ToArray(),
                _ when command.StartsWith("GET ") || command.StartsWith("XREAD ") => Bulk(name),
                _ => FakeRespServer.OkReply,
            },
        };

    private static byte[] Bulk(string value) => Encoding.ASCII.GetBytes($"${value.Length}\r\n{value}\r\n");
}
