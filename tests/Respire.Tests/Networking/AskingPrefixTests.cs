using Respire.Commands;
using Respire.Internal;
using Respire.Infrastructure;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class AskingPrefixTests
{
    [Test]
    public async Task PinnedAskingDoesNotRerouteAfterWaitingForCapacity()
    {
        var full = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = 0;
        await using var server = new FakeRespServer(2, FakeRespServer.PongReply)
        {
            SuppressReply = command =>
            {
                if (command != "PING") return false;
                if (Interlocked.Increment(ref received) == 2) full.TrySetResult();
                return true;
            },
        };
        await using var multiplexer = await RespireConnectionMultiplexer.CreateAsync("127.0.0.1", server.Port,
            connectionCount: 2, options: new RespireConnectionOptions { MaxInflightCommands = 2 });
        var selected = multiplexer.GetConnection(0);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = selected.SendAsync(new RawCommand(FakeRespServer.PingFrame), deadline.Token).AsTask();
        var second = selected.SendAsync(new RawCommand(FakeRespServer.PingFrame), deadline.Token).AsTask();
        await full.Task.WaitAsync(deadline.Token);
        var pending = ClusterRouter.SendAskingAsync(selected, new Cmd1(Verbs.Get, "key"),
            deadline.Token, "GET", pinToConnection: true).AsTask();
        await Assert.That(pending.IsCompleted).IsFalse();
        var retirement = selected.RetireAsync();
        await Assert.That(async () => await pending.WaitAsync(deadline.Token)).Throws<RespireConnectionRetiredException>();
        await server.SendRawAsync("+PONG\r\n+PONG\r\n"u8.ToArray(), connectionId: 0);
        using var firstReply = await first.WaitAsync(deadline.Token);
        using var secondReply = await second.WaitAsync(deadline.Token);
        await retirement.WaitAsync(deadline.Token);
        await Assert.That(server.ReceivedCommands.Contains("ASKING")).IsFalse();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task AskingErrorPrecedesCommandReplyAndDrainsBothReplies(bool pinned, bool commandError)
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "ASKING" => "-NOPERM ASKING denied\r\n"u8.ToArray(),
                "GET key" when commandError => "-ERR command failed\r\n"u8.ToArray(),
                "GET key" => "$5\r\nvalue\r\n"u8.ToArray(),
                _ => FakeRespServer.PongReply,
            },
        };
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port);
        var command = new Cmd1(Verbs.Get, "key");
        RespireServerException? observed = null;
        try
        {
            using var reply = await ClusterRouter.SendAskingAsync(connection, command,
                CancellationToken.None, "GET", pinToConnection: pinned);
        }
        catch (RespireServerException error) { observed = error; }
        await Assert.That(observed?.Message).IsEqualTo("NOPERM ASKING denied");
        using var pong = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame));
        await Assert.That(pong.AsString()).IsEqualTo("PONG");
        await Assert.That(connection.PendingResponseCount).IsEqualTo(0);
    }
}
