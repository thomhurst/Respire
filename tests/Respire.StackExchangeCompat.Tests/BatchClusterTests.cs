using System.Text;
using Respire.Tests.Networking;
using TUnit.Core;
using Assert = Xunit.Assert;

namespace Respire.StackExchangeCompat.Tests;

public class BatchClusterTests
{
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task CrossSlotMoveDoesNotCancelValidCommands(int movePosition)
    {
        await using var server = new FakeRespServer(4, FakeRespServer.OkReply);
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:0\r\n:16383\r\n*3\r\n$9\r\n127.0.0.1\r\n:{server.Port}\r\n$7\r\nprimary\r\n");
        server.ReplyOverride = (_, command) => command switch
        {
            "CLUSTER SLOTS" => topology,
            "HSET {a}:hash field value" => ":1\r\n"u8.ToArray(),
            "HGET {a}:hash field" => "$5\r\nvalue\r\n"u8.ToArray(),
            _ => null,
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true,
            ClusterTopologyRefreshInterval = null,
            Connections = 1,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        await using var connection = RespireConnectionMultiplexer.Wrap(client);
        var batch = connection.GetDatabase().CreateBatch();
        Task<StackExchange.Redis.RedisValue>? move = null;
        if (movePosition == 0) move = batch.ListRightPopLeftPushAsync("{a}:source", "{b}:destination");
        var write = batch.HashSetAsync("{a}:hash", "field", "value");
        if (movePosition == 1) move = batch.ListRightPopLeftPushAsync("{a}:source", "{b}:destination");
        var read = batch.HashGetAsync("{a}:hash", "field");
        if (movePosition == 2) move = batch.ListRightPopLeftPushAsync("{a}:source", "{b}:destination");
        Assert.False(write.IsCompleted);
        Assert.False(read.IsCompleted);
        Assert.False(move!.IsCompleted);

        batch.Execute();

        Assert.Equal("CROSSSLOT", (await Assert.ThrowsAsync<RespireServerException>(() => move)).Code);
        Assert.True(await write);
        Assert.Equal("value", (string?)await read);
        Assert.Equal(new[] { "HSET {a}:hash field value", "HGET {a}:hash field" },
            server.ReceivedCommands.Where(command => command.StartsWith("HSET ") || command.StartsWith("HGET ")));
        Assert.DoesNotContain(server.ReceivedCommands, command => command.StartsWith("RPOPLPUSH "));
    }
}
