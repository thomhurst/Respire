using System.Net;
using System.Net.Sockets;
using System.Text;
using Respire;
using Respire.Commands;
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

        var tasks = new List<Task<RespireValue>>();
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
    public async Task ManyCommands_AreCoalescedAndAllAnswered()
    {
        await using var server = new FakeRespServer("+OK\r\n"u8.ToArray());
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port);

        var tasks = new List<Task<RespireValue>>();
        for (var i = 0; i < 1000; i++)
        {
            tasks.Add(connection.SendAsync(new KeyValueCommand(CommandPrefixes.Set, $"key{i}", $"value{i}")).AsTask());
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
}
