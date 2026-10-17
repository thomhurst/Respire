using System.Text;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ReplicaRedirectTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CapabilityPipelinesAfterAuthAndIgnoresUnsupportedServers(bool supported)
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply,
            supported ? FakeRespServer.OkReply : Bytes("-ERR unknown subcommand 'CAPA'\r\n"), FakeRespServer.PongReply)
        { MinimumCommandsBeforeReply = 2 };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new RespireConnectionOptions { Password = "secret", AdvertiseReplicaRedirects = true }, cancellationToken: timeout.Token);
        server.MinimumCommandsBeforeReply = 1;
        using var pong = await connection.SendAsync(new Respire.Commands.RawCommand(FakeRespServer.PingFrame), timeout.Token);
        await Assert.That(pong.AsString()).IsEqualTo("PONG");
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(["AUTH secret", "CLIENT CAPA REDIRECT", "PING"]);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReadOnlyFollowsOnlySuccessfulCapability(bool supported)
    {
        await using var server = new FakeRespServer(
            supported ? FakeRespServer.OkReply : Bytes("-NOPERM unavailable\r\n"), FakeRespServer.OkReply);
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new RespireConnectionOptions { AdvertiseReplicaRedirects = true, ReplicaRedirectReadOnly = true });
        string[] expected = supported ? ["CLIENT CAPA REDIRECT", "READONLY"] : ["CLIENT CAPA REDIRECT"];
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(expected);
    }

    [Test]
    [Arguments("127.0.0.1:6380", "127.0.0.1", 6380)]
    [Arguments("primary.example:6380", "primary.example", 6380)]
    [Arguments("[::1]:6380", "::1", 6380)]
    [Arguments("2001:db8::1:6380", "2001:db8::1", 6380)]
    [Arguments(":6380", "source", 6380)]
    public async Task RedirectParsesEndpoint(string address, string host, int port)
    {
        await Assert.That(RespireClient.TryParseReplicaRedirect("REDIRECT " + address, "source", out var endpoint)).IsTrue();
        await Assert.That(endpoint).IsEqualTo(new RespireEndpoint(host, port));
    }

    [Test]
    [Arguments("REDIRECT bad")]
    [Arguments("REDIRECT host:0")]
    [Arguments("REDIRECT host:65536")]
    [Arguments("REDIRECT ?:6379")]
    [Arguments("REDIRECT host:6379 extra")]
    [Arguments("REDIRECT host:-1")]
    [Arguments("MOVED 0 host:6379")]
    public async Task MalformedRedirectDoesNotConnect(string message)
        => await Assert.That(RespireClient.TryParseReplicaRedirect(message, "source", out _)).IsFalse();

    [Test]
    public async Task KeylessWriteAndTypedReadFollowRedirectWithDatabaseAndCredentials()
    {
        await using var primary = new FakeRespServer(2)
        {
            ReplyOverride = (_, command) => command == "RANDOMKEY" ? Bytes("$3\r\nkey\r\n") : FakeRespServer.OkReply,
        };
        await using var replica = new FakeRespServer
        {
            ReplyOverride = (_, command) => command is "FLUSHDB" or "RANDOMKEY"
                ? Bytes($"-REDIRECT 127.0.0.1:{primary.Port}\r\n") : FakeRespServer.OkReply,
        };
        await using var client = await RespireClient.ConnectAsync(Options(replica.Port) with
        { Database = 2, Password = "secret", AllowAdmin = true });
        await client.Server.FlushDatabaseAsync();
        using var key = await client.ExecuteAsync(RespireCommands.Key.RANDOMKEY);
        await Assert.That(key.AsString()).IsEqualTo("key");
        await Assert.That(primary.ReceivedCommands.Count(command => command == "FLUSHDB")).IsEqualTo(1);
        await Assert.That(primary.ReceivedCommands.Count(command => command == "RANDOMKEY")).IsEqualTo(1);
        await Assert.That(primary.ReceivedCommands.Count(command => command == "AUTH secret")).IsEqualTo(2);
        await Assert.That(primary.ReceivedCommands.Count(command => command == "SELECT 2")).IsEqualTo(2);
    }

    [Test]
    public async Task UnsupportedCapabilityKeepsNormalTypedReplyPath()
    {
        await using var server = new FakeRespServer
        {
            ReplyOverride = (_, command) => command == "CLIENT CAPA REDIRECT"
                ? Bytes("-ERR unknown subcommand 'CAPA'\r\n") : Bytes("$5\r\nvalue\r\n"),
        };
        await using var client = await RespireClient.ConnectAsync(Options(server.Port));
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(["CLIENT CAPA REDIRECT", "GET key"]);
    }

    [Test]
    public async Task ReplicaReadPolicyUsesReadOnlyAndKeepsReadOnReplica()
    {
        await using var primary = new FakeRespServer(FakeRespServer.OkReply);
        await using var replica = new FakeRespServer
        {
            ReplyOverride = (_, command) => command switch
            {
                "ROLE" => Bytes("*5\r\n+slave\r\n+127.0.0.1\r\n:6379\r\n+connected\r\n:0\r\n"),
                "GET key" => Bytes("$7\r\nreplica\r\n"),
                _ => FakeRespServer.OkReply,
            },
        };
        await using var client = RespireClient.Create(Options(primary.Port) with
        { ReplicaEndpoints = [new("127.0.0.1", replica.Port)], ReadFrom = RespireReadFrom.Replica });
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("replica");
        await Assert.That(replica.ReceivedCommands).IsEquivalentTo(["CLIENT CAPA REDIRECT", "READONLY", "ROLE", "GET key"]);
        await Assert.That(primary.CommandsSeen).IsEqualTo(0);
    }

    [Test]
    public async Task SentinelGenerationFollowsRedirectAfterPrimaryBecomesReplica()
    {
        await using var promoted = new FakeRespServer(FakeRespServer.OkReply);
        await using var previous = new FakeRespServer
        {
            ReplyOverride = (_, command) => command switch
            {
                "ROLE" => Bytes("*3\r\n+master\r\n:0\r\n*0\r\n"),
                "FLUSHDB" => Bytes($"-REDIRECT 127.0.0.1:{promoted.Port}\r\n"),
                _ => FakeRespServer.OkReply,
            },
        };
        await using var sentinel = new FakeRespServer(64)
        {
            ReplyOverride = (_, command) => command switch
            {
                "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster" => Bytes($"*2\r\n+127.0.0.1\r\n+{previous.Port}\r\n"),
                "SENTINEL MASTER mymaster" => Bytes($"*8\r\n+ip\r\n+127.0.0.1\r\n+port\r\n+{previous.Port}\r\n+config-epoch\r\n+1\r\n+flags\r\n+master\r\n"),
                "SUBSCRIBE +switch-master +sdown +odown" => Bytes("*3\r\n+subscribe\r\n++switch-master\r\n:1\r\n*3\r\n+subscribe\r\n++sdown\r\n:2\r\n*3\r\n+subscribe\r\n++odown\r\n:3\r\n"),
                _ => Bytes("*0\r\n"),
            },
        };
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port) with
        { SentinelPrimaryName = "mymaster", AllowAdmin = true });
        await client.Server.FlushDatabaseAsync();
        await Assert.That(promoted.ReceivedCommands).IsEquivalentTo(["CLIENT CAPA REDIRECT", "FLUSHDB"]);
        await Assert.That(previous.ReceivedCommands.Contains("CLIENT CAPA REDIRECT")).IsTrue();
        await Assert.That(sentinel.ReceivedCommands.Contains("CLIENT CAPA REDIRECT")).IsFalse();
    }

    [Test]
    public async Task RedirectLoopsAreBounded()
    {
        var port = 0;
        await using var server = new FakeRespServer(20)
        {
            ReplyOverride = (_, command) => command == "CLIENT CAPA REDIRECT" ? FakeRespServer.OkReply
                : Bytes($"-REDIRECT 127.0.0.1:{port}\r\n"),
        };
        port = server.Port;
        await using var client = await RespireClient.ConnectAsync(Options(port));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.That(async () => await client.GetStringAsync("key", timeout.Token)).Throws<RespireServerException>();
        await Assert.That(server.ReceivedCommands.Count(command => command == "GET key"))
            .IsEqualTo(Respire.Internal.ClusterRouter.RedirectLimit + 1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TargetHandshakeSharesCommandDeadlineAndCallerCancellation(bool cancelCaller)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var target = new FakeRespServer
        {
            SuppressReply = _ => { entered.TrySetResult(); return true; },
        };
        await using var replica = new FakeRespServer
        {
            ReplyOverride = (_, command) => command == "CLIENT CAPA REDIRECT" ? FakeRespServer.OkReply
                : Bytes($"-REDIRECT 127.0.0.1:{target.Port}\r\n"),
        };
        using var caller = new CancellationTokenSource();
        await using var client = await RespireClient.ConnectAsync(Options(replica.Port) with
        { CommandTimeout = TimeSpan.FromMilliseconds(cancelCaller ? 5000 : 300) });
        var pending = client.GetStringAsync("key", caller.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (cancelCaller)
        {
            caller.Cancel();
            var error = await Assert.That(async () => await pending).Throws<OperationCanceledException>();
            await Assert.That(error!.CancellationToken).IsEqualTo(caller.Token);
        }
        else
            await Assert.That(async () => await pending).Throws<RespireTimeoutException>();
        await target.PeerClosed.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(target.ReceivedCommands).IsEquivalentTo(["CLIENT CAPA REDIRECT"]);
    }

    private static RespireOptions Options(int port) => new()
    {
        Protocol = RespireProtocol.Resp2, Connections = 1, EnableReplicaRedirects = true,
        Endpoints = [new("127.0.0.1", port)],
    };

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
}
