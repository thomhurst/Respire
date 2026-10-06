using System.Globalization;
using DotNet.Testcontainers.Builders;
using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
[NotInParallel]
public class ClusterInfoEpochIntegrationTests
{
    [Test]
    [Arguments("redis:8.4-alpine", "redis-server", "redis-cli", 2)]
    [Arguments("redis:8.4-alpine", "redis-server", "redis-cli", 3)]
    [Arguments("valkey/valkey:9-alpine", "valkey-server", "valkey-cli", 2)]
    [Arguments("valkey/valkey:9-alpine", "valkey-server", "valkey-cli", 3)]
    public async Task InfoRetainsEpochsAfterRealBumpBeyondSignedLimit(string image, string server, string cli, int protocol)
    {
        // Both nodes and all setup mutations belong to this case. The client reads only
        // its selected mapped endpoint; advertised ports are local to the owned container.
        await using var container = new ContainerBuilder(image).WithEntrypoint("sh", "-c")
            .WithCommand($"for port in 7000 7001; do mkdir -p /data/$port; {server} --port $port --dir /data/$port --cluster-enabled yes --cluster-config-file nodes.conf --cluster-node-timeout 1000 --cluster-announce-ip 127.0.0.1 --appendonly no --save '' --protected-mode no & done; wait")
            .WithPortBinding(7000, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(7000).UntilInternalTcpPortIsAvailable(7001)).Build();
        await container.StartAsync();
        (await Command(7001, "CLUSTER", "SET-CONFIG-EPOCH", long.MaxValue.ToString(CultureInfo.InvariantCulture))).Trim().Should().Be("OK");
        (await Command(7000, "CLUSTER", "MEET", "127.0.0.1", "7001", "17001")).Trim().Should().Be("OK");
        using var convergence = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!(await Command(7000, "CLUSTER", "INFO"))
            .Contains("cluster_current_epoch:9223372036854775807", StringComparison.Ordinal))
            await Task.Delay(50, convergence.Token);
        // BUMPEPOCH increments only when this node does not already own the maximum.
        (await Command(7000, "CLUSTER", "BUMPEPOCH")).Trim().Should().Be("BUMPED 9223372036854775808");
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new(container.Hostname, container.GetMappedPublicPort(7000))], Connections = 1,
            Protocol = protocol == 2 ? RespProtocol.Resp2 : RespProtocol.Resp3,
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Disabled,
        });
        var info = await client.Server.ClusterInfoAsync();
        await client.DisposeAsync();
        const ulong expected = (ulong)long.MaxValue + 1;
        info.CurrentEpoch.Should().Be(expected);
        info.MyEpoch.Should().Be(expected);
        info.Attributes["cluster_current_epoch"].Should().Be(expected.ToString(CultureInfo.InvariantCulture));
        info.Attributes["cluster_my_epoch"].Should().Be(expected.ToString(CultureInfo.InvariantCulture));
        info.KnownNodes.Should().Be(2);

        async Task<string> Command(int port, params string[] arguments)
        {
            var result = await container.ExecAsync([cli, "-e", "-p", port.ToString(CultureInfo.InvariantCulture), "--raw", .. arguments]);
            result.ExitCode.Should().Be(0, result.Stderr);
            return result.Stdout;
        }
    }
}
