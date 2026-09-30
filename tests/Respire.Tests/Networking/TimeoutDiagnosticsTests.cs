using Respire.Commands;
using Respire.Networking;
using Respire.Infrastructure;
using System.Net;
using System.Net.Sockets;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class TimeoutDiagnosticsTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PublicConstructionDoesNotInventObservations(bool withCause)
    {
        var error = withCause
            ? new RespireTimeoutException("GET", TimeSpan.FromSeconds(1), new IOException("cause"))
            : new RespireTimeoutException("GET", TimeSpan.FromSeconds(1));
        var snapshot = error.Diagnostics;
        await Assert.That(snapshot.Stage).IsEqualTo(RespireCommandStage.Unknown);
        await Assert.That(snapshot.Endpoint).IsNull();
        await Assert.That(snapshot.BusyWorkerThreads).IsNull();
        await Assert.That(snapshot.MinWorkerThreads).IsNull();
        await Assert.That(snapshot.BusyIoThreads).IsNull();
        await Assert.That(snapshot.MinIoThreads).IsNull();
        await Assert.That(snapshot.PendingWorkItems).IsNull();
        await Assert.That(snapshot.PossibleThreadPoolStarvation).IsFalse();
        await Assert.That(snapshot.Hint).Contains("No timeout observations");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TransactionCancellationRequiresTheDeadlineToken(bool deadlineExpired)
    {
        await using var server = new FakeRespServer();
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port);
        using var deadline = new CancellationTokenSource();
        using var unrelated = new CancellationTokenSource();
        deadline.Cancel();
        unrelated.Cancel();
        var cause = new OperationCanceledException(deadlineExpired ? deadline.Token : unrelated.Token);
        var method = typeof(RespireConnection).GetMethod("AwaitTimedMultiReplyAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var reply = (ValueTask<Respire.Protocol.RespValue>)method.Invoke(connection,
            [ValueTask.FromException<Respire.Protocol.RespValue>(cause), 0L, 0L,
                TimeSpan.FromSeconds(1), CancellationToken.None, deadline.Token])!;
        if (deadlineExpired)
        {
            var error = await Assert.That(async () => await reply).ThrowsExactly<RespireTimeoutException>();
            await Assert.That(error!.InnerException).IsSameReferenceAs(cause);
        }
        else
        {
            var error = await Assert.That(async () => await reply).ThrowsExactly<OperationCanceledException>();
            await Assert.That(error).IsSameReferenceAs(cause);
        }
    }

    [Test]
    public async Task ImmediateTimeout_ReportsConnectionAndAwaitingReplyWithoutArguments()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply)
        {
            SuppressReply = command => command.StartsWith("GET ", StringComparison.Ordinal)
        };
        await using var client = await ConnectAsync(server.Port);
        await client.PingAsync();

        var error = await Assert.That(async () => await client.GetStringAsync("secret-key"))
            .ThrowsExactly<RespireTimeoutException>();
        var diagnostics = error!.Diagnostics;
        await Assert.That(diagnostics.Stage).IsEqualTo(RespireCommandStage.AwaitingReply);
        await Assert.That(diagnostics.Endpoint).IsEqualTo(new RespireEndpoint("127.0.0.1", server.Port));
        await Assert.That(diagnostics.ConnectionId.GetValueOrDefault()).IsGreaterThan(0);
        await Assert.That(diagnostics.InflightCount).IsEqualTo(1);
        await Assert.That(diagnostics.InflightBytes.GetValueOrDefault()).IsGreaterThan(0);
        await Assert.That(diagnostics.PendingWriteBytes).IsEqualTo(0);
        await Assert.That(diagnostics.TimeSinceLastRead).IsNotNull();
        await Assert.That(diagnostics.TimeSinceLastWrite).IsNotNull();
        await Assert.That(diagnostics.IsConnected).IsTrue();
        await Assert.That(error.Message.Contains("secret-key", StringComparison.Ordinal)).IsFalse();
        await Assert.That(error.Message.Contains("Stage=AwaitingReply", StringComparison.Ordinal)).IsTrue();
        await Assert.That(diagnostics.BusyWorkerThreads.GetValueOrDefault()).IsGreaterThanOrEqualTo(0);
        await Assert.That(diagnostics.PendingWorkItems.GetValueOrDefault()).IsGreaterThanOrEqualTo(0);
    }

    [Test]
    public async Task FullRingTimeout_ReportsNotEnqueuedAndPreservesOutstandingBytes()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply) { SuppressReply = _ => true };
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new RespireConnectionOptions { MaxInflightCommands = 1, CommandTimeout = TimeSpan.FromMilliseconds(200) });
        var command = new RawCommand(FakeRespServer.PingFrame);
        using var cancellation = new CancellationTokenSource();
        var first = connection.SendAsync(command, cancellation.Token, armCommandDeadline: false).AsTask();
        var error = await Assert.That(async () => await connection.SendAsync(command, commandName: "PING"))
            .ThrowsExactly<RespireTimeoutException>();
        await Assert.That(error!.Diagnostics.Stage).IsEqualTo(RespireCommandStage.WaitingForCapacity);
        await Assert.That(error.Diagnostics.InflightCount).IsEqualTo(1);
        await Assert.That(error.Diagnostics.InflightBytes).IsEqualTo(FakeRespServer.PingFrame.LongLength);
        await Assert.That(error.Message.Contains("not been enqueued", StringComparison.Ordinal)).IsTrue();
        cancellation.Cancel();
        await Assert.That(async () => await first).ThrowsExactly<OperationCanceledException>();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TransactionTimeout_ReportsSharedOrDedicatedConnection(bool watched)
    {
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply)
        {
            SuppressReply = command => command == "EXEC"
        };
        await using var client = await ConnectAsync(server.Port);
        await using RespireTransactionBase transaction = watched
            ? await client.CreateTransactionAsync(["key"])
            : client.CreateTransaction();
        var pending = transaction.GetString("key");
        var error = await Assert.That(async () =>
        {
            if (transaction is RespireWatchedTransaction watchedTransaction)
                await watchedTransaction.CommitAsync();
            else
                await ((RespireTransaction)transaction).CommitAsync();
        })
            .ThrowsExactly<RespireTimeoutException>();
        await Assert.That(error!.Diagnostics.Stage).IsEqualTo(RespireCommandStage.AwaitingReply);
        await Assert.That(error.Diagnostics.Endpoint).IsEqualTo(new RespireEndpoint("127.0.0.1", server.Port));
        await Assert.That(error.Diagnostics.InflightCount).IsEqualTo(1);
        await Assert.That(error.Diagnostics.InflightBytes.GetValueOrDefault()).IsGreaterThan(0);
        await Assert.That(pending.Error).IsSameReferenceAs(error);
    }

    [Test]
    public async Task BatchTimeout_PreservesPhysicalSnapshot()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply) { SuppressReply = _ => true };
        await using var client = await ConnectAsync(server.Port);
        using var batch = client.CreateBatch();
        var pending = batch.GetString("key");
        var result = await batch.TryExecuteAsync();
        await Assert.That(result.FailureCount).IsEqualTo(1);
        var error = await Assert.That(pending.Error).IsTypeOf<RespireTimeoutException>();
        await Assert.That(error!.Diagnostics.Stage).IsEqualTo(RespireCommandStage.AwaitingReply);
        await Assert.That(error.Diagnostics.Endpoint).IsEqualTo(new RespireEndpoint("127.0.0.1", server.Port));
    }

    [Test]
    public async Task RelabelingTimeout_PreservesSnapshotAndCause()
    {
        var original = new RespireTimeoutException("CLIENT ID", TimeSpan.FromSeconds(1));
        var relabeled = new RespireTimeoutException("CLIENT ID / CLIENT KILL", TimeSpan.FromSeconds(2), original);
        await Assert.That(relabeled.Diagnostics).IsSameReferenceAs(original.Diagnostics);
        await Assert.That(relabeled.InnerException).IsSameReferenceAs(original);
        await Assert.That(relabeled.Diagnostics.Stage).IsEqualTo(RespireCommandStage.Unknown);
        await Assert.That(relabeled.Diagnostics.ConnectionId).IsNull();
    }

    [Test]
    public async Task StalledPeer_ReportsUnansweredPayloadAndObservedStage()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptSocketAsync();
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", port,
            new RespireConnectionOptions { SocketSendBufferSize = 1024, CommandTimeout = TimeSpan.FromMilliseconds(500) });
        using var peer = await accept;
        peer.ReceiveBufferSize = 1024;
        // The peer never reads, but kernels differ in how much loopback data a send can
        // accept. Assert the observed write stage while all unanswered bytes remain in flight.
        using var cancellation = new CancellationTokenSource();
        var large = connection.SendAsync(new RawCommand(new byte[8 * 1024 * 1024]), cancellation.Token,
            armCommandDeadline: false).AsTask();
        var error = await Assert.That(async () => await connection.SendAsync(
                new RawCommand(FakeRespServer.PingFrame), commandName: "PING"))
            .ThrowsExactly<RespireTimeoutException>();
        var snapshot = error!.Diagnostics;
        var pendingBytes = snapshot.PendingWriteBytes.GetValueOrDefault();
        var expectedStage = pendingBytes == 0 ? RespireCommandStage.AwaitingReply
            : pendingBytes >= FakeRespServer.PingFrame.Length ? RespireCommandStage.Buffered : RespireCommandStage.Writing;
        await Assert.That(snapshot.Stage).IsEqualTo(expectedStage);
        await Assert.That(snapshot.InflightBytes).IsEqualTo(8 * 1024 * 1024 + FakeRespServer.PingFrame.LongLength);
        await Assert.That(snapshot.InflightCount).IsEqualTo(2);
        cancellation.Cancel();
        await Assert.That(async () => await large).ThrowsExactly<OperationCanceledException>();
    }

    [Test]
    public async Task SuccessfulRingAccounting_DoesNotAllocatePerCommand()
    {
        var ring = new InflightRing(1);
        var source = InflightRing.DiscardSentinel;
        ring.TryEnqueue(source, 10);
        ring.TryDequeue(out _);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 1; i <= 1000; i++)
        {
            ring.TryEnqueue(source, 10 + i);
            ring.TryDequeue(out _);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        await Assert.That(allocated).IsEqualTo(0);
        await Assert.That(ring.CompletedWriteEnd).IsEqualTo(1010);
    }

    [Test]
    public async Task DeadlineSweep_SharesObservationsAcrossExpiredCommands()
    {
        var pool = new PendingResponsePool(2);
        var ring = new InflightRing(2);
        var first = pool.Rent(commandName: "GET");
        var second = pool.Rent(commandName: "PING");
        first.Deadline = second.Deadline = 1;
        ring.TryEnqueue(first);
        ring.TryEnqueue(second);

        await Assert.That(ring.SweepExpired(2, TimeSpan.FromMilliseconds(1))).IsEqualTo(-1);
        var firstError = await Assert.That(async () => await first.Task).ThrowsExactly<RespireTimeoutException>();
        var secondError = await Assert.That(async () => await second.Task).ThrowsExactly<RespireTimeoutException>();
        await Assert.That(firstError!.Diagnostics).IsSameReferenceAs(secondError!.Diagnostics);
        await Assert.That(firstError.CommandName).IsEqualTo("GET");
        await Assert.That(secondError.CommandName).IsEqualTo("PING");
        while (ring.TryDequeue(out var source))
            source.ReleaseRef();
    }

    [Test]
    public async Task SharedSnapshot_PreservesCountersAndClassifiesEachCommand()
    {
        var shared = RespireTimeoutDiagnostics.Capture(inflightCount: 3, inflightBytes: 30,
            pendingWriteBytes: 15, writtenBytes: 15);
        var replied = shared.ForCommand(0, 10);
        var writing = shared.ForCommand(10, 20);
        var buffered = shared.ForCommand(20, 30);
        await Assert.That(replied.Stage).IsEqualTo(RespireCommandStage.AwaitingReply);
        await Assert.That(writing.Stage).IsEqualTo(RespireCommandStage.Writing);
        await Assert.That(buffered.Stage).IsEqualTo(RespireCommandStage.Buffered);
        await Assert.That(shared.Stage).IsEqualTo(RespireCommandStage.Unknown);
        foreach (var snapshot in new[] { replied, writing, buffered })
        {
            await Assert.That(snapshot.InflightCount).IsEqualTo(3);
            await Assert.That(snapshot.InflightBytes).IsEqualTo(30);
            await Assert.That(snapshot.PendingWriteBytes).IsEqualTo(15);
            await Assert.That(snapshot.PendingWorkItems).IsEqualTo(shared.PendingWorkItems);
            await Assert.That(snapshot.BusyWorkerThreads).IsEqualTo(shared.BusyWorkerThreads);
        }
    }

    [Test]
    public async Task PhysicalSnapshot_DoesNotReportAnotherSlotsReconnect()
    {
        await using var server = new FakeRespServer(3, FakeRespServer.PongReply);
        await using var multiplexer = await RespireConnectionMultiplexer.CreateAsync(
            "127.0.0.1", server.Port, connectionCount: 2);
        var healthy = multiplexer.GetConnection();
        var failed = multiplexer.GetConnection();
        var observed = new TaskCompletionSource<RespireTimeoutDiagnostics>(TaskCreationOptions.RunContinuationsAsynchronously);
        multiplexer.StateChanged += change =>
        {
            if (change.State == RespireConnectionState.Reconnecting)
                observed.TrySetResult(healthy.CaptureTimeoutDiagnostics());
        };
        await failed.DisposeAsync();
        multiplexer.GetConnection();
        multiplexer.GetConnection();
        var snapshot = await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(snapshot.IsConnected).IsTrue();
        await Assert.That(snapshot.IsReconnecting).IsFalse();
    }

    private static ValueTask<RespireClient> ConnectAsync(int port)
        => RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint("127.0.0.1", port) },
            Connections = 1, CommandTimeout = TimeSpan.FromMilliseconds(200)
        });
}
