using System.Text;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ProtocolNegotiationTests
{
    private static readonly byte[] HelloReply = "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray();

    [Test]
    public async Task DefaultClientNegotiatesResp3BeforeApplicationCommands()
    {
        await using var server = new FakeRespServer(HelloReply, FakeRespServer.PongReply);
        await using var client = await RespireClient.ConnectAsync(Options(server));
        await client.PingAsync();
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(["HELLO 3", "PING"], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("-ERR unknown command 'HELLO', with args beginning with: '3'\r\n")]
    [Arguments("-ERR unknown command `HELLO`\r\n")]
    [Arguments("-ERR unknown command 'hello', with args beginning with: '3'\r\n")]
    [Arguments("-ERR unknown command \"hello\"\r\n")]
    [Arguments("-NOPROTO\r\n")]
    [Arguments("-NOPROTO unsupported protocol version\r\n")]
    public async Task UnsupportedHelloFallsBackBeforeAuthenticationAndSetup(string rejection)
    {
        await using var server = new FakeRespServer(Encoding.ASCII.GetBytes(rejection),
            FakeRespServer.OkReply, FakeRespServer.OkReply, FakeRespServer.OkReply, FakeRespServer.PongReply);
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        {
            Username = "user", Password = "secret", Database = 2, ClientName = "fallback",
        });
        await client.PingAsync();
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(
            ["HELLO 3 AUTH user secret", "AUTH user secret", "CLIENT SETNAME fallback", "SELECT 2", "PING"],
            CollectionOrdering.Matching);
        await Assert.That(server.ReceivedConnectionIds.All(id => id == 0)).IsTrue();
    }

    [Test]
    public async Task SuccessfulInlineAuthenticationIsNotRepeated()
    {
        await using var server = new FakeRespServer(HelloReply, FakeRespServer.OkReply, FakeRespServer.PongReply);
        await using var client = await RespireClient.ConnectAsync(Options(server) with { Password = "secret", Database = 2 });
        await client.PingAsync();
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(
            ["HELLO 3 AUTH default secret", "SELECT 2", "PING"], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("-WRONGPASS invalid username-password pair\r\n")]
    [Arguments("-NOAUTH Authentication required.\r\n")]
    [Arguments("-NOPERM this user has no permissions to run the 'hello' command\r\n")]
    [Arguments("-ERR syntax error\r\n")]
    [Arguments("-ERR unknown command 'other', with args beginning with: 'hello'\r\n")]
    [Arguments("-ERR unknown command 'HELLOOTHER'\r\n")]
    [Arguments("-LOADING Redis is loading the dataset in memory\r\n")]
    [Arguments("%1\r\n$5\r\nproto\r\n:2\r\n")]
    [Arguments("+OK\r\n")]
    [Arguments("!malformed\r\n")]
    public async Task OtherFailuresNeverDowngradeOrSendLaterSetup(string reply)
    {
        await using var server = new FakeRespServer(Encoding.ASCII.GetBytes(reply));
        await Assert.That(async () => await RespireClient.ConnectAsync(Options(server) with
        {
            Password = "secret", Database = 2, ClientName = "not initialized",
        })).Throws<RespireException>();
        await server.PeerClosed.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(["HELLO 3 AUTH default secret"], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("ERR proxy does not implement this operation", RespProtocol.Auto, false, true)]
    [Arguments("ERR unknown command HELLO", RespProtocol.Auto, false, true)]
    [Arguments("ERR authentication failed", RespProtocol.Auto, false, false)]
    [Arguments("ERR invalid password", RespProtocol.Auto, false, false)]
    [Arguments("ERR ACL denied", RespProtocol.Auto, false, false)]
    [Arguments("WRONGPASS invalid username-password pair", RespProtocol.Auto, false, false)]
    [Arguments("NOAUTH Authentication required", RespProtocol.Auto, false, false)]
    [Arguments("NOPERM denied", RespProtocol.Auto, false, false)]
    [Arguments("ERR proxy does not implement this operation", RespProtocol.Resp3, false, false)]
    [Arguments("ERR proxy does not implement this operation", RespProtocol.Auto, true, false)]
    public async Task CompatibilityHintIsLimitedToUnclassifiedAutomaticHelloErrors(
        string error, RespProtocol protocol, bool tracking, bool expectHint)
    {
        await using var server = new FakeRespServer(Encoding.ASCII.GetBytes("-" + error + "\r\n"));
        RespireConnectionException? failure = null;
        try
        {
            await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
                new RespireConnectionOptions { Protocol = protocol, EnableClientTracking = tracking });
        }
        catch (RespireConnectionException exception) { failure = exception; }
        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.Message).Contains($"HELLO failed for 127.0.0.1:{server.Port}: {error}");
        await Assert.That(failure.Message.Contains("protocol=2", StringComparison.Ordinal)).IsEqualTo(expectHint);
        await Assert.That(failure.InnerException).IsTypeOf<RespireServerException>();
        await Assert.That(failure.InnerException!.Message).Contains(error);
        await Assert.That(server.ReceivedCommands[0]).IsEqualTo("HELLO 3");
        if (!tracking)
            await Assert.That(server.ReceivedCommands).IsEquivalentTo(["HELLO 3"], CollectionOrdering.Matching);
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("AUTH "))).IsFalse();
    }

    [Test]
    public async Task ExplicitResp2DoesNotSendHello()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        await using var client = await RespireClient.ConnectAsync(Options(server) with { Protocol = RespProtocol.Resp2 });
        await client.PingAsync();
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(["PING"], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ExplicitResp3AndCachingDoNotDowngrade(bool caching)
    {
        await using var server = new FakeRespServer("-NOPROTO unsupported protocol version\r\n"u8.ToArray());
        await Assert.That(async () => await RespireClient.ConnectAsync(Options(server) with
        {
            Protocol = caching ? RespProtocol.Auto : RespProtocol.Resp3,
            ClientSideCache = caching ? new() : null,
        })).Throws<RespireConnectionException>();
        await Assert.That(server.ReceivedCommands[0]).IsEqualTo("HELLO 3");
        await Assert.That(server.ReceivedCommands.All(command => !command.StartsWith("AUTH"))).IsTrue();
    }

    [Test]
    public async Task TrackingHandshakeRemainsStrictWithFallbackRequested()
    {
        await using var server = new FakeRespServer("-NOPROTO unsupported protocol version\r\n"u8.ToArray(),
            FakeRespServer.OkReply);
        await Assert.That(async () => await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new RespireConnectionOptions { Protocol = RespProtocol.Auto, EnableClientTracking = true }))
            .Throws<RespireConnectionException>();
        await Assert.That(server.ReceivedCommands[0]).IsEqualTo("HELLO 3");
        await Assert.That(server.ReceivedCommands.All(command => !command.StartsWith("AUTH"))).IsTrue();
    }

    [Test]
    public async Task CancellationDuringNegotiationKeepsCallerTokenAndClosesConnection()
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(FakeRespServer.PongReply)
        {
            SuppressReply = command => { received.TrySetResult(); return true; },
        };
        using var cancellation = new CancellationTokenSource();
        var connecting = RespireClient.ConnectAsync(Options(server), cancellation.Token).AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var error = await Assert.That(async () => await connecting).Throws<OperationCanceledException>();
        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        await server.PeerClosed.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(["HELLO 3"], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TimeoutAndDisconnectDoNotFallBack(bool disconnect)
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply)
        {
            CloseConnectionAfterCommand = disconnect ? 1 : null,
            SuppressReply = _ => true,
        };
        await Assert.That(async () => await RespireClient.ConnectAsync(Options(server) with
        {
            CommandTimeout = TimeSpan.FromMilliseconds(500),
        })).Throws<RespireException>();
        if (!disconnect) await server.PeerClosed.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(["HELLO 3"], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("localhost")]
    [Arguments("localhost,protocol=auto")]
    [Arguments("redis://localhost")]
    [Arguments("redis://localhost?protocol=auto")]
    public async Task ConnectionStringsPreferResp3(string text)
    {
        var options = RespireOptions.Parse(text);
        await Assert.That(options.Protocol).IsEqualTo(RespProtocol.Auto);
        await Assert.That(options.ToConnectionOptions().Protocol).IsEqualTo(RespProtocol.Auto);
    }

    private static RespireOptions Options(FakeRespServer server) => new()
    {
        Endpoints = [new("127.0.0.1", server.Port)], Connections = 1,
    };
}
