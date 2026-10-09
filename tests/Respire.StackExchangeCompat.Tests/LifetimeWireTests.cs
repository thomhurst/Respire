using Respire.Tests.Networking;
using StackExchange.Redis;
using TUnit.Core;
using Assert = Xunit.Assert;

namespace Respire.StackExchangeCompat.Tests;

public class LifetimeWireTests
{
    [Test]
    public async Task BatchUsesOneOrderedPipelineEvenWithMultipleNativeConnections()
    {
        await using var server = new FakeRespServer(2, ":1\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)],
            Protocol = RespProtocol.Resp2,
            Connections = 2,
        });
        await using var connection = RespireConnectionMultiplexer.Wrap(client);
        var database = connection.GetDatabase();
        Assert.Throws<NotSupportedException>(() => database.HashGet("key", "field"));
        server.MinimumCommandsBeforeReply = 2;
        var batch = database.CreateBatch();
        var before = server.CommandsSeen;
        var set = batch.HashSetAsync("key", [new HashEntry("field", "value")]);
        var expiry = batch.KeyExpireAsync("key", TimeSpan.FromSeconds(30));
        Assert.Equal(before, server.CommandsSeen);
        Assert.False(set.IsCompleted);
        Assert.False(expiry.IsCompleted);
        batch.Execute();
        await Task.WhenAll(set, expiry).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(await expiry);
        var commands = server.ReceivedCommands.Skip(before).ToArray();
        Assert.Equal(new[] { "HSET key field value", "PEXPIRE key 30000" }, commands);
        Assert.Single(server.ReceivedConnectionIds.Skip(before).Distinct());
    }

    [Test]
    public async Task ColdIndividualCommandsPipelineInCallerOrder()
    {
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray()) { MinimumCommandsBeforeReply = 2 };
        await using var connection = RespireConnectionMultiplexer.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)],
            Protocol = RespProtocol.Resp2,
        });
        var first = connection.GetDatabase().HashSetAsync("key", [new HashEntry("field", "value")]);
        var second = connection.GetDatabase().KeyExpireAsync("key", TimeSpan.FromSeconds(30));
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new[] { "HSET key field value", "PEXPIRE key 30000" }, server.ReceivedCommands);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CloseSettlesEveryInFlightBatchTask(bool drain)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            SuppressReply = command =>
            {
                if (command.StartsWith("PEXPIRE ", StringComparison.Ordinal)) received.TrySetResult();
                return command.StartsWith("HSET ", StringComparison.Ordinal) || command.StartsWith("PEXPIRE ", StringComparison.Ordinal);
            },
        };
        var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await using var connection = RespireConnectionMultiplexer.Wrap(client, ownsClient: true);
        var batch = connection.GetDatabase().CreateBatch();
        var set = batch.HashSetAsync("key", [new HashEntry("field", "value")]);
        var expire = batch.KeyExpireAsync("key", TimeSpan.FromSeconds(30));
        batch.Execute();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var close = connection.CloseAsync(drain);
        if (drain)
        {
            Assert.False(close.IsCompleted);
            await server.SendRawAsync(":1\r\n:1\r\n"u8.ToArray());
            await set;
            Assert.True(await expire);
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => set);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => expire);
        }
        await close.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(set.IsCompleted);
        Assert.True(expire.IsCompleted);
        Assert.False(client.IsConnected);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CloseSettlesInFlightCommandAndOwnsOnlyItsClient(bool drain)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            SuppressReply = command =>
            {
                if (!command.StartsWith("HSET ", StringComparison.Ordinal)) return false;
                received.TrySetResult();
                return true;
            },
        };
        var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await using var connection = RespireConnectionMultiplexer.Wrap(client, ownsClient: true);
        var set = connection.GetDatabase().HashSetAsync("key", [new HashEntry("field", "value")]);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var close = connection.CloseAsync(drain);
        if (drain)
        {
            Assert.False(close.IsCompleted);
            await server.SendRawAsync(":1\r\n"u8.ToArray());
            await set;
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => set);
        }
        await close.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(client.IsConnected);
        await server.PeerClosed.WaitAsync(TimeSpan.FromSeconds(10));
        await connection.DisposeAsync();
    }

    [Test]
    public async Task NoRedirectAndServerOutcomesRemainObservable()
    {
        await using var server = new FakeRespServer("-MOVED 1 127.0.0.1:1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await using var connection = RespireConnectionMultiplexer.Wrap(client);
        var error = await Assert.ThrowsAsync<RespireServerException>(() => connection.GetDatabase().HashGetAsync("key", "field", CommandFlags.NoRedirect));
        Assert.Equal("MOVED", error.Code);
        Assert.Single(server.ReceivedCommands.Where(static command => command.StartsWith("HGET ", StringComparison.Ordinal)));
    }
}
