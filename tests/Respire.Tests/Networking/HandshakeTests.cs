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
    [Arguments(RespireProtocol.Resp2)]
    [Arguments(RespireProtocol.Resp3)]
    public async Task LibraryInfo_PipelinesWithAuthenticationAndSessionSetup(RespireProtocol protocol)
    {
        await using var server = new FakeRespServer(
            protocol == RespireProtocol.Resp2 ? FakeRespServer.OkReply : HelloReply,
            FakeRespServer.OkReply, FakeRespServer.OkReply, FakeRespServer.OkReply,
            FakeRespServer.OkReply, FakeRespServer.PongReply)
        {
            HandleLibraryInfo = false,
            MinimumCommandsBeforeReply = 5,
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var options = new RespireOptions
        {
            Protocol = protocol, Password = "secret", ClientName = "wire-test", Database = 1,
            LibraryName = "Respire(se-compat)",
        };
        await using var connection = await RespireConnection.ConnectAsync(
            "127.0.0.1", server.Port, options.ToConnectionOptions(), cancellationToken: timeout.Token);
        server.MinimumCommandsBeforeReply = 1;
        using var pong = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame), timeout.Token);

        await Assert.That(server.ReceivedCommands[0]).IsEqualTo(protocol == RespireProtocol.Resp2 ? "AUTH secret" : "HELLO 3 AUTH default secret");
        await Assert.That(server.ReceivedCommands[1]).IsEqualTo("CLIENT SETNAME wire-test");
        await Assert.That(server.ReceivedCommands[2]).IsEqualTo("CLIENT SETINFO LIB-NAME Respire(se-compat)");
        await Assert.That(server.ReceivedCommands[3]).IsEqualTo($"CLIENT SETINFO LIB-VER {RespireConnection.LibraryVersion}");
        await Assert.That(server.ReceivedCommands[4]).IsEqualTo("SELECT 1");
        await Assert.That(pong.AsString()).IsEqualTo("PONG");
    }

    [Test]
    [Arguments("-ERR unknown subcommand 'SETINFO'\r\n")]
    [Arguments("-NOPERM this user has no permissions to run the 'client|setinfo' command\r\n")]
    [Arguments("-ERR unsupported CLIENT command\r\n")]
    public async Task LibraryInfo_ServerErrorsAreIgnoredAndRepliesDrained(string error)
    {
        var errorReply = System.Text.Encoding.UTF8.GetBytes(error);
        await using var server = new FakeRespServer(errorReply, errorReply, FakeRespServer.PongReply)
        {
            HandleLibraryInfo = false,
        };
        await using var connection = await RespireConnection.ConnectAsync(
            "127.0.0.1", server.Port, new RespireOptions().ToConnectionOptions() with { Protocol = RespireProtocol.Resp2 });
        using var pong = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame));

        await Assert.That(server.ReceivedCommands[0]).IsEqualTo("CLIENT SETINFO LIB-NAME Respire");
        await Assert.That(pong.AsString()).IsEqualTo("PONG");
    }

    [Test]
    public async Task LibraryInfo_DoesNotHideAuthenticationFailure()
    {
        await using var server = new FakeRespServer(
            "-WRONGPASS invalid password\r\n"u8.ToArray(), FakeRespServer.OkReply, FakeRespServer.OkReply)
        {
            HandleLibraryInfo = false,
            MinimumCommandsBeforeReply = 3,
        };
        var error = await Assert.That(async () => await RespireConnection.ConnectAsync(
            "127.0.0.1", server.Port, new RespireOptions { Password = "wrong" }.ToConnectionOptions() with { Protocol = RespireProtocol.Resp2 }))
            .Throws<RespireConnectionException>();
        await Assert.That(error!.Message).Contains("AUTH failed");
    }

    [Test]
    public async Task LibraryInfo_TransportFailureStillFailsHandshake()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            HandleLibraryInfo = false,
            CloseConnectionAfterCommand = 2,
        };
        await Assert.That(async () => await RespireConnection.ConnectAsync(
            "127.0.0.1", server.Port, new RespireOptions().ToConnectionOptions() with { Protocol = RespireProtocol.Resp2 }))
            .Throws<RespireConnectionException>();
    }

    [Test]
    public async Task LibraryInfo_NullDisablesBothCommands()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply) { HandleLibraryInfo = false };
        await using var connection = await RespireConnection.ConnectAsync(
            "127.0.0.1", server.Port, new RespireOptions { LibraryName = null }.ToConnectionOptions() with { Protocol = RespireProtocol.Resp2 });
        using var pong = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame));
        await Assert.That(server.CommandsSeen).IsEqualTo(1);
        await Assert.That(pong.AsString()).IsEqualTo("PONG");
    }

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
            "127.0.0.1", server.Port, new RespireConnectionOptions { Protocol = RespireProtocol.Resp3 });

        await Assert.That(server.ReceivedCommands[0]).IsEqualTo("HELLO 3");
    }

    [Test]
    public async Task Resp3WithPassword_SendsHelloWithInlineAuth()
    {
        await using var server = new FakeRespServer(HelloReply);
        await using var connection = await RespireConnection.ConnectAsync(
            "127.0.0.1", server.Port, new RespireConnectionOptions { Protocol = RespireProtocol.Resp3, Password = "secret" });

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
                "127.0.0.1", server.Port, new RespireConnectionOptions { Protocol = RespireProtocol.Resp3 }))
            .Throws<RespireConnectionException>();
    }

    [Test]
    public async Task Resp3_WrongNegotiatedProtocol_ConnectThrows()
    {
        await using var server = new FakeRespServer("%1\r\n$5\r\nproto\r\n:2\r\n"u8.ToArray());

        await Assert.That(async () => await RespireConnection.ConnectAsync(
                "127.0.0.1", server.Port, new RespireConnectionOptions { Protocol = RespireProtocol.Resp3 }))
            .Throws<RespireConnectionException>();
    }

    [Test]
    public async Task Resp3_MissingProtocolField_ConnectThrows()
    {
        await using var server = new FakeRespServer("%1\r\n$6\r\nserver\r\n$5\r\nredis\r\n"u8.ToArray());

        await Assert.That(async () => await RespireConnection.ConnectAsync(
                "127.0.0.1", server.Port, new RespireConnectionOptions { Protocol = RespireProtocol.Resp3 }))
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
