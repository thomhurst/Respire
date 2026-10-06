using FluentAssertions;
using Respire.Internal;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
[ClassDataSource<SharedRedisClusterFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(SharedRedisClusterFixture.ReshardingKey)]
public class ClusterReadyReplyIntegrationTests(SharedRedisClusterFixture fixture)
{
    [Test]
    [Arguments(RespProtocol.Resp2, false, "string")]
    [Arguments(RespProtocol.Resp2, false, "bytes")]
    [Arguments(RespProtocol.Resp2, false, "integer")]
    [Arguments(RespProtocol.Resp2, true, "string")]
    [Arguments(RespProtocol.Resp2, true, "bytes")]
    [Arguments(RespProtocol.Resp2, true, "integer")]
    [Arguments(RespProtocol.Resp3, false, "string")]
    [Arguments(RespProtocol.Resp3, false, "bytes")]
    [Arguments(RespProtocol.Resp3, false, "integer")]
    [Arguments(RespProtocol.Resp3, true, "string")]
    [Arguments(RespProtocol.Resp3, true, "bytes")]
    [Arguments(RespProtocol.Resp3, true, "integer")]
    public async Task ReadyTypedReplySurvivesSlotMigration(RespProtocol protocol, bool asking, string shape)
    {
        var cluster = fixture.Cluster;
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            UseCluster = true, Protocol = protocol, Connections = 1,
            ClusterTopologyRefreshInterval = null,
            Endpoints = [new(cluster.Host, cluster.Port(0))],
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var key = $"ready:{{{fixture.ReserveSlot(0)}}}";
        var slot = ClusterHash.GetSlot(key);
        (await client.Strings.SetAsync(key, "payload", cancellationToken: timeout.Token)).Should().BeTrue();
        (await client.Strings.GetStringAsync(key, timeout.Token)).Should().Be("payload");
        await cluster.MoveKeysAsync(slot, source: 0, target: 1);
        if (!asking) await cluster.FinishMoveAsync(slot, target: 1);

        switch (shape)
        {
            case "string":
                (await client.Strings.GetStringAsync(key, timeout.Token)).Should().Be("payload");
                break;
            case "bytes":
                (await client.Strings.GetBytesAsync(key, timeout.Token)).Should().Equal("payload"u8.ToArray());
                break;
            case "integer":
                (await client.Strings.LengthAsync(key, timeout.Token)).Should().Be(7);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(shape));
        }
        client.Core.Cluster!.GetSlotOwnerEndpoint(slot)!.Value.Port.Should().Be(cluster.Port(asking ? 0 : 1));
    }
}
