using System.Diagnostics;
using System.Globalization;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category("ProtocolIndependent")]
[NotInParallel]
public class ClusterAdministrationIntegrationTests
{
    [Test]
    [Arguments("redis:8.4-alpine", "redis-server", "redis-cli", 2)]
    [Arguments("redis:8.4-alpine", "redis-server", "redis-cli", 3)]
    [Arguments("valkey/valkey:9-alpine", "valkey-server", "valkey-cli", 2)]
    [Arguments("valkey/valkey:9-alpine", "valkey-server", "valkey-cli", 3)]
    public async Task OwnedNodesSupportSlotAdministrationAndReplicaPromotion(string image, string server, string cli, int protocol)
    {
        // Every test owns both nodes. Advertised ports are container-local because only the
        // servers use them; the client deliberately targets each mapped endpoint directly.
        await using var container = OwnedNodes(image, server);
        await container.StartAsync();
        await using var first = Client(7000);
        await using var second = Client(7001);
        var primary = first.Server.OnNode(Endpoint(7000));
        var replica = second.Server.OnNode(Endpoint(7001));
        var firstId = await first.Server.ClusterMyIdAsync();
        var secondId = await second.Server.ClusterMyIdAsync();
        await primary.ClusterSetConfigEpochAsync(1);
        await replica.ClusterSetConfigEpochAsync(2);
        await primary.ClusterAddSlotsAsync([0, 2]);
        await primary.ClusterDeleteSlotsAsync([0, 2]);
        await primary.ClusterAddSlotsRangeAsync([new(0, 3), new(5, 6)]);
        await primary.ClusterDeleteSlotsRangeAsync([new(0, 3), new(5, 6)]);
        await primary.ClusterMeetAsync(new("127.0.0.1", 7001), 17001);
        // A handshake placeholder already counts as a node, but cannot be used by SETSLOT.
        await Until(async () => (await first.Server.ClusterNodesAsync()).Any(node => node.Id == secondId)
            && (await second.Server.ClusterNodesAsync()).Any(node => node.Id == firstId));
        (await primary.ClusterCountFailureReportsAsync(secondId)).Should().Be(0);
        await primary.ClusterAddSlotsRangeAsync([new(0, 16383)]);
        await primary.ClusterSetSlotAsync(0, RespireClusterSlotState.Migrating, secondId);
        await replica.ClusterSetSlotAsync(0, RespireClusterSlotState.Importing, firstId);
        await primary.ClusterSetSlotAsync(0, RespireClusterSlotState.Stable);
        await replica.ClusterSetSlotAsync(0, RespireClusterSlotState.Stable);
        await primary.ClusterSetSlotAsync(0, RespireClusterSlotState.Node, firstId);
        var epoch = await primary.ClusterBumpEpochAsync();
        epoch.Epoch.Should().BeGreaterThan(0);
        await primary.ClusterSaveConfigAsync();
        await Until(async () => (await first.Server.ClusterInfoAsync()).State == "ok");
        await replica.ClusterReplicateAsync(firstId);
        await Until(async () => (await primary.ClusterReplicasAsync(firstId)).Any(node => node.Id == secondId)
            && (await Command(container, cli, 7001, "INFO", "replication")).Contains("master_link_status:up", StringComparison.Ordinal));

        byte[] key = [255, 0, 128];
        await first.Strings.SetAsync(key, "owned-value");
        var slot = await first.Server.ClusterKeySlotAsync(key);
        var keys = await first.WithKeyPrefix("ignored:").Server.OnNode(Endpoint(7000)).ClusterGetKeysInSlotAsync(slot, 10);
        keys.Should().ContainSingle().Which.Should().Equal(key);
        (await primary.ClusterGetKeysInSlotAsync(slot, 0)).Should().BeEmpty();
        var topology = await primary.ClusterSlotsAsync();
        topology.Should().ContainSingle().Which.Primary.NodeId.Should().Be(firstId);
        await Until(async () => (await primary.ClusterSlotsAsync()).Single().Replicas.Any(node => node.NodeId == secondId));
        // Ensure the write reached the replica before deliberately promoting without consensus.
        await Until(async () => (await Command(container, cli, 7001, "DBSIZE")).Trim() == "1");
        await replica.ClusterFailoverAsync(RespireClusterFailoverMode.Takeover);
        await Until(async () => (await second.Server.ClusterNodesAsync()).Any(node => node.Id == secondId && node.Flags.Contains("master"))
            && (await second.Server.ClusterInfoAsync()).State == "ok"
            && (await first.Server.ClusterNodesAsync()).Any(node => node.Id == firstId && node.PrimaryId == secondId));
        (await second.Strings.GetAsync<string>(key)).Should().Be("owned-value");
        await second.Keys.DeleteAsync(key);
        await Until(async () => (await Command(container, cli, 7000, "DBSIZE")).Trim() == "0");
        // Reset the old node first so it cannot reintroduce itself through gossip after FORGET.
        await primary.ClusterResetAsync(RespireClusterResetMode.Hard);
        await replica.ClusterForgetAsync(firstId);
        await replica.ClusterFlushSlotsAsync();
        (await replica.ClusterSlotsAsync()).Should().BeEmpty();
        await replica.ClusterResetAsync();
        (await second.Server.ClusterMyIdAsync()).Should().Be(secondId);
        await replica.ClusterResetAsync(RespireClusterResetMode.Hard);
        (await second.Server.ClusterMyIdAsync()).Should().NotBe(secondId);
        await first.DisposeAsync();
        keys[0].Should().Equal(key);
        topology[0].Primary.NodeId.Should().Be(firstId);

        RespireEndpoint Endpoint(int port) => new(container.Hostname, container.GetMappedPublicPort(port));
        RespireClient Client(int port) => RespireClient.Create(new RespireOptions
        {
            Endpoints = [Endpoint(port)], Connections = 1, AllowAdmin = true,
            Protocol = protocol == 2 ? RespProtocol.Resp2 : RespProtocol.Resp3,
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Disabled,
        });
    }

    [Test]
    [Arguments("redis:8.4-alpine", "redis-server", 2)]
    [Arguments("redis:8.4-alpine", "redis-server", 3)]
    [Arguments("valkey/valkey:9-alpine", "valkey-server", 2)]
    [Arguments("valkey/valkey:9-alpine", "valkey-server", 3)]
    public async Task ReplicaRowsPreserveEpochAfterBumpingPastSignedLimit(string image, string server, int protocol)
    {
        await using var container = OwnedNodes(image, server);
        await container.StartAsync();
        await using var first = Client(7000);
        await using var second = Client(7001);
        var primary = first.Server.OnNode(Endpoint(7000));
        var replica = second.Server.OnNode(Endpoint(7001));
        var firstId = await first.Server.ClusterMyIdAsync();
        var secondId = await second.Server.ClusterMyIdAsync();
        await replica.ClusterSetConfigEpochAsync(long.MaxValue);
        await primary.ClusterMeetAsync(new("127.0.0.1", 7001), 17001);
        await Until(async () => (await first.Server.ClusterNodesAsync())
            .Any(node => node.Id == secondId && node.ConfigurationEpoch == (ulong)long.MaxValue)
            && (await second.Server.ClusterNodesAsync()).Any(node => node.Id == firstId));
        var bumped = await primary.ClusterBumpEpochAsync();
        bumped.Bumped.Should().BeTrue();
        bumped.Epoch.Should().Be((ulong)long.MaxValue + 1);
        await replica.ClusterReplicateAsync(firstId);
        await Until(async () => (await primary.ClusterReplicasAsync(firstId))
            .Any(node => node.Id == secondId && node.ConfigurationEpoch == bumped.Epoch));
        var nodes = await primary.ClusterReplicasAsync(firstId);
        await first.DisposeAsync();
        nodes.Single().ConfigurationEpoch.Should().Be(bumped.Epoch);

        RespireEndpoint Endpoint(int port) => new(container.Hostname, container.GetMappedPublicPort(port));
        RespireClient Client(int port) => RespireClient.Create(new RespireOptions
        {
            Endpoints = [Endpoint(port)], Connections = 1, AllowAdmin = true,
            Protocol = protocol == 2 ? RespProtocol.Resp2 : RespProtocol.Resp3,
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Disabled,
        });
    }

    private static IContainer OwnedNodes(string image, string server)
        => new ContainerBuilder(image).WithEntrypoint("sh", "-c")
            .WithCommand($"for port in 7000 7001; do mkdir -p /data/$port; {server} --port $port --dir /data/$port --cluster-enabled yes --cluster-config-file nodes.conf --cluster-node-timeout 1000 --cluster-announce-ip 127.0.0.1 --appendonly no --save '' --protected-mode no & done; wait")
            .WithPortBinding(7000, true).WithPortBinding(7001, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(7000).UntilInternalTcpPortIsAvailable(7001)).Build();

    private static async Task Until(Func<Task<bool>> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!await condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Owned Cluster did not converge.");
            await Task.Delay(50);
        }
    }

    private static async Task<string> Command(IContainer container, string cli, int port, params string[] arguments)
    {
        var result = await container.ExecAsync([cli, "-p", port.ToString(CultureInfo.InvariantCulture), "--raw", .. arguments]);
        result.ExitCode.Should().Be(0, result.Stderr);
        return result.Stdout;
    }
}
