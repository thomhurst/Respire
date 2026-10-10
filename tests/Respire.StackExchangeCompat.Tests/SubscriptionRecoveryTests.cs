using Respire.Tests.Networking;
using StackExchange.Redis;
using TUnit.Core;
using Assert = Xunit.Assert;

namespace Respire.StackExchangeCompat.Tests;

public class SubscriptionRecoveryTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SubscribeAfterReconnectExhaustionDoesNotReuseCompletedRegistration(bool differentHandler)
    {
        var recovering = 0;
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) =>
            {
                if (command != "SUBSCRIBE ch") return null;
                return Volatile.Read(ref recovering) == 0
                    ? "*3\r\n$9\r\nsubscribe\r\n$2\r\nch\r\n:1\r\n"u8.ToArray()
                    : "-NOPERM recovery rejected\r\n"u8.ToArray();
            },
        };
        await using var native = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)],
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            ReconnectPolicy = new() { InitialDelay = TimeSpan.Zero, JitterRatio = 0, MaxAttempts = 1 },
        });
        await using var connection = RespireConnectionMultiplexer.Wrap(native);
        var exhausted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        native.ConnectionStateChanged += change =>
        {
            if (change.ReconnectSource == RespireReconnectSource.PubSub && change.ReconnectExhausted)
                exhausted.TrySetResult();
        };
        var subscriber = connection.GetSubscriber();
        RedisChannel channel = RedisChannel.Literal("ch");
        Action<RedisChannel, RedisValue> handler = (_, _) => { };
        await subscriber.SubscribeAsync(channel, handler);

        Volatile.Write(ref recovering, 1);
        var subscribeIndex = server.ReceivedCommands.ToList().IndexOf("SUBSCRIBE ch");
        server.CloseConnection(server.ReceivedConnectionIds[subscribeIndex]);
        await exhausted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // A reachable server does not reset the native client's exhausted reconnect budget.
        Volatile.Write(ref recovering, 0);
        Action<RedisChannel, RedisValue> nextHandler = differentHandler ? (_, _) => { } : handler;
        await Assert.ThrowsAsync<RespireReconnectLimitException>(() => subscriber.SubscribeAsync(channel, nextHandler));
        await Assert.ThrowsAsync<RespireReconnectLimitException>(() => subscriber.SubscribeAsync(channel, handler));
        Assert.Equal(2, server.ReceivedCommands.Count(command => command == "SUBSCRIBE ch"));
    }
}
