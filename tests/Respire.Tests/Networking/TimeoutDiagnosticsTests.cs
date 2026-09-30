using Respire.Commands;
using Respire.Networking;
using System.Net;
using System.Net.Sockets;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class TimeoutDiagnosticsTests
{
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
        await Assert.That(diagnostics.IsConnected).IsEqualTo(true);
        await Assert.That(error.Message.Contains("secret-key", StringComparison.Ordinal)).IsFalse();
        await Assert.That(error.Message.Contains("Stage=AwaitingReply", StringComparison.Ordinal)).IsTrue();
        await Assert.That(diagnostics.BusyWorkerThreads).IsGreaterThanOrEqualTo(0);
        await Assert.That(diagnostics.PendingWorkItems).IsGreaterThanOrEqualTo(0);
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
        await using var transaction = watched
            ? await client.CreateTransactionAsync(["key"])
            : client.CreateTransaction();
        var pending = transaction.GetString("key");
        var error = await Assert.That(async () => await transaction.CommitAsync())
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
    public async Task StalledWrite_ReportsBufferedCommandAndPayloadBacklog()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptSocketAsync();
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", port,
            new RespireConnectionOptions { SocketSendBufferSize = 1024, CommandTimeout = TimeSpan.FromMilliseconds(500) });
        using var peer = await accept;
        peer.ReceiveBufferSize = 1024;
        // The peer deliberately never reads. A payload exceeding both socket buffers keeps
        // the persistent sender occupied while the next command remains in the active buffer.
        using var cancellation = new CancellationTokenSource();
        var large = connection.SendAsync(new RawCommand(new byte[8 * 1024 * 1024]), cancellation.Token,
            armCommandDeadline: false).AsTask();
        var error = await Assert.That(async () => await connection.SendAsync(
                new RawCommand(FakeRespServer.PingFrame), commandName: "PING"))
            .ThrowsExactly<RespireTimeoutException>();
        await Assert.That(error!.Diagnostics.Stage).IsEqualTo(RespireCommandStage.Buffered);
        await Assert.That(error.Diagnostics.PendingWriteBytes.GetValueOrDefault()).IsGreaterThan(1024);
        await Assert.That(error.Diagnostics.InflightCount).IsEqualTo(2);
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

    private static ValueTask<RespireClient> ConnectAsync(int port)
        => RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint("127.0.0.1", port) },
            Connections = 1, CommandTimeout = TimeSpan.FromMilliseconds(200)
        });
}
