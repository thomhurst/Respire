using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
[ClassDataSource<SharedRedisClusterFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(SharedRedisClusterFixture.ReshardingKey)]
public class ClusterMultiKeyIntegrationTests(SharedRedisClusterFixture fixture)
{
    [Test]
    [Arguments(RespireProtocol.Resp2)]
    [Arguments(RespireProtocol.Resp3)]
    public async Task CrossSlotCommandsPreserveRedisResults(RespireProtocol protocol)
    {
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            UseCluster = true, SplitCrossSlotCommands = true, Protocol = protocol, Connections = 1,
            Endpoints = [new(fixture.Cluster.Host, fixture.Cluster.Port(0))],
        });
        RespireKey a = $"{{{fixture.ReserveSlot(0)}}}:a";
        RespireKey b = $"{{{fixture.ReserveSlot(1)}}}:b";
        RespireKey missing = $"{{{fixture.ReserveSlot(2)}}}:missing";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        (await client.Strings.SetManyAsync([(a, "first"), (b, "second"), (a, "last")], timeout.Token)).Should().BeTrue();
        (await client.Strings.GetManyAsync([b, missing, a, b], timeout.Token)).Should().Equal("second", null, "last", "second");
        using (var exists = await client.ExecuteAsync(RespireCommands.Key.EXISTS,
            [a.AsValue(), missing.AsValue(), b.AsValue(), b.AsValue()], cancellationToken: timeout.Token))
            exists.AsInteger().Should().Be(3);
        (await client.Keys.TouchAsync([a, b, missing], timeout.Token)).Should().Be(2);
        (await client.Keys.DeleteAsync([a, b, a, missing], timeout.Token)).Should().Be(2);
        (await client.Strings.SetManyAsync([(a, "1"), (b, "2")], timeout.Token)).Should().BeTrue();
        (await client.Keys.UnlinkAsync([a, missing, b, a], timeout.Token)).Should().Be(2);
    }
}
