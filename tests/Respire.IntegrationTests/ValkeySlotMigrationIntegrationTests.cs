using System.Globalization;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Respire.Internal;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
[ParallelLimiter<DockerHeavy>]
public class ValkeySlotMigrationIntegrationTests
{
    [Test]
    [Arguments("valkey/valkey:9.0-alpine", 2)]
    [Arguments("valkey/valkey:9.0-alpine", 3)]
    [Arguments("valkey/valkey:9.1-alpine", 2)]
    [Arguments("valkey/valkey:9.1-alpine", 3)]
    public async Task ExportStatusCancellationAndFlushUseOwnedCluster(string image, int protocol)
    {
        await using var cluster = await OwnedCluster.StartAsync(image);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = deadline.Token;
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [cluster.Endpoint(0)], AllowAdmin = true,
            Protocol = protocol == 2 ? RespProtocol.Resp2 : RespProtocol.Resp3,
            CommandTimeout = TimeSpan.FromSeconds(5), ConnectTimeout = TimeSpan.FromSeconds(5),
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Disabled,
        });
        var source = client.Server.OnNode(cluster.Endpoint(0));
        var target = client.Server.OnNode(cluster.Endpoint(1));
        var key = KeyInSlot(0);
        await cluster.CommandAsync(0, token, "SET", key, "owned-value");
        (await source.ClusterGetSlotMigrationsAsync(token)).Should().BeEmpty();
        await source.ClusterMigrateSlotsAsync([
            new(cluster.NodeIds[1], [new(0, 0), new(2, 2)]),
            new(cluster.NodeIds[2], [new(4, 4)])], token);
        RespireValkeySlotMigration[] completed;
        while (true)
        {
            completed = await source.ClusterGetSlotMigrationsAsync(token);
            completed.Should().HaveCount(2);
            completed.Should().NotContain(job => job.State == "failed" || job.State == "cancelled");
            if (completed.All(job => job.State == "success")) break;
            await Task.Delay(50, token);
        }
        var exported = completed.Single(job => job.TargetNodeId == cluster.NodeIds[1]);
        exported.Operation.Should().Be("EXPORT");
        exported.SourceNodeId.Should().Be(cluster.NodeIds[0]);
        exported.CreateTime.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(-5));
        exported.IsTerminal.Should().BeTrue();
        if (image.Contains("9.1", StringComparison.Ordinal)) exported.RemainingReplicationBytes.Should().Be(0);
        else exported.RemainingReplicationBytes.Should().BeNull();
        (await cluster.CommandAsync(1, token, "GET", key)).TrimEnd('\r', '\n').Should().Be("owned-value");
        (await target.ClusterGetSlotMigrationsAsync(token)).Should().Contain(job => job.Name == exported.Name && job.Operation == "IMPORT");

        // FLUSHSLOT removes only keys in the selected slot, not ownership or adjacent data.
        var neighbour = KeyInSlot(2);
        await cluster.CommandAsync(1, token, "SET", neighbour, "keep");
        await target.ClusterFlushSlotAsync(0, ServerFlushMode.Sync, token);
        (await cluster.CommandAsync(1, token, "EXISTS", key)).Trim().Should().Be("0");
        (await cluster.CommandAsync(1, token, "GET", neighbour)).Trim().Should().Be("keep");
        await cluster.CommandAsync(1, token, "SET", key, "still-owned");
        await target.ClusterFlushSlotAsync(0, ServerFlushMode.Async, token);
        (await cluster.CommandAsync(1, token, "EXISTS", key)).Trim().Should().Be("0");
        await target.ClusterFlushSlotAsync(2, cancellationToken: token);
        (await cluster.CommandAsync(1, token, "EXISTS", neighbour)).Trim().Should().Be("0");

        // Pause the target to hold its handshake; cancellation on the source cannot race completion.
        // The owned container is removed on every exit; no cleanup command is sent to the paused node.
        await cluster.CommandAsync(1, token, "CLIENT", "PAUSE", "60000", "ALL");
        await source.ClusterMigrateSlotsAsync([new(cluster.NodeIds[1], [new(6, 6)])], token);
        var active = (await source.ClusterGetSlotMigrationsAsync(token)).Single(job => !job.IsTerminal);
        await source.ClusterCancelSlotMigrationsAsync(token);
        (await source.ClusterGetSlotMigrationsAsync(token)).Should().Contain(job => job.Name == active.Name && job.State == "cancelled");
        var cancelWithoutExports = async () => await source.ClusterCancelSlotMigrationsAsync(token);
        await cancelWithoutExports.Should().ThrowAsync<RespireServerException>().WithMessage("*No migrations ongoing*");
        var invalid = async () => await source.ClusterMigrateSlotsAsync([new(cluster.NodeIds[1], [new(6000, 6000)])], token);
        await invalid.Should().ThrowAsync<RespireServerException>();
        await client.DisposeAsync();
        exported.SourceNodeId.Should().Be(cluster.NodeIds[0]);
    }

    private static string KeyInSlot(int slot) => Enumerable.Range(0, 1_000_000)
        .Select(index => $"valkey-migration-{index}").First(key => ClusterHash.GetSlot(key) == slot);

    private sealed class OwnedCluster(IContainer container) : IAsyncDisposable
    {
        internal string[] NodeIds { get; } = new string[3];
        internal RespireEndpoint Endpoint(int index) => new(container.Hostname, container.GetMappedPublicPort(7000 + index));

        internal static async Task<OwnedCluster> StartAsync(string image)
        {
            var container = new ContainerBuilder(image)
                .WithPortBinding(7000, true).WithPortBinding(7001, true).WithPortBinding(7002, true)
                .WithEntrypoint("sh", "-c")
                .WithCommand("for port in 7000 7001 7002; do mkdir -p /data/$port; valkey-server --port $port --dir /data/$port --cluster-enabled yes --cluster-config-file nodes.conf --cluster-node-timeout 1000 --cluster-announce-ip 127.0.0.1 --appendonly no --protected-mode no & done; wait")
                .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(7000)
                    .UntilInternalTcpPortIsAvailable(7001).UntilInternalTcpPortIsAvailable(7002)).Build();
            var cluster = new OwnedCluster(container);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                await container.StartAsync(deadline.Token);
                for (var node = 0; node < 3; node++)
                {
                    await cluster.CommandAsync(node, deadline.Token, "CLUSTER", "SET-CONFIG-EPOCH", (node + 1).ToString(CultureInfo.InvariantCulture));
                    await cluster.CommandAsync(node, deadline.Token, "CLUSTER", "ADDSLOTSRANGE",
                        (node * 5461).ToString(CultureInfo.InvariantCulture), (node == 2 ? 16383 : (node + 1) * 5461 - 1).ToString(CultureInfo.InvariantCulture));
                    cluster.NodeIds[node] = (await cluster.CommandAsync(node, deadline.Token, "CLUSTER", "MYID")).Trim();
                }
                // Migration sockets use internal ports; only client connections use random host ports.
                await cluster.CommandAsync(0, deadline.Token, "CLUSTER", "MEET", "127.0.0.1", "7001");
                await cluster.CommandAsync(0, deadline.Token, "CLUSTER", "MEET", "127.0.0.1", "7002");
                for (var node = 0; node < 3; node++)
                    while (!(await cluster.CommandAsync(node, deadline.Token, "CLUSTER", "INFO")).Contains("cluster_state:ok", StringComparison.Ordinal))
                        await Task.Delay(50, deadline.Token);
                return cluster;
            }
            catch { await cluster.DisposeAsync(); throw; }
        }

        internal async Task<string> CommandAsync(int node, CancellationToken token, params string[] arguments)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var result = await container.ExecAsync(["valkey-cli", "-e", "--raw", "-p", (7000 + node).ToString(CultureInfo.InvariantCulture), .. arguments], deadline.Token);
            if (result.ExitCode != 0) throw new InvalidOperationException($"Owned cluster command failed: {result.Stdout} {result.Stderr}");
            return result.Stdout;
        }

        public ValueTask DisposeAsync() => container.DisposeAsync();
    }
}
