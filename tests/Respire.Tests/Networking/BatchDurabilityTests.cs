using System.Text;
using Microsoft.Extensions.Logging;
using Respire.Internal;
using Respire.Commands;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class BatchDurabilityTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task CleanupFailurePreservesEarlierAcknowledgementError(bool aof, bool acknowledgementFails)
    {
        byte[] acknowledgement = acknowledgementFails ? "-ERR acknowledgement failed\r\n"u8.ToArray()
            : aof ? "*2\r\n:1\r\n:1\r\n"u8.ToArray() : ":1\r\n"u8.ToArray();
        await using var server = new FakeRespServer(FakeRespServer.OkReply, acknowledgement);
        using var logger = new FailingDisconnectLogger();
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Connections = 1, Endpoints = [new("127.0.0.1", server.Port)], LoggerFactory = logger,
        });
        using var batch = client.CreateBatch();
        var write = batch.Set("key", "value");
        if (acknowledgementFails)
        {
            var error = await Assert.That(async () => await Execute(batch, aof, 1, TimeSpan.Zero))
                .ThrowsExactly<RespireServerException>();
            await Assert.That(error!.Message).IsEqualTo("ERR acknowledgement failed");
            await Assert.That(error.CommandName).IsEqualTo(aof ? "WAITAOF" : "WAIT");
        }
        else
        {
            var error = await Assert.That(async () => await Execute(batch, aof, 1, TimeSpan.Zero))
                .ThrowsExactly<InvalidOperationException>();
            await Assert.That(error).IsSameReferenceAs(logger.Failure);
        }
        await Assert.That(write.Result).IsTrue();
        await logger.ReportedFailure.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EveryExecutionUsesFreshReplicationHistory(bool aof)
    {
        byte[] acknowledgement = aof ? "*2\r\n:1\r\n:1\r\n"u8.ToArray() : ":1\r\n"u8.ToArray();
        await using var server = new FakeRespServer(3, FakeRespServer.OkReply, acknowledgement);
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Connections = 1, Endpoints = [new("127.0.0.1", server.Port)],
        });
        var pool = client.Core.DedicatedPool;
        var prior = await pool.RentAsync(CancellationToken.None);
        using (var reply = await prior.SendAsync(new Cmd2(Verbs.Set, "earlier", "value"))) { }
        pool.Return(prior);

        using var first = client.CreateBatch();
        _ = first.Set("key", "value");
        await Execute(first, aof, 1, TimeSpan.Zero);
        using var second = client.CreateBatch();
        var read = second.GetString("key");
        await Execute(second, aof, 1, TimeSpan.Zero);
        await Assert.That(read.Result).IsEqualTo("OK");
        var ids = server.ReceivedConnectionIds;
        await Assert.That(ids.Count).IsEqualTo(5);
        await Assert.That(ids.Distinct().Count()).IsEqualTo(3);
        await Assert.That(ids[1]).IsEqualTo(ids[2]);
        await Assert.That(ids[3]).IsEqualTo(ids[4]);
        await Assert.That(prior.IsConnected).IsTrue(); // Existing idle borrowers were not consumed or closed.
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AcknowledgementFollowsAllWritesOnAnExclusiveConnection(bool aof)
    {
        // Initial SET replies must complete before WAIT is sent. Give that setup a
        // scheduling budget under the parallel suite, then outwait both deadlines.
        var ordinaryTimeout = TimeSpan.FromSeconds(2);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(3, FakeRespServer.OkReply)
        {
            SuppressReply = command =>
            {
                if (!command.StartsWith("WAIT", StringComparison.Ordinal)) return false;
                waiting.TrySetResult();
                return true;
            },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = [new("127.0.0.1", server.Port)], Connections = 2,
            CommandTimeout = ordinaryTimeout, ConnectionIdleReadTimeout = ordinaryTimeout,
        });
        using var batch = client.WithKeyPrefix("tenant:").CreateBatch();
        var first = batch.Set("a", "first");
        var second = batch.Set("b", "second");
        var executing = Execute(batch, aof, replicas: 2, TimeSpan.FromSeconds(5));
        // Surface an early write/acquisition failure instead of hiding it behind the
        // server-observation timeout, which otherwise loses the actual failure stage.
        await Task.WhenAny(waiting.Task, executing).WaitAsync(TimeSpan.FromSeconds(5));
        if (executing.IsCompleted) await executing;
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // A blocking acknowledgement must not inherit ordinary command/response deadlines or occupy a multiplexed socket.
        await Task.Delay(ordinaryTimeout + TimeSpan.FromSeconds(1));
        await Assert.That(executing.IsCompleted).IsFalse();
        await client.SetAsync("outside", "unrelated");
        var commands = server.ReceivedCommands;
        var ids = server.ReceivedConnectionIds;
        var acknowledgementIndex = Array.FindIndex(commands.ToArray(), x => x.StartsWith("WAIT", StringComparison.Ordinal));
        await Assert.That(ids[0]).IsEqualTo(ids[1]);
        await Assert.That(ids[1]).IsEqualTo(ids[acknowledgementIndex]);
        await Assert.That(ids[^1] == ids[acknowledgementIndex]).IsFalse();
        await server.SendRawAsync(aof ? "*2\r\n:1\r\n:1\r\n"u8.ToArray() : ":1\r\n"u8.ToArray(), ids[acknowledgementIndex]);
        var result = await executing;
        await Assert.That(result).IsEqualTo(new RespireAofAcknowledgement(aof ? 1 : 0, 1));
        await Assert.That(first.Result && second.Result).IsTrue();
        await Assert.That(server.ReceivedCommands.Take(3)).IsEquivalentTo([
            "SET tenant:a first", "SET tenant:b second", aof ? "WAITAOF 1 2 5000" : "WAIT 2 5000"]);
        await Assert.That(async () => await Execute(batch, aof, 1, TimeSpan.Zero)).Throws<InvalidOperationException>();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CancelledAcknowledgementDiscardsLeaseAndPreservesWriteResult(bool aof)
    {
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledgements = 0;
        byte[] reply = aof ? "*2\r\n:1\r\n:0\r\n"u8.ToArray() : ":0\r\n"u8.ToArray();
        await using var server = new FakeRespServer(3, FakeRespServer.OkReply, reply)
        {
            SuppressReply = command =>
            {
                if (!command.StartsWith("WAIT", StringComparison.Ordinal) || Interlocked.Increment(ref acknowledgements) != 1) return false;
                waiting.TrySetResult();
                return true;
            },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var cancellation = new CancellationTokenSource();
        using var batch = client.CreateBatch();
        var write = batch.Set("first", "value");
        var executing = Execute(batch, aof, 1, TimeSpan.Zero, cancellation.Token);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.That(async () => await executing.WaitAsync(TimeSpan.FromSeconds(5))).Throws<OperationCanceledException>();
        await Assert.That(write.Result).IsTrue();
        using var next = client.CreateBatch();
        _ = next.Set("second", "value");
        await Execute(next, aof, 1, TimeSpan.FromTicks(1));
        var ids = server.ReceivedConnectionIds;
        await Assert.That(ids[0] == ids[2]).IsFalse();
        await Assert.That(ids[0]).IsEqualTo(ids[1]);
        await Assert.That(ids[2]).IsEqualTo(ids[3]);
        await Assert.That(server.ReceivedCommands[^1]).IsEqualTo(aof ? "WAITAOF 1 1 1" : "WAIT 1 1");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FailedWritesCompleteNeighborsAndPreventAcknowledgement(bool aof)
    {
        await using var server = new FakeRespServer(2, "-WRONGTYPE rejected\r\n"u8.ToArray(), FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var batch = client.CreateBatch();
        var bad = batch.Set("bad", "value");
        var good = batch.Set("good", "value");
        await Assert.That(async () => await Execute(batch, aof, 1, TimeSpan.Zero)).Throws<RespireServerException>();
        await Assert.That(bad.Error).IsNotNull();
        await Assert.That(good.Result).IsTrue();
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(["SET bad value", "SET good value"]);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ServerAcknowledgementErrorsDoNotEraseSuccessfulWrites(bool aof)
    {
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply, "-ERR unsupported acknowledgement\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var batch = client.CreateBatch();
        var write = batch.Set("key", "value");
        var error = await Assert.That(async () => await Execute(batch, aof, 1, TimeSpan.Zero)).Throws<RespireServerException>();
        await Assert.That(error!.CommandName).IsEqualTo(aof ? "WAITAOF" : "WAIT");
        await Assert.That(write.Result).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EmptyBatchRejectionAllowsAddingAndExecutingCommands(bool aof)
    {
        byte[] acknowledgement = aof ? "*2\r\n:1\r\n:1\r\n"u8.ToArray() : ":1\r\n"u8.ToArray();
        await using var server = new FakeRespServer(FakeRespServer.OkReply, acknowledgement);
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Connections = 1, Endpoints = [new("127.0.0.1", server.Port)],
        });
        using var batch = client.CreateBatch();
        await Assert.That(async () => await Execute(batch, aof, 1, TimeSpan.Zero))
            .Throws<InvalidOperationException>();
        await Assert.That(batch.IsSent).IsFalse();
        await Assert.That(server.ReceivedCommands).IsEmpty();
        var write = batch.Set("key", "value");
        await Execute(batch, aof, 1, TimeSpan.Zero);
        await Assert.That(write.Result).IsTrue();
        await Assert.That(batch.IsSent).IsTrue();
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(
            ["SET key value", aof ? "WAITAOF 1 1 0" : "WAIT 1 0"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task InvalidOptionsAndClusterLayoutsFailBeforeSending()
    {
        await using var server = new FakeRespServer();
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = [new("127.0.0.1", server.Port)], Connections = 1, UseCluster = true,
        });
        using var empty = client.CreateBatch();
        await Assert.That(async () => await empty.ExecuteAndWaitForReplicationAsync(1, TimeSpan.Zero)).Throws<InvalidOperationException>();
        using var batch = client.CreateBatch();
        _ = batch.Set("{a}:key", "value");
        await Assert.That(async () => await batch.ExecuteAndWaitForReplicationAsync(-1, TimeSpan.Zero)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await batch.ExecuteAndWaitForAofAsync(true, -1, TimeSpan.Zero)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await batch.ExecuteAndWaitForReplicationAsync(1, TimeSpan.FromTicks(-1))).Throws<ArgumentOutOfRangeException>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(async () => await batch.ExecuteAndWaitForReplicationAsync(1, TimeSpan.Zero, cancellation.Token)).Throws<OperationCanceledException>();
        _ = batch.Set("{b}:key", "value");
        await Assert.That(async () => await batch.ExecuteAndWaitForReplicationAsync(1, TimeSpan.Zero)).Throws<NotSupportedException>();
        await Assert.That(batch.IsSent).IsFalse();
        using var keyless = client.CreateBatch();
        _ = keyless.Functions.List();
        await Assert.That(async () => await keyless.ExecuteAndWaitForReplicationAsync(1, TimeSpan.Zero)).Throws<NotSupportedException>();
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ClusterRoutesDedicatedLeaseAndSurfacesRedirectWithoutReplay(bool redirect)
    {
        byte[] writeReply = redirect ? "-MOVED 1 127.0.0.1:1\r\n"u8.ToArray() : FakeRespServer.OkReply;
        await using var owner = new FakeRespServer(2, writeReply, ":1\r\n"u8.ToArray());
        var slot = ClusterHash.GetSlot("tenant:{durability}:key");
        var topology = Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:{slot}\r\n:{slot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{owner.Port}\r\n");
        await using var seed = new FakeRespServer(topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true, Connections = 1, Endpoints = [new("127.0.0.1", seed.Port)],
        });
        using var batch = client.WithKeyPrefix("tenant:").CreateBatch();
        _ = batch.Set("{durability}:key", "value");
        if (redirect)
            await Assert.That(async () => await batch.ExecuteAndWaitForReplicationAsync(1, TimeSpan.FromSeconds(1))).Throws<RespireServerException>();
        else
            await Assert.That(await batch.ExecuteAndWaitForReplicationAsync(1, TimeSpan.FromSeconds(1))).IsEqualTo(1);
        await Assert.That(seed.ReceivedCommands).IsEquivalentTo(["CLUSTER SLOTS"]);
        string[] expected = redirect ? ["SET tenant:{durability}:key value"] : ["SET tenant:{durability}:key value", "WAIT 1 1000"];
        await Assert.That(owner.ReceivedCommands).IsEquivalentTo(expected);
        await Assert.That(owner.ReceivedConnectionIds.Distinct().Count()).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CancelledWriteFaultsPendingAndNeverSendsAcknowledgement(bool aof)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply)
        {
            SuppressReply = _ => { received.TrySetResult(); return true; },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var batch = client.CreateBatch();
        var write = batch.Set("key", "value");
        using var cancellation = new CancellationTokenSource();
        var executing = Execute(batch, aof, 1, TimeSpan.Zero, cancellation.Token);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.That(async () => await executing.WaitAsync(TimeSpan.FromSeconds(5))).Throws<OperationCanceledException>();
        await Assert.That(() => write.Result).Throws<OperationCanceledException>();
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(["SET key value"]);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DisconnectDuringAcknowledgementDoesNotReplayWrites(bool aof)
    {
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply) { CloseConnectionAfterCommand = 2 };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var batch = client.CreateBatch();
        var write = batch.Set("key", "value");
        await Assert.That(async () => await Execute(batch, aof, 1, TimeSpan.Zero).WaitAsync(TimeSpan.FromSeconds(5))).Throws<RespireConnectionException>();
        await Assert.That(write.Result).IsTrue();
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(["SET key value", aof ? "WAITAOF 1 1 0" : "WAIT 1 0"]);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ClientDisposalAbortsBlockedAcknowledgement(bool aof)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply)
        {
            SuppressReply = command =>
            {
                if (!command.StartsWith("WAIT", StringComparison.Ordinal)) return false;
                received.TrySetResult();
                return true;
            },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var batch = client.CreateBatch();
        var write = batch.Set("key", "value");
        var executing = Execute(batch, aof, 1, TimeSpan.Zero);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await client.DisposeAsync();
        await Assert.That(async () => await executing.WaitAsync(TimeSpan.FromSeconds(5))).Throws<RespireConnectionException>();
        await Assert.That(write.Result).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AcquisitionFailureFaultsEveryPending(bool aof)
    {
        using var unavailable = new ReservedUnavailablePort();
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = [new("127.0.0.1", unavailable.Port)], ConnectTimeout = TimeSpan.FromSeconds(1),
        });
        using var batch = client.CreateBatch();
        var first = batch.Set("first", "value");
        var second = batch.Set("second", "value");
        var error = await Assert.That(async () => await Execute(batch, aof, 1, TimeSpan.Zero)).Throws<Exception>();
        await Assert.That(first.Error).IsSameReferenceAs(error);
        await Assert.That(second.Error).IsSameReferenceAs(error);
    }

    [Test]
    [Arguments(false, ":-1\r\n")]
    [Arguments(true, "*1\r\n:1\r\n")]
    [Arguments(true, "*2\r\n:2\r\n:0\r\n")]
    [Arguments(true, "*2\r\n:1\r\n:-1\r\n")]
    public async Task InvalidAcknowledgementCountsAreNotReportedAsDurability(bool aof, string response)
    {
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply, Encoding.ASCII.GetBytes(response));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var batch = client.CreateBatch();
        var write = batch.Set("key", "value");
        await Assert.That(async () => await Execute(batch, aof, 1, TimeSpan.Zero)).Throws<RespireProtocolException>();
        await Assert.That(write.Result).IsTrue();
    }

    private sealed class FailingDisconnectLogger : ILoggerFactory, ILogger
    {
        internal readonly InvalidOperationException Failure = new("Test disconnect failure.");
        internal readonly TaskCompletionSource ReportedFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ILogger CreateLogger(string categoryName) => this;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            // Inject failure after the connection has released its sockets and buffers.
            if (logLevel == LogLevel.Debug && formatter(state, exception).StartsWith("Disconnected from", StringComparison.Ordinal))
                throw Failure;
            if (logLevel == LogLevel.Warning && ReferenceEquals(exception, Failure)) ReportedFailure.TrySetResult();
        }
    }

    private static async Task<RespireAofAcknowledgement> Execute(RespireBatch batch, bool aof, int replicas, TimeSpan timeout, CancellationToken cancellationToken = default)
        => aof ? await batch.ExecuteAndWaitForAofAsync(true, replicas, timeout, cancellationToken)
            : new(0, await batch.ExecuteAndWaitForReplicationAsync(replicas, timeout, cancellationToken));
}
