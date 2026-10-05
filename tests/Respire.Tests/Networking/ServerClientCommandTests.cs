using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ServerClientCommandTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CommandsUsePinnedConnectionAndReturnOwnedData(bool resp3)
    {
        byte[] binary = [255, 0, 128];
        byte[][] fields = [Bulk("flags"), Sequence(resp3 ? '~' : '*', Bulk("on"), Bulk("future")),
            Bulk("redirect"), Integer(-1), Bulk("prefixes"), Sequence('*', Bulk(binary)),
            Bulk("future-field"), Sequence('*', Bulk(binary))];
        byte[][] replies = [Integer(42), Bulk("id=42 addr=host:1 name=worker db=2 flags=N cmd=client|info user=default age=4 idle=0 future=value\n"),
            resp3 ? "_\r\n"u8.ToArray() : "$-1\r\n"u8.ToArray(),
            .. Enumerable.Repeat(FakeRespServer.OkReply, 9), Integer(1), Integer(0),
            Sequence(resp3 ? '%' : '*', fields), Bulk(binary)];
        if (resp3) replies = ["%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(), .. replies];
        await using var server = new FakeRespServer(replies);
        await using var client = await RespireClient.ConnectAsync(Options(server.Port) with { Protocol = resp3 ? RespProtocol.Resp3 : RespProtocol.Resp2 });
        var connection = await client.WithKeyPrefix("ignored:").Server.GetClientConnectionAsync();
        await Assert.That(connection.Id).IsEqualTo(42);
        await Assert.That(connection.Endpoint.Port).IsEqualTo(server.Port);
        var info = await connection.InfoAsync();
        await Assert.That(await connection.GetNameAsync()).IsNull();
        await connection.SetInfoAsync(RespireClientInfoAttribute.LibraryName, "library");
        await connection.SetInfoAsync(RespireClientInfoAttribute.LibraryVersion, "1.0");
        await connection.SetNoEvictAsync(true);
        await connection.SetNoEvictAsync(false);
        await connection.SetNoTouchAsync(true);
        await connection.SetNoTouchAsync(false);
        await connection.PauseClientsAsync(TimeSpan.FromMilliseconds(12));
        await connection.PauseClientsAsync(TimeSpan.Zero, RespireClientPauseMode.All);
        await connection.UnpauseClientsAsync();
        await Assert.That(await connection.UnblockClientAsync(50)).IsTrue();
        await Assert.That(await connection.UnblockClientAsync(51, RespireClientUnblockMode.Error)).IsFalse();
        var tracking = await connection.TrackingInfoAsync();
        var echo = await connection.EchoAsync(binary);
        await client.DisposeAsync();
        await Assert.That(info.Attributes["future"]).IsEqualTo("value");
        await Assert.That(tracking.Flags).IsEquivalentTo(["on", "future"]);
        await Assert.That(tracking.RedirectClientId).IsEqualTo(-1);
        await Assert.That(tracking.Prefixes[0]).IsEquivalentTo(binary, CollectionOrdering.Matching);
        await Assert.That(tracking.Fields["future-field"][0].AsBytes()).IsEquivalentTo(binary, CollectionOrdering.Matching);
        await Assert.That(echo).IsEquivalentTo(binary, CollectionOrdering.Matching);
        await Assert.That(connection.IsConnected).IsFalse();
        await Assert.That(server.ReceivedCommands.Where(command => command != "HELLO 3").Take(15)).IsEquivalentTo([
            "CLIENT ID", "CLIENT INFO", "CLIENT GETNAME", "CLIENT SETINFO LIB-NAME library", "CLIENT SETINFO LIB-VER 1.0",
            "CLIENT NO-EVICT ON", "CLIENT NO-EVICT OFF", "CLIENT NO-TOUCH ON", "CLIENT NO-TOUCH OFF",
            "CLIENT PAUSE 12 WRITE", "CLIENT PAUSE 0 ALL", "CLIENT UNPAUSE", "CLIENT UNBLOCK 50 TIMEOUT",
            "CLIENT UNBLOCK 51 ERROR", "CLIENT TRACKINGINFO"], CollectionOrdering.Matching);
        await Assert.That(server.ReceivedArguments[^1][1]).IsEquivalentTo(binary, CollectionOrdering.Matching);
    }

    [Test]
    public async Task AdminAndInvalidOptionsFailBeforeSending()
    {
        await using var server = new FakeRespServer(Integer(1));
        await using var client = await RespireClient.ConnectAsync(Options(server.Port) with { AllowAdmin = false });
        var connection = await client.Server.GetClientConnectionAsync();
        Func<ValueTask>[] changes = [() => connection.SetInfoAsync(RespireClientInfoAttribute.LibraryName, "name"),
            () => connection.SetNoEvictAsync(true), () => connection.SetNoTouchAsync(true),
            () => connection.PauseClientsAsync(TimeSpan.FromSeconds(1)), () => connection.UnpauseClientsAsync(),
            async () => { _ = await connection.UnblockClientAsync(2); }];
        foreach (var change in changes)
            await Assert.That(async () => await change()).ThrowsExactly<NotSupportedException>();
        await Assert.That(server.CommandsSeen).IsEqualTo(1);
    }

    [Test]
    public async Task InvalidArgumentsAndPreCancellationDoNotTouchSocket()
    {
        await using var server = new FakeRespServer(Integer(1));
        await using var client = RespireClient.Create(Options(server.Port));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.That(async () => await client.Server.GetClientConnectionAsync(cancelled.Token)).Throws<OperationCanceledException>();
        await Assert.That(server.CommandsSeen).IsEqualTo(0);
        var connection = await client.Server.GetClientConnectionAsync();
        await Assert.That(async () => await connection.SetInfoAsync((RespireClientInfoAttribute)99, "x")).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await connection.PauseClientsAsync(TimeSpan.FromTicks(1))).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await connection.PauseClientsAsync(TimeSpan.FromMilliseconds(-1))).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await connection.PauseClientsAsync(TimeSpan.Zero, (RespireClientPauseMode)99)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await connection.UnblockClientAsync(0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await connection.UnblockClientAsync(1, (RespireClientUnblockMode)99)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await connection.InfoAsync(cancelled.Token)).Throws<OperationCanceledException>();
        await Assert.That(async () => await connection.GetNameAsync(cancelled.Token)).Throws<OperationCanceledException>();
        await Assert.That(async () => await connection.TrackingInfoAsync(cancelled.Token)).Throws<OperationCanceledException>();
        await Assert.That(async () => await connection.EchoAsync(new byte[] { 1 }, cancelled.Token)).Throws<OperationCanceledException>();
        await Assert.That(async () => await connection.SetNoTouchAsync(true, cancelled.Token)).Throws<OperationCanceledException>();
        await Assert.That(server.CommandsSeen).IsEqualTo(1);
        await client.DisposeAsync();
        await Assert.That(async () => await connection.InfoAsync()).ThrowsExactly<ObjectDisposedException>();
    }

    [Test]
    public async Task HandlesDoNotRotateAcrossMultiplexedConnections()
    {
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply);
        server.SuppressReply = command =>
        {
            if (command != "CLIENT ID") return false;
            var socket = server.ReceivedConnectionIds[^1];
            _ = server.SendRawAsync(Integer(socket + 100), socket);
            return true;
        };
        await using var client = await RespireClient.ConnectAsync(Options(server.Port) with { Connections = 2 });
        var first = await client.Server.GetClientConnectionAsync();
        var second = await client.Server.GetClientConnectionAsync();
        await Assert.That(first.Id).IsNotEqualTo(second.Id);
        await first.SetNoEvictAsync(true);
        await second.SetNoTouchAsync(true);
        await first.SetInfoAsync(RespireClientInfoAttribute.LibraryName, "first");
        await Assert.That(server.ReceivedConnectionIds.Skip(2)).IsEquivalentTo(
            [(int)first.Id - 100, (int)second.Id - 100, (int)first.Id - 100], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ServerErrorsDoNotReplayOrDisturbFollowingReplies()
    {
        await using var server = new FakeRespServer(Integer(1), "-ERR unknown subcommand\r\n"u8.ToArray(), Bulk("after"));
        await using var client = await RespireClient.ConnectAsync(Options(server.Port));
        var connection = await client.Server.GetClientConnectionAsync();
        await Assert.That(async () => await connection.SetNoTouchAsync(true)).ThrowsExactly<RespireServerException>();
        await Assert.That(await connection.GetNameAsync()).IsEqualTo("after");
        await Assert.That(server.CommandsSeen).IsEqualTo(3);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PendingCommandsObserveCancellationAndClientDisposal(bool dispose)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(Integer(1));
        server.SuppressReply = command =>
        {
            if (command != "CLIENT INFO") return false;
            received.TrySetResult();
            return true;
        };
        await using var client = await RespireClient.ConnectAsync(Options(server.Port));
        var connection = await client.Server.GetClientConnectionAsync();
        using var cancellation = new CancellationTokenSource();
        var pending = connection.InfoAsync(cancellation.Token).AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (dispose) await client.DisposeAsync();
        else cancellation.Cancel();
        if (dispose)
            await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5))).Throws<Exception>();
        else
        {
            var error = await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5))).Throws<OperationCanceledException>();
            await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        }
        await Assert.That(server.CommandsSeen).IsEqualTo(2);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task ClusterClientListsKeepEndpointSuccessAndFailure(bool verbatim, bool filtered)
    {
        await using var replica = new FakeRespServer("-NOPERM CLIENT LIST denied\r\n"u8.ToArray());
        await using var seed = new FakeRespServer(2, Bulk("id=7 addr=a name= db=0 flags=N cmd=client|list age=0 idle=0\n"));
        seed.SuppressReply = command =>
        {
            if (command == "CLUSTER SLOTS") { _ = seed.SendRawAsync("*0\r\n"u8.ToArray()); return true; }
            if (command != "CLUSTER NODES") return false;
            var topology = $"self 127.0.0.1:{seed.Port}@2 myself,master - 0 0 1 connected\nreplica 127.0.0.1:{replica.Port}@2 slave self 0 0 1 connected\n";
            _ = seed.SendRawAsync(verbatim
                ? Encoding.ASCII.GetBytes($"={topology.Length + 4}\r\ntxt:{topology}\r\n")
                : Bulk(topology));
            return true;
        };
        await using var client = await RespireClient.ConnectAsync(Options(seed.Port) with { UseCluster = true });
        var results = filtered
            ? await client.Server.ClientsOnAllNodesAsync(new() { Type = RespireClientType.Normal, Ids = [7] }, default)
            : await client.Server.ClientsOnAllNodesAsync();
        await Assert.That(results.Length).IsEqualTo(2);
        await Assert.That(results.Single(row => row.Endpoint.Port == seed.Port).Value[0].Id).IsEqualTo(7);
        await Assert.That(results.Single(row => row.Endpoint.Port == replica.Port).Error).IsTypeOf<RespireServerException>();
        var expected = filtered ? "CLIENT LIST TYPE normal ID 7" : "CLIENT LIST";
        await Assert.That(seed.ReceivedCommands.Contains(expected)).IsTrue();
        await Assert.That(replica.ReceivedCommands.Contains(expected)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PinnedClusterControlsNeverFollowRedirects(bool kill)
    {
        await using var target = new FakeRespServer(FakeRespServer.OkReply);
        await using var seed = new FakeRespServer("*0\r\n"u8.ToArray(), Integer(1),
            Encoding.ASCII.GetBytes($"-MOVED 0 127.0.0.1:{target.Port}\r\n"));
        await using var client = await RespireClient.ConnectAsync(Options(seed.Port) with { UseCluster = true });
        var connection = await client.Server.GetClientConnectionAsync();
        var error = await Assert.That(async () =>
        {
            if (kill) await connection.KillClientsAsync(new() { Ids = [42] });
            else await connection.PauseClientsAsync(TimeSpan.Zero);
        }).ThrowsExactly<RespireServerException>();
        await Assert.That(error!.Code).IsEqualTo("MOVED");
        await Assert.That(connection.Endpoint.Port).IsEqualTo(seed.Port);
        await Assert.That(target.CommandsSeen).IsEqualTo(0);
        await Assert.That(seed.CommandsSeen).IsEqualTo(3);
    }

    [Test]
    [Arguments("*1\r\n$5\r\nflags\r\n")]
    [Arguments("*0\r\n")]
    [Arguments("+OK\r\n")]
    public async Task MalformedTrackingInfoFailsAsAProtocolError(string reply)
    {
        await using var server = new FakeRespServer(Integer(1), Encoding.ASCII.GetBytes(reply));
        await using var client = await RespireClient.ConnectAsync(Options(server.Port));
        var connection = await client.Server.GetClientConnectionAsync();
        await Assert.That(async () => await connection.TrackingInfoAsync()).ThrowsExactly<RespireProtocolException>();
    }

    private static RespireOptions Options(int port) => new() { Protocol = RespProtocol.Resp2, Connections = 1, AllowAdmin = true, Endpoints = [new("127.0.0.1", port)] };
    private static byte[] Integer(long value) => Encoding.ASCII.GetBytes($":{value}\r\n");
    private static byte[] Bulk(string value) => Bulk(Encoding.UTF8.GetBytes(value));
    private static byte[] Bulk(byte[] value) => [.. Encoding.ASCII.GetBytes($"${value.Length}\r\n"), .. value, 13, 10];
    private static byte[] Sequence(char kind, params byte[][] values) =>
        [.. Encoding.ASCII.GetBytes($"{kind}{(kind == '%' ? values.Length / 2 : values.Length)}\r\n"), .. values.SelectMany(value => value)];
}
