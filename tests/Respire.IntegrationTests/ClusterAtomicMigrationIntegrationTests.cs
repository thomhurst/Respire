using System.Globalization;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Respire.Internal;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
[ParallelLimiter<DockerHeavy>]
public class ClusterAtomicMigrationIntegrationTests
{
    [Test]
    [Arguments("redis:8.4-alpine", 2)]
    [Arguments("redis:8.4-alpine", 3)]
    [Arguments("redis:8.10-alpine", 2)]
    [Arguments("redis:8.10-alpine", 3)]
    public async Task ImportStatusAndCancellationUseOwnedCluster(string image, int protocol)
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
        var destination = client.Server.OnNode(cluster.Endpoint(1));
        var key = Enumerable.Range(0, 1_000_000).Select(index => $"atomic-migration-{index}")
            .First(candidate => ClusterHash.GetSlot(candidate) == 0);
        await cluster.CommandAsync(0, token, "SET", key, "owned-value");
        (await destination.ClusterMigrationStatusAsync(cancellationToken: token)).Should().BeEmpty();
        (await destination.ClusterMigrationStatusAsync("missing", token)).Should().BeEmpty();
        // The published optional selector is rejected by these real Redis versions.
        var omitted = async () => await destination.ClusterMigrationStatusAsync(RespireClusterMigrationStatusScope.Default, token);
        await omitted.Should().ThrowAsync<RespireServerException>();

        var taskId = await destination.ClusterMigrationImportAsync([new(0, 0), new(2, 2)], token);
        taskId.Should().NotBeNullOrEmpty();
        RespireClusterMigrationTask completed;
        while (true)
        {
            completed = (await destination.ClusterMigrationStatusAsync(taskId, token)).Should().ContainSingle().Which;
            if (completed.State == "completed") break;
            completed.State.Should().NotBe("failed");
            await Task.Delay(50, token);
        }
        completed.Operation.Should().Be("import");
        completed.SourceNodeId.Should().Be(cluster.NodeIds[0]);
        completed.DestinationNodeId.Should().Be(cluster.NodeIds[1]);
        completed.CreateTime.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(-5));
        completed.StartTime.Should().NotBeNull();
        completed.EndTime.Should().NotBeNull();
        completed.LastError.Should().BeEmpty();
        (await cluster.CommandAsync(1, token, "GET", key)).TrimEnd('\r', '\n').Should().Be("owned-value");
        (await destination.ClusterMigrationStatusAsync(cancellationToken: token)).Should().Contain(task => task.Id == taskId);
        (await destination.ClusterMigrationCancelAsync(taskId, token)).Should().Be(0);

        // Block the source's replication handshake so cancellation tests cannot race a completed import.
        // Do not send cleanup commands to the paused source. The await-using fixture removes
        // this entire owned container on every exit, including assertion failures.
        await cluster.CommandAsync(0, token, "CLIENT", "PAUSE", "60000", "ALL");
        var cancelId = await destination.ClusterMigrationImportAsync([new(4, 4)], token);
        (await destination.ClusterMigrationCancelAsync(cancelId, token)).Should().Be(1);
        var cancelled = (await destination.ClusterMigrationStatusAsync(cancelId, token)).Should().ContainSingle().Which;
        cancelled.State.Should().Be("canceled");
        await destination.ClusterMigrationImportAsync([new(6, 6)], token);
        (await destination.ClusterMigrationCancelAllAsync(token)).Should().Be(1);
        (await destination.ClusterMigrationCancelAllAsync(token)).Should().Be(0);

        // This range is already owned by the chosen destination; the error must stay node-local.
        var invalid = async () => await destination.ClusterMigrationImportAsync([new(6000, 6000)], token);
        await invalid.Should().ThrowAsync<RespireServerException>();
        await client.DisposeAsync();
        completed.Id.Should().Be(taskId); // Parsed results outlive sockets and the client.
    }

    private sealed class OwnedCluster(IContainer container) : IAsyncDisposable
    {
        internal string[] NodeIds { get; } = new string[3];
        internal RespireEndpoint Endpoint(int index) => new(container.Hostname, container.GetMappedPublicPort(7000 + index));

        internal static async Task<OwnedCluster> StartAsync(string image)
        {
            var container = new ContainerBuilder(image)
                .WithPortBinding(7000, true).WithPortBinding(7001, true).WithPortBinding(7002, true)
                .WithEntrypoint("sh", "-c")
                .WithCommand("for port in 7000 7001 7002; do mkdir -p /data/$port; redis-server --port $port --dir /data/$port --cluster-enabled yes --cluster-config-file nodes.conf --cluster-node-timeout 1000 --cluster-announce-ip 127.0.0.1 --appendonly no --protected-mode no & done; wait")
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
                // Keep the internal ports advertised: ASM opens server-to-server replication sockets.
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
            var result = await container.ExecAsync(["redis-cli", "-e", "--raw", "-p", (7000 + node).ToString(CultureInfo.InvariantCulture), .. arguments], deadline.Token);
            if (result.ExitCode != 0) throw new InvalidOperationException($"Owned cluster command failed: {result.Stdout} {result.Stderr}");
            return result.Stdout;
        }

        public ValueTask DisposeAsync() => container.DisposeAsync();
    }
}
