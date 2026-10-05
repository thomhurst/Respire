using System.Text;
using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
// The moving channel uses a slot reserved for this row; slot moves are serialized cluster-wide.
[ClassDataSource<SharedRedisClusterFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(SharedRedisClusterFixture.ReshardingKey)]
public class ClusterShardedPubSubIntegrationTests(SharedRedisClusterFixture fixture)
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ThreePrimariesDeliverBinaryChannelsAndReshardWithoutReplacingSubscription(int protocol)
    {
        var cluster = fixture.Cluster;
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            UseCluster = true, Protocol = (RespProtocol)protocol, Connections = 1,
            Endpoints = { new RespireEndpoint(cluster.Host, cluster.Port(0)) },
        });
        // Hash tags keep the binary suffix in the routed channel while selecting three owners.
        // Reserved slots keep other rows' slot moves away from these channels.
        RespireChannel[] channels =
        [
            (byte[])[(byte)'{', .. Encoding.ASCII.GetBytes(fixture.ReserveSlot(node: 0)), (byte)'}', 0, 0xff],
            $"{{{fixture.ReserveSlot(node: 1)}}}:middle",
            $"{{{fixture.ReserveSlot(node: 2)}}}:moving",
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
}
