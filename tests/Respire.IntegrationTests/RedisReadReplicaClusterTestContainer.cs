using System.Net;
using System.Net.Sockets;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Respire.Internal;
using Respire.Testing.Containers;

namespace Respire.IntegrationTests;

internal sealed class RedisReadReplicaClusterTestContainer(IContainer container, int[] ports) : IAsyncDisposable
{
    internal string Host => container.Hostname;
    internal int Port(int node) => container.GetMappedPublicPort(ports[node]);
    internal Task<string> ClusterSlotsAsync() => CommandAsync(0, "CLUSTER", "SLOTS");
    internal Task<string> ClusterNodesAsync() => CommandAsync(0, "CLUSTER", "NODES");

    internal static async Task<RedisReadReplicaClusterTestContainer> StartAsync()
    {
        var (container, ports) = await StartContainerAsync();
        var cluster = new RedisReadReplicaClusterTestContainer(container, ports);
        try
        {
            var ids = new string[6];
            for (var node = 0; node < 6; node++)
            {
                if (node < 3)
                {
                    await cluster.CommandAsync(node, "CLUSTER", "SET-CONFIG-EPOCH", (node + 1).ToString());
                    var start = node * 5461;
                    var end = node == 2 ? 16383 : (node + 1) * 5461 - 1;
                    await cluster.CommandAsync(node, "CLUSTER", "ADDSLOTSRANGE", start.ToString(), end.ToString());
                }
                ids[node] = (await cluster.CommandAsync(node, "CLUSTER", "MYID")).Trim();
            }

            for (var node = 1; node < 6; node++)
                await cluster.CommandAsync(0, "CLUSTER", "MEET", "127.0.0.1", ports[node].ToString());
            using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (true)
            {
                var nodes = await cluster.CommandAsync(0, "CLUSTER", "NODES");
                if (ids.All(nodes.Contains)) break;
                await Task.Delay(100, ready.Token);
            }
            for (var replica = 3; replica < 6; replica++)
            {
                while (!(await cluster.CommandAsync(replica, "CLUSTER", "NODES"))
                    .Contains(ids[replica - 3], StringComparison.Ordinal))
                {
                    await Task.Delay(100, ready.Token);
                }
                await cluster.CommandAsync(replica, "CLUSTER", "REPLICATE", ids[replica - 3]);
            }

            while (true)
            {
                var primariesReady = true;
                for (var primary = 0; primary < 3; primary++)
                {
                    if (!(await cluster.CommandAsync(primary, "CLUSTER", "INFO"))
                        .Contains("cluster_state:ok", StringComparison.Ordinal))
                    {
                        primariesReady = false;
                        break;
                    }
                }
                if (primariesReady) break;
                await Task.Delay(100, ready.Token);
            }

            for (var primary = 0; primary < 3; primary++)
            {
                var start = primary * 5461;
                var end = primary == 2 ? 16383 : (primary + 1) * 5461 - 1;
                var key = FindKeyInRange(start, end);
                await cluster.CommandAsync(primary, "SET", key, "replica-warmup");
                while (!int.TryParse(await cluster.CommandAsync(primary, "WAIT", "1", "5000"), out var acknowledgements)
                    || acknowledgements < 1)
                {
                    await Task.Delay(100, ready.Token);
                }
            }

            while (true)
            {
                var allReady = true;
                for (var node = 0; node < 6; node++)
                {
                    if (!(await cluster.CommandAsync(node, "CLUSTER", "INFO"))
                        .Contains("cluster_state:ok", StringComparison.Ordinal))
                    {
                        allReady = false;
                        break;
                    }
                }
                if (allReady)
                {
                    var nodes = await cluster.CommandAsync(0, "CLUSTER", "NODES");
                    for (var replica = 3; replica < 6; replica++)
                    {
                        var replicaLine = nodes.Split('\n').FirstOrDefault(line =>
                            line.StartsWith(ids[replica], StringComparison.Ordinal));
                        var fields = replicaLine?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (fields is not { Length: >= 8 }
                            || !fields[2].Contains("slave", StringComparison.Ordinal)
                            || fields[3] != ids[replica - 3]
                            || fields[^1] != "connected")
                        {
                            allReady = false;
                            break;
                        }
                    }
                    for (var replica = 3; allReady && replica < 6; replica++)
                    {
                        var replication = await cluster.CommandAsync(replica, "INFO", "REPLICATION");
                        if (!replication.Contains("master_link_status:up", StringComparison.Ordinal)
                            || !replication.Contains("master_sync_in_progress:0", StringComparison.Ordinal))
                        {
                            allReady = false;
                        }
                    }
                    for (var primary = 0; allReady && primary < 3; primary++)
                    {
                        var slots = await cluster.CommandAsync(primary, "CLUSTER", "SLOTS");
                        if (!slots.Contains(ids[primary + 3], StringComparison.Ordinal))
                            allReady = false;
                    }
                }
                if (allReady) break;
                await Task.Delay(100, ready.Token);
            }
            return cluster;
        }
        catch
        {
            await cluster.DisposeAsync();
            throw;
        }
    }

    private static async Task<(IContainer Container, int[] Ports)> StartContainerAsync()
    {
        const int maximumAttempts = 3;
        var excludedPorts = new HashSet<int>();
        for (var attempt = 1; ; attempt++)
        {
            var ports = new int[6];
            for (var i = 0; i < ports.Length; i++)
            {
                ports[i] = GetAvailablePort(excludedPorts);
                excludedPorts.Add(ports[i]);
            }
            var commandPorts = string.Join(' ', ports);
            var builder = new ContainerBuilder("redis:7.0.15");
            foreach (var port in ports) builder = builder.WithPortBinding(port, port);
            var container = builder
                .WithCreateParameterModifier(parameters =>
                {
                    var portBindings = parameters.HostConfig?.PortBindings;
                    if (portBindings is null) return;
                    foreach (var binding in portBindings.Values.SelectMany(static bindings => bindings))
                        binding.HostIP = IPAddress.Loopback.ToString();
                })
                .WithEntrypoint("sh", "-c")
                .WithCommand($"for port in {commandPorts}; do mkdir -p /data/$port; redis-server --port $port --dir /data/$port --cluster-enabled yes --cluster-config-file nodes.conf --cluster-node-timeout 1000 --cluster-announce-ip 127.0.0.1 --appendonly no --protected-mode no & done; wait")
                .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(ports[0])
                    .UntilInternalTcpPortIsAvailable(ports[1]).UntilInternalTcpPortIsAvailable(ports[2])
                    .UntilInternalTcpPortIsAvailable(ports[3]).UntilInternalTcpPortIsAvailable(ports[4])
                    .UntilInternalTcpPortIsAvailable(ports[5]))
                .Build();
            try
            {
                await container.StartAsync();
                return (container, ports);
            }
            catch (Exception error)
            {
                var retry = attempt < maximumAttempts && ContainerPortCollision.IsMatch(error, ports);
                try { await container.DisposeAsync(); }
                catch (Exception cleanupError)
                {
                    throw new AggregateException("Replica cluster container startup and cleanup both failed.", error, cleanupError);
                }
                if (!retry) throw;
            }
        }
    }

    private async Task<string> CommandAsync(int node, params string[] arguments)
    {
        var result = await container.ExecAsync(["redis-cli", "-e", "--raw", "-p", ports[node].ToString(), .. arguments]);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Replica cluster setup failed: {result.Stdout} {result.Stderr}");
        return result.Stdout;
    }

    private static int GetAvailablePort(HashSet<int> excludedPorts)
    {
        while (true)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            if (port <= 55_535 && !excludedPorts.Contains(port)) return port;
        }
    }

    private static string FindKeyInRange(int start, int end)
    {
        for (var candidate = 0; ; candidate++)
        {
            var key = $"{{replica-warmup-{candidate}}}:key";
            var slot = ClusterHash.GetSlot(key);
            if (slot >= start && slot <= end) return key;
        }
    }

    public ValueTask DisposeAsync() => container.DisposeAsync();
}
