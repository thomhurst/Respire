using Respire.Tests.Networking;
using TUnit.Core;
using Assert = Xunit.Assert;

namespace Respire.StackExchangeCompat.Tests;

public class WriteRetryWireTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    public async Task LostIncrementReplyDoesNotReplayAdapterWrite(bool enableReplicaRedirects, bool batch)
    {
        var effects = 0;
        var nativeAttempts = 0;
        await using var server = new FakeRespServer(4, FakeRespServer.OkReply);
        server.ReplyOverride = (_, command) =>
        {
            if (command == "INCR native")
            {
                return Interlocked.Increment(ref nativeAttempts) == 1
                    ? "-TRYAGAIN busy\r\n"u8.ToArray() : ":2\r\n"u8.ToArray();
            }

            if (command != "INCR adapter")
            {
                return null;
            }

            if (Interlocked.Increment(ref effects) == 1)
            {
                server.CloseConnections();
            }

            return ":2\r\n"u8.ToArray();
        };

        await using var client = RespireClient.Create(Options(server) with { EnableReplicaRedirects = enableReplicaRedirects });
        await using var connection = RespireConnectionMultiplexer.Wrap(client);
        var database = connection.GetDatabase();
        Task<long> increment;

        if (batch)
        {
            var queue = database.CreateBatch();
            increment = queue.StringIncrementAsync("adapter");
            queue.Execute();
        }
        else
        {
            increment = database.StringIncrementAsync("adapter");
        }

        await Assert.ThrowsAnyAsync<RespireConnectionException>(() => increment);
        Assert.Equal(1, effects);

        using var result = await client.ExecuteAsync(RespireCommands.String.INCR, ["native"]);
        Assert.Equal(2, result.AsInteger());
        Assert.Equal(2, nativeAttempts);
    }

    [Test]
    public async Task LostBatchRepliesDoNotReplayIncrements()
    {
        var effects = 0;
        await using var server = new FakeRespServer(4, FakeRespServer.OkReply)
        {
            MinimumCommandsBeforeReply = 2,
        };
        server.ReplyOverride = (connectionId, command) =>
        {
            if (command != "INCR adapter")
            {
                return null;
            }

            var effect = Interlocked.Increment(ref effects);

            if (effect == 2)
            {
                server.CloseConnection(connectionId);
            }

            return effect == 1 ? ":1\r\n"u8.ToArray() : ":2\r\n"u8.ToArray();
        };
        await using var connection = RespireConnectionMultiplexer.Create(Options(server));
        var batch = connection.GetDatabase().CreateBatch();
        var first = batch.StringIncrementAsync("adapter");
        var second = batch.StringIncrementAsync("adapter");

        batch.Execute();

        await Assert.ThrowsAnyAsync<RespireConnectionException>(() => second);
        await Assert.ThrowsAnyAsync<RespireConnectionException>(() => first);
        Assert.Equal(2, effects);
    }

    [Test]
    public async Task LostExecReplyDoesNotReplayTransactionIncrement()
    {
        var effects = 0;
        await using var server = new FakeRespServer(4, FakeRespServer.OkReply);
        server.ReplyOverride = (connectionId, command) =>
        {
            if (command == "INCR adapter")
            {
                return "+QUEUED\r\n"u8.ToArray();
            }

            if (command != "EXEC")
            {
                return null;
            }

            if (Interlocked.Increment(ref effects) == 1)
            {
                server.CloseConnection(connectionId);
            }

            return "*1\r\n:2\r\n"u8.ToArray();
        };
        await using var connection = RespireConnectionMultiplexer.Create(Options(server));
        var transaction = connection.GetDatabase().CreateTransaction();
        var increment = transaction.StringIncrementAsync("adapter");

        await Assert.ThrowsAnyAsync<RespireConnectionException>(() => transaction.ExecuteAsync());
        await Assert.ThrowsAnyAsync<RespireConnectionException>(() => increment);
        Assert.Equal(1, effects);
        Assert.Single(server.ReceivedCommands, command => command == "INCR adapter");
    }

    [Test]
    public async Task TryAgainDoesNotReplayHashWriteAfterQueuedExpiry()
    {
        var expiryReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hashWrites = 0;
        var reads = 0;
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            SuppressReply = command =>
            {
                if (command == "HSET key field value")
                {
                    return Interlocked.Increment(ref hashWrites) == 1;
                }

                if (command == "PEXPIRE key 60001")
                {
                    expiryReceived.TrySetResult();
                    return true;
                }

                return false;
            },
            ReplyOverride = (_, command) => command switch
            {
                "HSET key field value" => ":1\r\n"u8.ToArray(),
                "HGET key field" => Interlocked.Increment(ref reads) == 1
                    ? "-TRYAGAIN busy\r\n"u8.ToArray() : "$-1\r\n"u8.ToArray(),
                _ => null,
            },
        };
        await using var connection = RespireConnectionMultiplexer.Create(Options(server));
        var database = connection.GetDatabase();

        var write = database.HashSetAsync("key", "field", "value");
        var expiry = database.KeyExpireAsync("key", TimeSpan.FromMilliseconds(60001));
        await expiryReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await server.SendRawAsync("-TRYAGAIN busy\r\n:0\r\n"u8.ToArray());

        Assert.False(await expiry);
        var error = await Assert.ThrowsAsync<RespireServerException>(() => write);
        Assert.Equal("TRYAGAIN", error.Code);
        Assert.Equal(1, hashWrites);

        Assert.True((await database.HashGetAsync("key", "field")).IsNull);
        Assert.Equal(2, reads);
    }

    private static RespireOptions Options(FakeRespServer server)
    {
        return new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)],
            Protocol = RespireProtocol.Resp2,
            CommandTimeout = TimeSpan.FromSeconds(10),
            RetryPolicy = new()
            {
                MaxCommandRetryCategory = RespireCommandRetryCategory.WriteAccumulating,
                InitialBackoff = TimeSpan.Zero,
            },
        };
    }
}
