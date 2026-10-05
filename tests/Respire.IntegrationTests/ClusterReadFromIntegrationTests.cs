using FluentAssertions;
using Respire.Commands;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
public class ClusterReadFromIntegrationTests
{
    [Test]
    [Arguments(RespProtocol.Resp2, RespireReadFrom.Replica)]
    [Arguments(RespProtocol.Resp3, RespireReadFrom.Replica)]
    [Arguments(RespProtocol.Resp2, RespireReadFrom.Nearest)]
    [Arguments(RespProtocol.Resp3, RespireReadFrom.Nearest)]
    public async Task ReplicaReadsObserveUpdatesAndExpiry(RespProtocol protocol, RespireReadFrom policy)
    {
        await using var cluster = await RedisReadReplicaClusterTestContainer.StartAsync();
        var slots = await cluster.ClusterSlotsAsync();
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            UseCluster = true,
            Protocol = protocol,
            Connections = 1,
            Endpoints = [new(cluster.Host, cluster.Port(0))],
        });
        var key = $"{{read-from-{Guid.NewGuid():N}}}:value";
        var replica = client.WithReadFrom(policy);

        await client.Strings.SetAsync(key, "first");
        try
        {
            await WaitForReplicaValue("first");
        }
        catch (RespireConnectionException error)
        {
            throw new InvalidOperationException($"Replica routing failed. CLUSTER SLOTS: {slots} CLUSTER NODES: {await cluster.ClusterNodesAsync()}", error);
        }
        using (var raw = await replica.ExecuteAsync(RespireCommands.String.GET, key))
            raw.AsString().Should().Be("first");
        using (var batch = replica.CreateBatch())
        {
            var pending = batch.Strings.GetString(key);
            await batch.ExecuteAsync();
            (await pending).Should().Be("first");
        }
        await client.Strings.SetAsync(key, "updated", RespireExpiry.In(TimeSpan.FromSeconds(2)));
        await WaitForReplicaValue("updated");
        using var expiry = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await replica.Strings.GetStringAsync(key, expiry.Token) is not null)
            await Task.Delay(100, expiry.Token);

        async Task WaitForReplicaValue(string expected)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                if (await replica.Strings.GetStringAsync(key, deadline.Token) == expected) return;
                await Task.Delay(50, deadline.Token);
            }
        }
    }
}
