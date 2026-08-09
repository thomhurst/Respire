using Respire;
using Respire.Commands;
using Respire.Networking;
using TUnit.Core;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Respire.Tests.Networking;

/// <summary>
/// Handshake wire tests: AUTH (RESP2), HELLO 3 (RESP3), CLIENT SETNAME, and failure handling.
/// </summary>
public class HandshakeTests
{
    private static readonly byte[] HelloReply = "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray();

    [Test]
    public async Task PasswordOnly_SendsAuthBeforeAnythingElse()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply, FakeRespServer.PongReply);
        await using var connection = await RespireConnection.ConnectAsync(
            "127.0.0.1", server.Port, new RespireConnectionOptions { Password = "secret" });

        var pong = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame));

        await Assert.That(pong.AsString()).IsEqualTo("PONG");
        await Assert.That(server.ReceivedCommands[0]).IsEqualTo("AUTH secret");
        await Assert.That(server.ReceivedCommands[1]).IsEqualTo("PING");
        pong.Dispose();
    }

    [Test]
    public async Task UsernameAndPassword_SendsAclAuth()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var connection = await RespireConnection.ConnectAsync(
            "127.0.0.1", server.Port, new RespireConnectionOptions { Username = "admin", Password = "secret" });

        await Assert.That(server.ReceivedCommands[0]).IsEqualTo("AUTH admin secret");
    }

    [Test]
    public async Task Resp3_SendsHello3()
    {
        await using var server = new FakeRespServer(HelloReply);
        await using var connection = await RespireConnection.ConnectAsync(
            "127.0.0.1", server.Port, new RespireConnectionOptions { UseResp3 = true });

        await Assert.That(server.ReceivedCommands[0]).IsEqualTo("HELLO 3");
    }

    [Test]
    public async Task Resp3WithPassword_SendsHelloWithInlineAuth()
    {
        await using var server = new FakeRespServer(HelloReply);
        await using var connection = await RespireConnection.ConnectAsync(
            "127.0.0.1", server.Port, new RespireConnectionOptions { UseResp3 = true, Password = "secret" });

        await Assert.That(server.ReceivedCommands[0]).IsEqualTo("HELLO 3 AUTH default secret");
    }

    [Test]
    public async Task ClientName_SendsSetNameDuringHandshake()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var connection = await RespireConnection.ConnectAsync(
            "127.0.0.1", server.Port, new RespireConnectionOptions { ClientName = "respire-tests" });

        await Assert.That(server.ReceivedCommands[0]).IsEqualTo("CLIENT SETNAME respire-tests");
    }

    [Test]
    public async Task AuthAndClientName_ArePipelinedBeforeFirstReply()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply, FakeRespServer.OkReply)
        {
            MinimumCommandsBeforeReply = 2,
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await using var connection = await RespireConnection.ConnectAsync(
            "127.0.0.1", server.Port,
            new RespireConnectionOptions { Password = "secret", ClientName = "respire-tests" },
            cancellationToken: timeout.Token);

        await Assert.That(server.ReceivedCommands[0]).IsEqualTo("AUTH secret");
        await Assert.That(server.ReceivedCommands[1]).IsEqualTo("CLIENT SETNAME respire-tests");
    }

    [Test]
    public async Task AuthRejected_ConnectThrowsAndNothingElseIsSent()
    {
        await using var server = new FakeRespServer("-ERR invalid password\r\n"u8.ToArray());

        await Assert.That(async () => await RespireConnection.ConnectAsync(
                "127.0.0.1", server.Port, new RespireConnectionOptions { Password = "wrong" }))
            .Throws<RespireConnectionException>();

        await Assert.That(server.CommandsSeen).IsEqualTo(1);
    }

    [Test]
    public async Task AuthRejected_LaterProtocolFaultDoesNotReplaceHandshakeError()
    {
        await using var server = new FakeRespServer(
            "-ERR invalid password\r\n"u8.ToArray(),
            "!malformed\r\n"u8.ToArray())
        {
            MinimumCommandsBeforeReply = 2,
        };

        RespireConnectionException? failure = null;
        try
        {
            await using var _ = await RespireConnection.ConnectAsync(
                "127.0.0.1",
                server.Port,
                new RespireConnectionOptions { Password = "wrong", ClientName = "respire-tests" });
        }
        catch (RespireConnectionException ex)
        {
            failure = ex;
        }

        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.Message).Contains("AUTH failed");
        await Assert.That(failure.Message).Contains("invalid password");
    }

    [Test]
    public async Task HelloRejected_ConnectThrows()
    {
        await using var server = new FakeRespServer("-ERR unknown command 'HELLO'\r\n"u8.ToArray());

        await Assert.That(async () => await RespireConnection.ConnectAsync(
                "127.0.0.1", server.Port, new RespireConnectionOptions { UseResp3 = true }))
            .Throws<RespireConnectionException>();
    }

    [Test]
    public async Task NoOptions_NoHandshakeCommandsSent()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port);

        var pong = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame));
        pong.Dispose();

        await Assert.That(server.ReceivedCommands[0]).IsEqualTo("PING");
        await Assert.That(server.CommandsSeen).IsEqualTo(1);
    }
}
