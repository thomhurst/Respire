using FluentAssertions;
using Testcontainers.Redis;
using TUnit.Core;

namespace Respire.IntegrationTests;

public class ServerPubSubIntegrationTests
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task InspectLiteralPatternAndShardedSubscriptions(int protocol)
    {
        // Pub/sub is not database-isolated. Own the server so global NUMPAT assertions are exact.
        await using var container = new RedisBuilder("redis:7.0.15").Build();
        await container.StartAsync();
        var connectionString = $"redis://{container.Hostname}:{container.GetMappedPublicPort(6379)}?protocol={protocol}";
        await using var first = await RespireClient.ConnectAsync(connectionString);
        await using var second = await RespireClient.ConnectAsync(connectionString);
        byte[] bytes = [255, 0, (byte)':', (byte)'x'];
        RespireChannel channel = bytes;
        RespireChannel pattern = RespireChannel.Pattern((byte[])[255, 0, (byte)':', (byte)'*']);
        var shard = RespireChannel.Sharded(channel);
        await using var literal1 = await first.SubscribeAsync(channel);
        await using var literal2 = await second.SubscribeAsync(channel);
        await using var pattern1 = await first.SubscribeAsync(pattern);
        await using var pattern2 = await second.SubscribeAsync(pattern);
        await using var sharded = await first.SubscribeAsync(shard);
        var server = first.WithKeyPrefix("ignored:").Server;
        var channels = await server.PubSubChannelsAsync(pattern);
        channels.Should().Equal(channel);
        var counts = await server.PubSubSubscriberCountsAsync([channel, "missing"]);
        counts.Should().Equal(new RespireChannelSubscriberCount(channel, 2), new RespireChannelSubscriberCount("missing", 0));
        (await server.PubSubPatternCountAsync()).Should().Be(1);
        (await server.PubSubChannelsAsync(pattern, sharded: true)).Should().Equal(shard);
        var shardCounts = await server.PubSubSubscriberCountsAsync([channel], sharded: true);
        shardCounts.Should().Equal(new RespireChannelSubscriberCount(shard, 1));
        shardCounts[0].Channel.Kind.Should().Be(SubscriptionKind.Sharded);
        var nodes = await server.PubSubSubscriberCountsOnAllNodesAsync([channel]);
        nodes.Should().ContainSingle();
        nodes[0].IsSuccess.Should().BeTrue();
        nodes[0].Value.Should().Equal(new RespireChannelSubscriberCount(channel, 2));
        (await server.PubSubPatternCountOnAllNodesAsync())[0].Value.Should().Be(1);
        (await server.PubSubChannelsOnAllNodesAsync(pattern, sharded: true))[0].Value.Should().Equal(shard);
        await first.DisposeAsync();
        channels[0].Bytes.ToArray().Should().Equal(bytes);
        shardCounts[0].Channel.Bytes.ToArray().Should().Equal(bytes);
    }
}
