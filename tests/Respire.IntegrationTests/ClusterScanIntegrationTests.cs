using System.Net;
using System.Net.Sockets;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Respire.Internal;
using TUnit.Core;

namespace Respire.IntegrationTests;

[NotInParallel]
public class ClusterScanIntegrationTests
{
    [Test]
    public async Task PeriodicRefreshRoutesWritesAfterRealPrimaryFailover()
    {
        await using var cluster = await ScanCluster.StartAsync();
        var options = new RespireOptions
        {
            UseCluster = true,
            Protocol = RespProtocol.Resp2,
            ClusterTopologyRefreshInterval = TimeSpan.FromMilliseconds(100),
            Endpoints = { new RespireEndpoint(cluster.Host, cluster.Port(0)) },
        };
        await using var client = await RespireClient.ConnectAsync(options);
        var key = $"{{{TagForSlot(1)}}}:before-failover";
        var slot = ClusterHash.GetSlot(key);
        (await client.SetAsync(key, "before")).Should().BeTrue();
        client.Core.Cluster!.GetSlotOwnerEndpoint(slot)
            .Should().Be(new RespireEndpoint("127.0.0.1", cluster.Port(0)));

        await cluster.FailoverAsync(replica: 3);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var promotedEndpoint = new RespireEndpoint("127.0.0.1", cluster.Port(3));
        while (client.Core.Cluster.GetSlotOwnerEndpoint(slot) != promotedEndpoint)
            await Task.Delay(25, deadline.Token);

        (await client.SetAsync(key, "after")).Should().BeTrue();
        (await client.GetStringAsync(key)).Should().Be("after");
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task SerializedCursorReturnsEveryPersistentKeyWhileSlotMovesToCompletedPrimary(int protocol)
    {
        await using var cluster = await ScanCluster.StartAsync();
        var options = new RespireOptions
        {
            UseCluster = true, Protocol = (RespProtocol)protocol, Connections = 1,
            Endpoints = { new RespireEndpoint(cluster.Host, cluster.Port(0)) },
        };
        await using var client = await RespireClient.ConnectAsync(options);
        var tag = TagForSlot(6000);
        var prefix = Guid.NewGuid().ToString("N");
        var expected = Enumerable.Range(0, 256).Select(index => $"{prefix}:{{{tag}}}:{index}").ToHashSet();
        expected.Add($"{prefix}:{{{TagForSlot(1)}}}:stable-first");
        expected.Add($"{prefix}:{{{TagForSlot(12000)}}}:stable-third");
        foreach (var key in expected) await client.SetAsync(key, "present throughout scan");
        var seen = new HashSet<string>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var firstCheckpoint = RespireClusterScanCursor.Start;
        RespireClusterScanPage page;
        do
        {
            page = await client.Keys.ScanClusterPageAsync(firstCheckpoint, countHint: 1, cancellationToken: deadline.Token);
            seen.UnionWith(page.Keys);
            firstCheckpoint = page.Cursor;
        }
        while (page.Cursor.CompletedSlotCount == 0);
        page.Cursor.CompletedSlotCount.Should().Be(5461);
        page = await client.Keys.ScanClusterPageAsync(page.Cursor, countHint: 1);
        seen.UnionWith(page.Keys);
        page.Cursor.IsComplete.Should().BeFalse();
        seen.Count.Should().BeLessThan(expected.Count);

        // The destination has already completed its scan. Move keys before publishing the
        // new owner, exercising the importing/migrating window as well as final ownership.
        await cluster.MoveKeysAsync(6000, source: 1, target: 0);
        page = await client.Keys.ScanClusterPageAsync(RespireClusterScanCursor.Parse(page.Cursor.ToString()), countHint: 1);
        seen.UnionWith(page.Keys);
        page.Cursor.IsComplete.Should().BeFalse();
        await cluster.FinishMoveAsync(6000, target: 0);

        // A new client has no process-local cursor registry or previous connection state.
        await using var resumed = await RespireClient.ConnectAsync(options);
        var checkpoint = RespireClusterScanCursor.Parse(page.Cursor.ToString());
        var pages = 0;
        do
        {
            page = await resumed.Keys.ScanClusterPageAsync(checkpoint, countHint: 13, cancellationToken: deadline.Token);
            seen.UnionWith(page.Keys);
            checkpoint = RespireClusterScanCursor.Parse(page.Cursor.ToString());
            pages++;
        }
        while (!checkpoint.IsComplete);
        pages.Should().BeGreaterThan(1);
        seen.Should().BeEquivalentTo(expected);
        foreach (var key in expected) (await resumed.GetStringAsync(key)).Should().Be("present throughout scan");
    }

    private static string TagForSlot(int slot)
    {
        for (var index = 0; ; index++)
        {
            var tag = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (ClusterHash.GetSlot(tag) == slot) return tag;
        }
    }

    private sealed class ScanCluster(IContainer container, int basePort) : IAsyncDisposable
    {
        private readonly string[] _ids = new string[6];
        internal string Host => container.Hostname;
        internal int Port(int node) => container.GetMappedPublicPort(basePort + node);

        internal static async Task<ScanCluster> StartAsync()
        {
            var basePort = FindAvailableClusterPorts();
            var ports = Enumerable.Range(basePort, 6).ToArray();
            var container = new ContainerBuilder("redis:7.0.15")
                .WithPortBinding(ports[0], ports[0]).WithPortBinding(ports[1], ports[1])
                .WithPortBinding(ports[2], ports[2]).WithPortBinding(ports[3], ports[3])
                .WithPortBinding(ports[4], ports[4]).WithPortBinding(ports[5], ports[5])
                .WithCreateParameterModifier(parameters =>
                {
                    foreach (var binding in parameters.HostConfig.PortBindings.Values.SelectMany(static bindings => bindings))
                    {
                        binding.HostIP = "127.0.0.1";
                    }
                })
                .WithEntrypoint("sh", "-c")
                .WithCommand($"for port in {string.Join(' ', ports)}; do mkdir -p /data/$port; redis-server --port $port --dir /data/$port --cluster-enabled yes --cluster-config-file nodes.conf --cluster-node-timeout 1000 --cluster-announce-ip 127.0.0.1 --cluster-announce-port $port --cluster-announce-bus-port $((port + 10000)) --appendonly no --protected-mode no & done; wait")
                .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(ports[0])
                    .UntilInternalTcpPortIsAvailable(ports[1]).UntilInternalTcpPortIsAvailable(ports[2])
                    .UntilInternalTcpPortIsAvailable(ports[3]).UntilInternalTcpPortIsAvailable(ports[4])
                    .UntilInternalTcpPortIsAvailable(ports[5]))
                .Build();
            var cluster = new ScanCluster(container, basePort);
            try
            {
                await container.StartAsync();
                for (var node = 0; node < 6; node++)
                {
                    await cluster.CommandAsync(node, "CLUSTER", "SET-CONFIG-EPOCH", (node + 1).ToString());
                    if (node < 3)
                        await cluster.CommandAsync(node, "CLUSTER", "ADDSLOTSRANGE", (node * 5461).ToString(), (node == 2 ? 16383 : (node + 1) * 5461 - 1).ToString());
                    cluster._ids[node] = (await cluster.CommandAsync(node, "CLUSTER", "MYID")).Trim();
                }
                await cluster.CommandAsync(0, "CLUSTER", "MEET", "127.0.0.1", (basePort + 1).ToString());
                await cluster.CommandAsync(0, "CLUSTER", "MEET", "127.0.0.1", (basePort + 2).ToString());
                for (var node = 3; node < 6; node++)
                    await cluster.CommandAsync(0, "CLUSTER", "MEET", "127.0.0.1", (basePort + node).ToString());
                using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                for (var node = 0; node < 6; node++)
                {
                    while (!(await cluster.CommandAsync(node, "CLUSTER", "INFO")).Contains("cluster_state:ok", StringComparison.Ordinal))
                        await Task.Delay(100, ready.Token);
                }
                for (var replica = 3; replica < 6; replica++)
                {
                    await cluster.CommandAsync(replica, "CLUSTER", "REPLICATE", cluster._ids[replica - 3]);
                    while (true)
                    {
                        var replication = await cluster.CommandAsync(replica, "INFO", "replication");
                        if (replication.Contains("role:slave", StringComparison.Ordinal)
                            && replication.Contains("master_link_status:up", StringComparison.Ordinal)) break;
                        await Task.Delay(100, ready.Token);
                    }
                }
                return cluster;
            }
            catch { await cluster.DisposeAsync(); throw; }
        }

        private static int FindAvailableClusterPorts()
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                var basePort = Random.Shared.Next(20_000, 39_994);
                var listeners = new List<TcpListener>(12);
                try
                {
                    foreach (var port in Enumerable.Range(basePort, 6)
                        .Concat(Enumerable.Range(basePort + 10_000, 6)))
                    {
                        var listener = new TcpListener(IPAddress.Any, port);
                        listener.Start();
                        listeners.Add(listener);
                    }
                    return basePort;
                }
                catch (SocketException)
                {
                }
                finally
                {
                    foreach (var listener in listeners) listener.Stop();
                }
            }

            throw new InvalidOperationException("Could not reserve a free Redis Cluster test port range.");
        }

        internal async Task FailoverAsync(int replica)
        {
            await CommandAsync(replica, "CLUSTER", "FAILOVER");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                while (true)
                {
                    var nodes = await CommandAsync(replica, "CLUSTER", "NODES");
                    var promoted = nodes.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                        .Any(line => line.StartsWith(_ids[replica], StringComparison.Ordinal)
                            && line.Contains("myself,master", StringComparison.Ordinal));
                    if (promoted) return;
                    await Task.Delay(100, deadline.Token);
                }
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                var nodes = await CommandAsync(replica, "CLUSTER", "NODES");
                throw new InvalidOperationException($"Replica {replica} was not promoted before timeout. CLUSTER NODES: {nodes}");
            }
        }

        internal async Task MoveKeysAsync(int slot, int source, int target)
        {
            await CommandAsync(target, "CLUSTER", "SETSLOT", slot.ToString(), "IMPORTING", _ids[source]);
            await CommandAsync(source, "CLUSTER", "SETSLOT", slot.ToString(), "MIGRATING", _ids[target]);
            while (true)
            {
                var keys = (await CommandAsync(source, "CLUSTER", "GETKEYSINSLOT", slot.ToString(), "100"))
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                if (keys.Length == 0) return;
                await CommandAsync(source, ["MIGRATE", "127.0.0.1", (basePort + target).ToString(), "", "0", "5000", "KEYS", .. keys]);
            }
        }

        internal async Task FinishMoveAsync(int slot, int target)
        {
            for (var node = 0; node < 3; node++)
                await CommandAsync(node, "CLUSTER", "SETSLOT", slot.ToString(), "NODE", _ids[target]);
        }

        private async Task<string> CommandAsync(int node, params string[] arguments)
        {
            var result = await container.ExecAsync(["redis-cli", "-e", "--raw", "-p", (basePort + node).ToString(), .. arguments]);
            if (result.ExitCode != 0) throw new InvalidOperationException($"Cluster fixture command failed: {result.Stdout} {result.Stderr}");
            return result.Stdout;
        }

        public ValueTask DisposeAsync() => container.DisposeAsync();
    }
}
