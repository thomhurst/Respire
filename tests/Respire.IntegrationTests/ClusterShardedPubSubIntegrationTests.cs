using System.Text;
using FluentAssertions;
using Respire.Internal;
using TUnit.Core;

namespace Respire.IntegrationTests;

public class ClusterShardedPubSubIntegrationTests
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ThreePrimariesDeliverBinaryChannelsAndReshardWithoutReplacingSubscription(int protocol)
    {
        await using var cluster = await RedisClusterTestContainer.StartAsync();
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            UseCluster = true, Protocol = (RespProtocol)protocol, Connections = 1,
            Endpoints = { new RespireEndpoint(cluster.Host, cluster.Port(0)) },
        });
        // Hash tags keep the binary suffix in the routed channel while selecting three owners.
        RespireChannel[] channels =
        [
            new byte[] { (byte)'{', (byte)'b', (byte)'a', (byte)'r', (byte)'}', 0, 0xff },
            $"{{{TagForSlot(6000)}}}:middle",
            "{foo}:moving",
        ];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var subscription = await client.SubscribeShardedAsync(channels, deadline.Token);
        await using var reader = subscription.GetAsyncEnumerator(deadline.Token);
        await PublishAndReadAll("before");

        var slot = channels[2].ClusterSlot;
        await cluster.MoveKeysAsync(slot, source: 2, target: 0);
        await cluster.FinishMoveAsync(slot, target: 0);
        // Redis itself sends SUNSUBSCRIBE when ownership moves. No application command or
        // manual topology refresh is needed to wake the subscription recovery loop.
        (await reader.MoveNextAsync()).Should().BeTrue();
        reader.Current.Kind.Should().Be(RespireMessageKind.Gap);
        reader.Current.Gap!.Reason.Should().Be(RespireSubscriptionGapReason.Reconnect);
        await PublishAndReadAll("after");
        subscription.IsDisposed.Should().BeFalse();
        await subscription.DisposeAsync();
        (await subscription.Completion).Should().Be(RespireSubscriptionEndReason.Disposed);
        foreach (var channel in channels) (await client.PublishShardedAsync(channel, "unsubscribed", deadline.Token)).Should().Be(0);

        async Task PublishAndReadAll(string text)
        {
            foreach (var channel in channels)
                (await client.PublishShardedAsync(channel, Encoding.UTF8.GetBytes(text), deadline.Token)).Should().Be(1);
            var received = new HashSet<RespireChannel>();
            for (var i = 0; i < channels.Length; i++)
            {
                (await reader.MoveNextAsync()).Should().BeTrue();
                reader.Current.Kind.Should().Be(RespireMessageKind.Message);
                reader.Current.Text.Should().Be(text);
                received.Add(reader.Current.Channel);
            }
            received.Should().BeEquivalentTo(channels);
        }
    }

    private static string TagForSlot(int slot)
    {
        for (var index = 0; ; index++)
        {
            var tag = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (ClusterHash.GetSlot(tag) == slot) return tag;
        }
    }
}
