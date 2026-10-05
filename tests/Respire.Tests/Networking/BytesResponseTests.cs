using System.Text;
using System.Runtime.CompilerServices;
using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class BytesResponseTests
{
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(1024)]
    [Arguments(4095)]
    [Arguments(4096)]
    [Arguments(1048576)]
    public async Task BufferedAndFragmentedRepliesReturnOwnedBytes(int length)
    {
        var expected = Enumerable.Range(0, length).Select(i => (byte)(i % 251)).ToArray();
        await using var server = new FakeRespServer { SuppressReply = _ => true };
        await using var connection = await Connect(server);
        var command = new Cmd1(Verbs.Get, "key");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = connection.SendBytesAsync(command, deadline.Token, "GET").AsTask();
        await WaitForCommands(server, 1, deadline.Token);
        await server.SendRawAsync([.. Encoding.ASCII.GetBytes($"${length}\r\n"), .. expected, 13, 10]);
        var owned = await first.WaitAsync(deadline.Token);
        await Assert.That(owned!.AsSpan().SequenceEqual(expected)).IsTrue();

        var second = connection.SendBytesAsync(command, deadline.Token, "GET").AsTask();
        await WaitForCommands(server, 2, deadline.Token);
        // Attributes and fragmented framing must not change the top-level reply's ownership.
        await server.SendRawAsync("|1\r\n+meta\r\n+value\r\n$"u8.ToArray());
        await server.SendRawAsync(Encoding.ASCII.GetBytes($"{length}\r\n"));
        var half = length / 2;
        if (half != 0) await server.SendRawAsync(expected[..half]);
        await server.SendRawAsync([.. expected[half..], 13]);
        await server.SendRawAsync([10]);
        var next = await second.WaitAsync(deadline.Token);
        await Assert.That(next!.AsSpan().SequenceEqual(expected)).IsTrue();
        if (length > 0)
        {
            next![0] ^= 0xff;
            await Assert.That(owned.AsSpan().SequenceEqual(expected)).IsTrue();
        }
    }

    [Test]
    public async Task NullEmptyAndErrorPreserveReplyOrder()
    {
        await using var server = new FakeRespServer("$-1\r\n"u8.ToArray(), "$0\r\n\r\n"u8.ToArray(), "-ERR failed\r\n"u8.ToArray(), "$1\r\nx\r\n"u8.ToArray());
        await using var connection = await Connect(server);
        var command = new Cmd1(Verbs.Get, "key");
        await Assert.That(await connection.SendBytesAsync(command, commandName: "GET")).IsNull();
        await Assert.That((await connection.SendBytesAsync(command, commandName: "GET"))!.Length).IsEqualTo(0);
        await Assert.That(async () => await connection.SendBytesAsync(command, commandName: "GET")).Throws<RespireServerException>();
        await Assert.That((await connection.SendBytesAsync(command, commandName: "GET"))![0]).IsEqualTo((byte)'x');
    }

    [Test]
    public async Task CanceledLargeReplyIsDrainedBeforeNextReply()
    {
        await using var server = new FakeRespServer { SuppressReply = _ => true };
        await using var connection = await Connect(server);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource();
        var command = new Cmd1(Verbs.Get, "key");
        var canceled = connection.SendBytesAsync(command, cancellation.Token, "GET").AsTask();
        await WaitForCommands(server, 1, deadline.Token);
        await server.SendRawAsync("$8192\r\nx"u8.ToArray());
        cancellation.Cancel();
        await Assert.That(async () => await canceled.WaitAsync(deadline.Token)).Throws<OperationCanceledException>();
        var next = connection.SendBytesAsync(command, deadline.Token, "GET").AsTask();
        await WaitForCommands(server, 2, deadline.Token);
        await server.SendRawAsync([.. new byte[8191], 13, 10, .. "$1\r\nz\r\n"u8]);
        await Assert.That((await next.WaitAsync(deadline.Token))![0]).IsEqualTo((byte)'z');
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TruncatedOrMalformedLargeReplyFailsWithoutPublishingArray(bool malformed)
    {
        await using var server = new FakeRespServer { SuppressReply = _ => true };
        await using var connection = await Connect(server);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = connection.SendBytesAsync(new Cmd1(Verbs.Get, "key"), deadline.Token, "GET").AsTask();
        await WaitForCommands(server, 1, deadline.Token);
        if (malformed) await server.SendRawAsync([.. "$8192\r\n"u8, .. new byte[8192], 0, 0]);
        else
        {
            await server.SendRawAsync("$8192\r\nx"u8.ToArray());
            await server.DisposeAsync();
        }
        await Assert.That(async () => await pending.WaitAsync(deadline.Token)).Throws<RespireException>();
    }

    [Test]
    [Arguments(0, false)]
    [Arguments(1, false)]
    [Arguments(2, false)]
    [Arguments(3, false)]
    [Arguments(0, true)]
    [Arguments(1, true)]
    [Arguments(2, true)]
    [Arguments(3, true)]
    public async Task PublicGetPathsPreserveCallerOwnership(int mode, bool cached)
    {
        var expected = Enumerable.Range(0, 8192).Select(i => (byte)(i % 251)).ToArray();
        byte[] reply = [.. "$8192\r\n"u8, .. expected, 13, 10];
        await using var server = new FakeRespServer
        {
            SuppressReply = command => command.StartsWith("GET ", StringComparison.Ordinal),
            ReplyOverride = (_, command) => command.StartsWith("GET ", StringComparison.Ordinal)
                ? reply : command == "HELLO 3" ? "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray() : FakeRespServer.OkReply,
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            Connections = 1,
            ClientSideCache = cached ? new() : null,
        });
        var view = client.WithKeyPrefix("tenant:");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = Read(view, mode, deadline.Token).AsTask();
        while (!server.ReceivedCommands.Contains("GET tenant:key")) await Task.Delay(1, deadline.Token);
        var ring = Inflight(client.Core.Multiplexer.GetConnection());
        await Assert.That(ring.TryPeek(out var head)).IsTrue();
        await Assert.That(head is BytesPendingResponseSource).IsEqualTo(!cached && !RespireTelemetry.IsEnabled);
        await server.SendRawAsync(reply);
        var first = await pending.WaitAsync(deadline.Token);
        await Assert.That(first!.AsSpan().SequenceEqual(expected)).IsTrue();
        first![0] ^= 0xff;
        var secondPending = Read(view, mode, deadline.Token).AsTask();
        if (!cached)
        {
            while (server.ReceivedCommands.Count(command => command == "GET tenant:key") < 2)
                await Task.Delay(1, deadline.Token);
            await server.SendRawAsync(reply);
        }
        var second = await secondPending.WaitAsync(deadline.Token);
        await Assert.That(second!.AsSpan().SequenceEqual(expected)).IsTrue();
        await Assert.That(server.ReceivedCommands.Count(command => command == "GET tenant:key"))
            .IsEqualTo(cached ? 1 : 2);
    }

    [Test]
    public async Task LargePushPayloadDoesNotConsumePendingByteReply()
    {
        var pushed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer { SuppressReply = _ => true };
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port, new()
        {
            Protocol = RespProtocol.Resp2,
            PushHandler = (in RespValue value) => pushed.TrySetResult(value.AsArray()[1].AsSpan().Length),
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = connection.SendBytesAsync(new Cmd1(Verbs.Get, "key"), deadline.Token, "GET").AsTask();
        await WaitForCommands(server, 1, deadline.Token);
        await server.SendRawAsync([.. ">2\r\n+notice\r\n$8192\r\n"u8, .. new byte[8192], 13, 10, .. "$1\r\nz\r\n"u8]);
        await Assert.That(await pushed.Task.WaitAsync(deadline.Token)).IsEqualTo(8192);
        await Assert.That((await pending.WaitAsync(deadline.Token))![0]).IsEqualTo((byte)'z');
    }

    [Test]
    [Arguments("+hello\r\n")]
    [Arguments("=9\r\ntxt:hello\r\n")]
    public async Task OtherStringShapesRetainConversion(string frame)
    {
        await using var server = new FakeRespServer(Encoding.ASCII.GetBytes(frame));
        await using var connection = await Connect(server);
        var result = await connection.SendBytesAsync(new Cmd1(Verbs.Get, "key"), commandName: "GET");
        await Assert.That(result!.AsSpan().SequenceEqual("hello"u8)).IsTrue();
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_inflight")]
    private static extern ref InflightRing Inflight(RespireConnection connection);

    private static ValueTask<byte[]?> Read(IRespireClient client, int mode, CancellationToken token)
        => mode switch
        {
            0 => client.GetBytesAsync("key", token),
            1 => client.GetAsync<byte[]>("key", token),
            2 => client.Strings.GetBytesAsync("key", token),
            _ => client.Strings.GetAsync<byte[]>("key", token),
        };

    private static Task<RespireConnection> Connect(FakeRespServer server)
        => RespireConnection.ConnectAsync("127.0.0.1", server.Port, new() { Protocol = RespProtocol.Resp2 });

    private static async Task WaitForCommands(FakeRespServer server, int count, CancellationToken token)
    {
        while (server.CommandsSeen < count) await Task.Delay(1, token);
    }
}
