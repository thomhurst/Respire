using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Respire.Internal;
using TUnit.Core;

namespace Respire.IntegrationTests;

public class ClusterScanIntegrationTests
{
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

    private sealed class ScanCluster(IContainer container) : IAsyncDisposable
    {
        private readonly string[] _ids = new string[3];
        internal string Host => container.Hostname;
        internal int Port(int node) => container.GetMappedPublicPort(7000 + node);

        internal static async Task<ScanCluster> StartAsync()
        {
            var container = new ContainerBuilder("redis:7.0.15")
                .WithPortBinding(7000, true).WithPortBinding(7001, true).WithPortBinding(7002, true)
                .WithEntrypoint("sh", "-c")
                .WithCommand("for port in 7000 7001 7002; do mkdir -p /data/$port; redis-server --port $port --dir /data/$port --cluster-enabled yes --cluster-config-file nodes.conf --cluster-node-timeout 1000 --cluster-announce-ip 127.0.0.1 --appendonly no --protected-mode no & done; wait")
                .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(7000)
                    .UntilInternalTcpPortIsAvailable(7001).UntilInternalTcpPortIsAvailable(7002))
                .Build();
            var cluster = new ScanCluster(container);
            try
            {
                await container.StartAsync();
                for (var node = 0; node < 3; node++)
                {
                    await cluster.CommandAsync(node, "CONFIG", "SET", "cluster-announce-port", cluster.Port(node).ToString());
                    await cluster.CommandAsync(node, "CLUSTER", "SET-CONFIG-EPOCH", (node + 1).ToString());
                    await cluster.CommandAsync(node, "CLUSTER", "ADDSLOTSRANGE", (node * 5461).ToString(), (node == 2 ? 16383 : (node + 1) * 5461 - 1).ToString());
                    cluster._ids[node] = (await cluster.CommandAsync(node, "CLUSTER", "MYID")).Trim();
                }
                await cluster.CommandAsync(0, "CLUSTER", "MEET", "127.0.0.1", "7001");
                await cluster.CommandAsync(0, "CLUSTER", "MEET", "127.0.0.1", "7002");
                using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                for (var node = 0; node < 3; node++)
                {
                    while (!(await cluster.CommandAsync(node, "CLUSTER", "INFO")).Contains("cluster_state:ok", StringComparison.Ordinal))
                        await Task.Delay(100, ready.Token);
                }
                return cluster;
            }
            catch { await cluster.DisposeAsync(); throw; }
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
                await CommandAsync(source, ["MIGRATE", "127.0.0.1", (7000 + target).ToString(), "", "0", "5000", "KEYS", .. keys]);
            }
        }

        internal async Task FinishMoveAsync(int slot, int target)
        {
            for (var node = 0; node < 3; node++)
                await CommandAsync(node, "CLUSTER", "SETSLOT", slot.ToString(), "NODE", _ids[target]);
        }

        private async Task<string> CommandAsync(int node, params string[] arguments)
        {
            var result = await container.ExecAsync(["redis-cli", "-e", "--raw", "-p", (7000 + node).ToString(), .. arguments]);
            if (result.ExitCode != 0) throw new InvalidOperationException($"Cluster fixture command failed: {result.Stdout} {result.Stderr}");
            return result.Stdout;
        }

        public ValueTask DisposeAsync() => container.DisposeAsync();
    }
}
