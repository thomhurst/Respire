using DotNet.Testcontainers.Builders;
using FluentAssertions;
using Respire.Internal;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
[ParallelLimiter<DockerHeavy>]
public class NodeAddressMappingIntegrationTests
{
    [Test]
    public async Task ClusterUsesPublishedPortsForTopologyAndAskMovedRedirects()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await using var cluster = await RedisClusterTestContainer.StartAsync(announceInternalPorts: true);
        var map = Enumerable.Range(0, 3).ToDictionary(
            node => new RespireEndpoint("127.0.0.1", 7000 + node),
            node => new RespireEndpoint(cluster.Host, cluster.Port(node)));
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            UseCluster = true, Connections = 1, ClusterTopologyRefreshInterval = null,
            Endpoints = [new(cluster.Host, cluster.Port(0))], NodeAddressMap = map,
        }, deadline.Token);
        for (var index = 0; index < 30; index++)
        {
            var key = "nat:" + index;
            await client.SetAsync(key, "value", cancellationToken: deadline.Token);
            (await client.GetStringAsync(key, deadline.Token)).Should().Be("value");
        }

        const string movedKey = "nat:moved";
        var slot = ClusterHash.GetSlot(movedKey);
        var source = Math.Min(slot / 5461, 2);
        var target = (source + 1) % 3;
        await client.SetAsync(movedKey, "migrated", cancellationToken: deadline.Token);
        await cluster.MoveKeysAsync(slot, source, target);
        // The cached owner returns ASK while the slot is importing.
        (await client.GetStringAsync(movedKey, deadline.Token)).Should().Be("migrated");
        await cluster.FinishMoveAsync(slot, target);
        // The same cached owner now returns MOVED to the advertised internal port.
        (await client.GetStringAsync(movedKey, deadline.Token)).Should().Be("migrated");
    }

    [Test]
    public async Task SentinelUsesPublishedPortsForInternallyAdvertisedPrimaryAndReplica()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        // The processes share their own private loopback; only random published ports
        // are reachable by the test client. No shared Redis resources are involved.
        await using var container = new ContainerBuilder("redis:7-alpine")
            .WithPortBinding(6379, true).WithPortBinding(6380, true).WithPortBinding(26379, true)
            .WithEntrypoint("sh", "-c")
            .WithCommand("redis-server --port 6379 --protected-mode no --appendonly no & "
                + "redis-server --port 6380 --replicaof 127.0.0.1 6379 --protected-mode no --appendonly no & "
                + "printf '%s\\n' 'port 26379' 'sentinel monitor nat-primary 127.0.0.1 6379 1' "
                + "'sentinel down-after-milliseconds nat-primary 30000' > /tmp/sentinel.conf; "
                + "redis-server /tmp/sentinel.conf --sentinel & wait")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379)
                .UntilInternalTcpPortIsAvailable(26379)
                .UntilCommandIsCompleted("sh", "-c", "redis-cli -p 6380 info replication | grep -q master_link_status:up"))
            .Build();
        await container.StartAsync(deadline.Token);
        var sentinelEndpoint = new RespireEndpoint(container.Hostname, container.GetMappedPublicPort(26379));
        await using var sentinel = await RespireSentinelClient.ConnectAsync(sentinelEndpoint, cancellationToken: deadline.Token);
        // Sentinel can list a newly discovered replica while it is still disconnected.
        // Wait for the same healthy flags required by replica routing before connecting.
        while (!(await sentinel.ReplicasAsync("nat-primary", deadline.Token)).Any(replica =>
            replica.Endpoint.Port == 6380
            && !replica.Flags.Contains("s_down", StringComparison.Ordinal)
            && !replica.Flags.Contains("o_down", StringComparison.Ordinal)
            && !replica.Flags.Contains("disconnected", StringComparison.Ordinal)))
            await Task.Delay(50, deadline.Token);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            SentinelPrimaryName = "nat-primary", Connections = 1,
            Endpoints = [sentinelEndpoint],
            NodeAddressMap = new Dictionary<RespireEndpoint, RespireEndpoint>
            {
                ["127.0.0.1:6379"] = new(container.Hostname, container.GetMappedPublicPort(6379)),
                ["127.0.0.1:6380"] = new(container.Hostname, container.GetMappedPublicPort(6380)),
            },
        }, deadline.Token);
        await client.SetAsync("nat:sentinel", "mapped", cancellationToken: deadline.Token);
        (await client.GetStringAsync("nat:sentinel", deadline.Token)).Should().Be("mapped");
        client.Endpoint.Should().Be(new RespireEndpoint("127.0.0.1", 6379));
        await client.Core.ReadRouter.RefreshNowAsync(deadline.Token);
        var reader = client.WithReadFrom(RespireReadFrom.Replica);
        while (await reader.GetStringAsync("nat:sentinel", deadline.Token) != "mapped")
            await Task.Delay(10, deadline.Token);
    }
}
