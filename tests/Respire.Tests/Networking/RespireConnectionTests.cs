using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Respire;
using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Core;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Respire.Tests.Networking;

/// <summary>
/// End-to-end wire tests against an in-process RESP server: write coalescing, FIFO
/// response pairing, large-payload direct fill, and connection-death fault propagation.
/// </summary>
public class RespireConnectionTests
{
    [Test]
    public async Task SingleCommand_RoundTrips()
    {
        await using var server = new FakeRespServer("+PONG\r\n"u8.ToArray());
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port);

        var response = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame));

        await Assert.That(response.AsString()).IsEqualTo("PONG");
        response.Dispose();
    }

    [Test]
    public async Task PipelinedCommands_CompleteInFifoOrder()
    {
        var replies = Enumerable.Range(0, 50)
            .Select(i => Encoding.UTF8.GetBytes($":{i}\r\n"))
            .ToArray();
        await using var server = new FakeRespServer(replies);
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port);

        var tasks = new List<Task<RespValue>>();
        for (var i = 0; i < 50; i++)
        {
            tasks.Add(connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask());
        }

        var results = await Task.WhenAll(tasks);
        for (var i = 0; i < 50; i++)
        {
            await Assert.That(results[i].AsInteger()).IsEqualTo(i);
        }
    }

    [Test]
    public async Task RingFull_Backpressure_WaitsForCapacity()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        server.DelayReply(0, 500);
        await using var connection = await RespireConnection.ConnectAsync(
            "127.0.0.1", server.Port, new RespireConnectionOptions { MaxInflightCommands = 2 });

        var first = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        var second = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (server.CommandsSeen == 0)
        {
            await Task.Delay(10, timeout.Token);
        }

        var third = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        await Task.Delay(100, timeout.Token);

        await Assert.That(third.IsCompleted).IsFalse();

        var responses = await Task.WhenAll(first, second, third).WaitAsync(timeout.Token);
        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Test]
    public async Task RingFull_ConnectionFailureFaultsCapacityWaiters()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var commandsReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeConnection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptSocketAsync();
            var buffer = new byte[1024];
            await socket.ReceiveAsync(buffer, SocketFlags.None);
            commandsReceived.SetResult();
            await closeConnection.Task;
        });

        await using var connection = await RespireConnection.ConnectAsync(
            "127.0.0.1", port, new RespireConnectionOptions { MaxInflightCommands = 2 });
        var first = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        var second = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        await commandsReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var capacityWaiter = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();

        await Task.Delay(100);
        await Assert.That(capacityWaiter.IsCompleted).IsFalse();
        closeConnection.SetResult();

        var all = Task.WhenAll(first, second, capacityWaiter);
        await Assert.That(async () => await all.WaitAsync(TimeSpan.FromSeconds(5)))
            .Throws<RespireConnectionException>();
        await Assert.That(capacityWaiter.IsFaulted).IsTrue();

        await server;
        listener.Stop();
    }

    [Test]
    public async Task ConvertedPipelinedCommands_CompleteInFifoOrder()
    {
        const int batchSize = 50;
        var replies = Enumerable.Range(0, batchSize * 2)
            .Select(i => Encoding.UTF8.GetBytes($":{i % batchSize}\r\n"))
            .ToArray();
        await using var server = new FakeRespServer(replies);
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port);

        var pending = new ValueTask<long>[batchSize];
        for (var batch = 0; batch < 2; batch++)
        {
            for (var i = 0; i < pending.Length; i++)
            {
                pending[i] = connection.SendConvertedAsync(
                    new RawCommand(FakeRespServer.PingFrame),
                    state: 0,
                    static (int _, in RespValue response) => ResponseReader.Integer(in response),
                    transferOwnership: false);
            }

            for (var i = 0; i < pending.Length; i++)
            {
                await Assert.That(await pending[i]).IsEqualTo(i);
            }
        }
    }

    [Test]
    public async Task ConvertedCommand_ErrorReplyThrowsServerException()
    {
        await using var server = new FakeRespServer("-WRONGTYPE bad value\r\n"u8.ToArray());
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port);

        var pending = connection.SendConvertedAsync(
            new RawCommand(FakeRespServer.PingFrame),
            state: 0,
            static (int _, in RespValue response) => ResponseReader.String(in response),
            transferOwnership: false);

        await Assert.That(async () => await pending).ThrowsExactly<RespireServerException>();
    }

    [Test]
    public async Task ConvertedCommand_CancellationPreservesFifo()
    {
        await using var server = new FakeRespServer(
            "$5\r\nfirst\r\n"u8.ToArray(),
            "$6\r\nsecond\r\n"u8.ToArray());
        server.DelayReply(0, 100);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var cancellation = new CancellationTokenSource();
        var first = client.GetStringAsync("first", cancellation.Token);

        using var commandTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (server.CommandsSeen == 0)
        {
            await Task.Delay(10, commandTimeout.Token);
        }

        cancellation.Cancel();
        await Assert.That(async () => await first)
            .ThrowsExactly<OperationCanceledException>();

        await Assert.That(await client.GetStringAsync("second")).IsEqualTo("second");
    }

    [Test]
    public async Task LargeBulkString_DirectFillRoundTrips()
    {
        // Well above the 4 KB direct-fill threshold, and larger than the receive buffer.
        var payload = new string('x', 256 * 1024);
        var reply = Encoding.UTF8.GetBytes($"${payload.Length}\r\n{payload}\r\n");
        await using var server = new FakeRespServer(reply);
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port);

        var response = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame));

        await Assert.That(response.Type).IsEqualTo(RespDataType.BulkString);
        await Assert.That(response.AsString()).IsEqualTo(payload);
        response.Dispose();
    }

    [Test]
    public async Task LargeBulkStringNestedInArray_DirectFillRoundTrips()
    {
        var payload = new string('n', 256 * 1024);
        var reply = Encoding.UTF8.GetBytes($"*2\r\n${payload.Length}\r\n{payload}\r\n:7\r\n");
        await using var server = new FakeRespServer(reply);
        await using var connection = await RespireConnection.ConnectAsync(
            "127.0.0.1", server.Port, new RespireConnectionOptions { ReceiveBufferSize = 128 });

        var response = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame));
        var receivedPayload = response.AsArray()[0].AsString();
        var receivedInteger = response.AsArray()[1].AsInteger();

        await Assert.That(receivedPayload).IsEqualTo(payload);
        await Assert.That(receivedInteger).IsEqualTo(7);
        response.Dispose();
    }

    [Test]
    public async Task ManyCommands_AreCoalescedAndAllAnswered()
    {
        await using var server = new FakeRespServer("+OK\r\n"u8.ToArray());
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port);

        var tasks = new List<Task<RespValue>>();
        for (var i = 0; i < 1000; i++)
        {
            tasks.Add(connection.SendAsync(new SetCommand($"key{i}", $"value{i}")).AsTask());
        }

        var results = await Task.WhenAll(tasks);

        await Assert.That(server.CommandsSeen).IsEqualTo(1000);
        foreach (var result in results)
        {
            await Assert.That(result.AsString()).IsEqualTo("OK");
            result.Dispose();
        }
    }

    [Test]
    public async Task FireAndForget_ResponseIsConsumedNotDelivered()
    {
        await using var server = new FakeRespServer("+OK\r\n"u8.ToArray(), "+PONG\r\n"u8.ToArray());
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port);

        await connection.SendFireAndForgetAsync(new RawCommand(FakeRespServer.PingFrame));
        var response = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame));

        // The discarded first reply ("+OK") must not be delivered to the second command.
        await Assert.That(response.AsString()).IsEqualTo("PONG");
        response.Dispose();
    }

    [Test]
    public async Task FireAndForget_AwaitsWriteBeforeImmediateDisposal()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        server.SuppressReply = _ => true;
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port);

        var pending = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (server.CommandsSeen == 0)
        {
            await Task.Delay(10, timeout.Token);
        }

        await connection.SendFireAndForgetAsync(new RawCommand(FakeRespServer.PingFrame));
        await connection.DisposeAsync();

        while (server.CommandsSeen < 2)
        {
            await Task.Delay(10, timeout.Token);
        }

        await Assert.That(server.CommandsSeen).IsEqualTo(2);
        await Assert.That(async () => await pending).Throws<RespireConnectionException>();
    }

    [Test]
    public async Task ServerCloses_InFlightCommandsFail()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var acceptTask = Task.Run(async () =>
        {
            using var socket = await listener.AcceptSocketAsync();
            var buffer = new byte[1024];
            await socket.ReceiveAsync(buffer, SocketFlags.None);
            socket.Close();
        });

        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", port);

        var task = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();

        await Assert.That(async () => await task).Throws<RespireConnectionException>();
        await acceptTask;
        listener.Stop();
    }

    [Test]
    public async Task ResponseWatchdog_NoBytesWhilePending_AbortsConnection()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        server.DelayReply(0, 1000);
        await using var connection = await RespireConnection.ConnectAsync(
            "127.0.0.1",
            server.Port,
            new RespireConnectionOptions { ResponseTimeout = TimeSpan.FromMilliseconds(100) });

        var response = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();

        var exception = await Assert.That(async () => await response)
            .ThrowsExactly<RespireConnectionException>();
        await Assert.That(exception!.Message).Contains("received no data");
        await Assert.That(connection.IsConnected).IsFalse();
    }

    [Test]
    public async Task ResponseWatchdog_UsesRemainingDeadlineInsteadOfPollingPeriod()
    {
        var timeout = TimeSpan.FromMilliseconds(500);
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        server.DelayReply(0, 2000);
        await using var connection = await RespireConnection.ConnectAsync(
            "127.0.0.1",
            server.Port,
            new RespireConnectionOptions { ResponseTimeout = timeout });

        // Start just after the watchdog's first idle wait. A timeout-sized periodic poll would
        // miss the first deadline and take almost two timeout periods to abort.
        await Task.Delay(TimeSpan.FromMilliseconds(600));
        var stopwatch = Stopwatch.StartNew();
        var response = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();

        await Assert.That(async () => await response).ThrowsExactly<RespireConnectionException>();
        await Assert.That(stopwatch.Elapsed < TimeSpan.FromMilliseconds(750)).IsTrue();
    }

    [Test]
    public async Task ResponseWatchdog_DoesNotAbortIntentionallyBlockingConnection()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        server.DelayReply(0, 250);
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            ResponseTimeout = TimeSpan.FromMilliseconds(50),
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var response = await client.SendBlockingAsync(
            "PING", new RawCommand(FakeRespServer.PingFrame), timeout.Token);

        await Assert.That(response.AsString()).IsEqualTo("PONG");
        response.Dispose();
    }

    [Test]
    public async Task ResponseWatchdog_StillAppliesToNonBlockingDedicatedConnection()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        server.DelayReply(0, 250);
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            ResponseTimeout = TimeSpan.FromMilliseconds(50),
        });
        var connection = await client.Core.DedicatedPool.RentAsync(CancellationToken.None);

        await Assert.That(async () =>
                await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)))
            .ThrowsExactly<RespireConnectionException>();

        await client.Core.DedicatedPool.DiscardAsync(connection);
    }

    [Test]
    public async Task ResponseWatchdog_ChunksTimeoutsBeyondTaskDelayLimit()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        await using var connection = await RespireConnection.ConnectAsync(
            "127.0.0.1",
            server.Port,
            new RespireConnectionOptions { ResponseTimeout = TimeSpan.FromDays(60) });

        var response = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame));

        await Assert.That(response.AsString()).IsEqualTo("PONG");
        response.Dispose();
    }

    [Test]
    public async Task ResponseWatchdog_RejectsSubMillisecondTimeout()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);

        await Assert.That(async () => await RespireConnection.ConnectAsync(
                "127.0.0.1",
                server.Port,
                new RespireConnectionOptions { ResponseTimeout = TimeSpan.FromTicks(1) }))
            .ThrowsExactly<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task ResponseWatchdog_ImmediatePeerCloseDisposesPromptly()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var acceptTask = Task.Run(async () =>
        {
            using var socket = await listener.AcceptSocketAsync();
            socket.Close();
        });
        var connection = await RespireConnection.ConnectAsync(
            "127.0.0.1",
            port,
            new RespireConnectionOptions { ResponseTimeout = TimeSpan.FromDays(60) });
        await acceptTask;

        await connection.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        listener.Stop();
    }

    [Test]
    public async Task SendAfterDeath_ThrowsConnectionException()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var acceptTask = Task.Run(async () =>
        {
            using var socket = await listener.AcceptSocketAsync();
            socket.Close();
        });

        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", port);
        await acceptTask;

        // Wait for the receive loop to observe the close.
        while (connection.IsConnected)
        {
            await Task.Delay(10);
        }

        await Assert.That(async () => await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)))
            .Throws<RespireConnectionException>();
        listener.Stop();
    }

    [Test]
    public async Task ErrorReply_IsDeliveredAsErrorValue()
    {
        await using var server = new FakeRespServer("-ERR bad things\r\n"u8.ToArray());
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port);

        var response = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame));

        await Assert.That(response.IsError).IsTrue();
        await Assert.That(response.GetErrorMessage()).IsEqualTo("ERR bad things");
        response.Dispose();
    }

    [Test]
    public async Task ArrayReply_RoundTrips()
    {
        await using var server = new FakeRespServer("*3\r\n$3\r\nfoo\r\n:42\r\n$-1\r\n"u8.ToArray());
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port);

        var response = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame));

        await Assert.That(response.Type).IsEqualTo(RespDataType.Array);
        var count = response.AsArray().Length;
        var first = response.AsArray()[0].AsString();
        var second = response.AsArray()[1].AsInteger();
        var thirdIsNull = response.AsArray()[2].IsNull;
        await Assert.That(count).IsEqualTo(3);
        await Assert.That(first).IsEqualTo("foo");
        await Assert.That(second).IsEqualTo(42);
        await Assert.That(thirdIsNull).IsTrue();
        response.Dispose();
    }

    /// <summary>SET key value, serialized through the public writer API.</summary>
    private readonly struct SetCommand(string key, string value) : IRespCommand
    {
        public void Write(ref RespWriter writer)
        {
            writer.WriteArrayHeader(3);
            writer.WriteBulkString("SET"u8);
            writer.WriteBulkString(key);
            writer.WriteBulkString(value);
        }
    }
}
