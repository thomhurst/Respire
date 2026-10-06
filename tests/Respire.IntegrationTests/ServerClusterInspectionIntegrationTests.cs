using DotNet.Testcontainers.Builders;
using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
public class ServerClusterInspectionIntegrationTests
{
    [Test]
    [Arguments(2, false, "redis:7.0.15")]
    [Arguments(3, false, "redis:7.0.15")]
    [Arguments(2, true, "redis:8.4-alpine")]
    [Arguments(3, true, "redis:8.4-alpine")]
    [Arguments(2, true, "redis:8.10-alpine")]
    [Arguments(3, true, "redis:8.10-alpine")]
    public async Task InspectAnIsolatedClusterWithoutChangingItsTopology(int protocol, bool modern, string image)
    {
        await using var container = new ContainerBuilder(image)
            .WithPortBinding(6379, true)
            .WithCommand("redis-server", "--cluster-enabled", "yes", "--cluster-config-file", "nodes.conf",
                "--cluster-announce-ip", "127.0.0.1", "--appendonly", "no")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379)).Build();
        await container.StartAsync();
        var port = container.GetMappedPublicPort(6379);
        await Run(["CONFIG", "SET", "cluster-announce-port", port.ToString()]);
        await Run(["CLUSTER", "ADDSLOTSRANGE", "0", "16383"]);
        using (var ready = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
        {
            while (true)
            {
                var info = await container.ExecAsync(["redis-cli", "CLUSTER", "INFO"], ready.Token);
                if (info.Stdout.Contains("cluster_state:ok", StringComparison.Ordinal)) break;
                await Task.Delay(100, ready.Token);
            }
        }
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new(container.Hostname, port)], Connections = 1, UseCluster = true,
            Protocol = protocol == 3 ? RespProtocol.Resp3 : RespProtocol.Resp2,
        });
        var view = client.WithKeyPrefix("tenant:");
        await view.SetAsync("key", "value");
        var server = view.Server;
        var expectedSlot = client.ResolveKey("tenant:key").ClusterSlot;
        (await server.ClusterKeySlotAsync("key")).Should().Be(expectedSlot);
        (await server.ClusterCountKeysInSlotAsync(expectedSlot)).Should().Be(1);
        var infoResult = await server.ClusterInfoAsync();
        infoResult.State.Should().Be("ok");
        infoResult.SlotsAssigned.Should().Be(16384);
        var nodes = await server.ClusterNodesAsync();
        nodes.Should().ContainSingle();
        nodes[0].Flags.Should().Contain("myself");
        nodes[0].Slots.Should().Equal(new RespireClusterSlotRange(0, 16383));
        (await server.ClusterMyIdAsync()).Should().Be(nodes[0].Id);
        var shards = await server.ClusterShardsAsync();
        shards.Should().ContainSingle();
        shards[0].Nodes.Single().Id.Should().Be(nodes[0].Id);
        shards[0].Nodes[0].Role.Should().Be("master");
        shards[0].Slots.Should().Equal(new RespireClusterSlotRange(0, 16383));
        (await server.ClusterLinksAsync()).Should().BeEmpty(); // A single-member Cluster has no peer links.

        if (modern)
        {
            (await server.ClusterMyShardIdAsync()).Should().HaveLength(40);
            var stats = await server.ClusterSlotStatsAsync(expectedSlot, expectedSlot);
            stats.Should().ContainSingle();
            stats[0].KeyCount.Should().Be(1);
            (await server.ClusterSlotStatsByMetricAsync(RespireClusterSlotMetric.KeyCount, 1))[0].Slot.Should().Be(expectedSlot);
            (await server.ClusterMyShardIdOnAllNodesAsync())[0].Value.Should().HaveLength(40);
            (await server.ClusterSlotStatsOnAllNodesAsync(expectedSlot, expectedSlot))[0].Value.Single().KeyCount.Should().Be(1);
            (await server.ClusterSlotStatsByMetricOnAllNodesAsync(RespireClusterSlotMetric.KeyCount, 1))[0].Value.Single().Slot.Should().Be(expectedSlot);
        }
        else
        {
            Func<Task> unsupportedId = async () => await server.ClusterMyShardIdAsync();
            await unsupportedId.Should().ThrowAsync<RespireServerException>();
            Func<Task> unsupportedStats = async () => await server.ClusterSlotStatsAsync(0, 0);
            await unsupportedStats.Should().ThrowAsync<RespireServerException>();
            (await server.ClusterMyShardIdOnAllNodesAsync())[0].Error.Should().BeOfType<RespireServerException>();
            (await server.ClusterSlotStatsByMetricOnAllNodesAsync(RespireClusterSlotMetric.KeyCount, 1))[0].Error.Should().BeOfType<RespireServerException>();
        }
        var perNode = await server.ClusterInfoOnAllNodesAsync();
        perNode.Should().ContainSingle();
        perNode[0].Endpoint.Port.Should().Be(port);
        perNode[0].Value.State.Should().Be("ok");
        (await server.ClusterNodesOnAllNodesAsync())[0].Value.Single().Id.Should().Be(nodes[0].Id);
        (await server.ClusterShardsOnAllNodesAsync())[0].Value.Single().Nodes.Single().Id.Should().Be(nodes[0].Id);
        (await server.ClusterLinksOnAllNodesAsync())[0].Value.Should().BeEmpty();
        (await server.ClusterMyIdOnAllNodesAsync())[0].Value.Should().Be(nodes[0].Id);
        (await server.ClusterKeySlotOnAllNodesAsync("key"))[0].Value.Should().Be(expectedSlot);
        (await server.ClusterCountKeysInSlotOnAllNodesAsync(expectedSlot))[0].Value.Should().Be(1);
        (await view.GetStringAsync("key")).Should().Be("value");
        await client.DisposeAsync();
        shards[0].Nodes[0].Id.Should().Be(nodes[0].Id);
        infoResult.Attributes["cluster_state"].Should().Be("ok");

        async Task Run(string[] arguments)
        {
            var result = await container.ExecAsync(["redis-cli", "-e", .. arguments]);
            if (result.ExitCode != 0) throw new InvalidOperationException($"Cluster setup failed: {result.Stdout} {result.Stderr}");
        }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task StandaloneErrorsRemainServerErrors(int protocol)
    {
        await using var container = new Testcontainers.Redis.RedisBuilder("redis:7.0.15").Build();
        await container.StartAsync();
        await using var client = await RespireClient.ConnectAsync($"redis://{container.Hostname}:{container.GetMappedPublicPort(6379)}?protocol={protocol}");
        Func<Task> operation = async () => await client.Server.ClusterShardsAsync();
        await operation.Should().ThrowAsync<RespireServerException>().WithMessage("*cluster support disabled*");
        var results = await client.Server.ClusterInfoOnAllNodesAsync();
        results.Should().ContainSingle();
        results[0].Endpoint.Port.Should().Be(container.GetMappedPublicPort(6379));
        results[0].Error.Should().BeOfType<RespireServerException>();
    }
}
