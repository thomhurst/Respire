using System.Net;
using System.Net.Sockets;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Respire.Testing.Containers;
using StackExchange.Redis;
using Assert = Xunit.Assert;

namespace Respire.Orleans.Acceptance.Tests;

// Test-only six-process deployment. Direct connections only administer faults and fence replication.
internal sealed class ClusterDeployment(IContainer container, int[] ports) : IAsyncDisposable
{
    private readonly List<ConnectionMultiplexer> _controls = [];
    public int Port(int node) => ports[node];
    public string[] Ids { get; } = new string[6];
    public IServer Control(int node) => _controls[node].GetServer("127.0.0.1", ports[node]);
    public Task<RedisResult> Command(int node, params object[] args) => Control(node).ExecuteAsync("CLUSTER", args);

    public static Task<ClusterDeployment> Start() => StartAttempt(1);

    private static async Task<ClusterDeployment> StartAttempt(int attempt)
    {
        // Reserve command ports together; use explicit bus ports inside the container.
        var reservations = Enumerable.Range(0, 6).Select(_ => new TcpListener(IPAddress.Loopback, 0)).ToArray();
        int[] ports;
        try
        {
            foreach (var listener in reservations) listener.Start();
            ports = reservations.Select(listener => ((IPEndPoint)listener.LocalEndpoint).Port).ToArray();
        }
        finally { foreach (var listener in reservations) listener.Stop(); }
        var builder = new ContainerBuilder("redis:7.0.15");
        foreach (var port in ports) builder = builder.WithPortBinding(port, port);
        var commands = ports.Select((port, index) =>
            $"mkdir -p /data/{port}; redis-server --port {port} --dir /data/{port} --cluster-enabled yes " +
            $"--cluster-config-file nodes.conf --cluster-port {16000 + index} --cluster-announce-bus-port {16000 + index} " +
            "--cluster-node-timeout 1000 --cluster-announce-ip 127.0.0.1 --appendonly no --protected-mode no &");
        var wait = Wait.ForUnixContainer();
        foreach (var port in ports) wait = wait.UntilInternalTcpPortIsAvailable(port);
        var container = builder.WithCreateParameterModifier(parameters =>
            {
                foreach (var binding in parameters.HostConfig!.PortBindings.Values.SelectMany(value => value))
                    binding.HostIP = "127.0.0.1";
            })
            .WithEntrypoint("sh", "-c").WithCommand(string.Join(' ', commands) + " wait")
            .WithWaitStrategy(wait).Build();
        var deployment = new ClusterDeployment(container, ports);
        var startingContainer = true;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            await container.StartAsync(deadline.Token);
            startingContainer = false;
            for (var node = 0; node < 6; node++)
            {
                deployment._controls.Add(await ConnectionMultiplexer.ConnectAsync(
                    $"127.0.0.1:{ports[node]},allowAdmin=true,asyncTimeout=10000"));
                deployment.Ids[node] = (string)(await deployment.Command(node, "MYID"))!;
                if (node < 3)
                {
                    await deployment.Command(node, "SET-CONFIG-EPOCH", node + 1);
                    await deployment.Command(node, "ADDSLOTSRANGE", node * 5461, node == 2 ? 16383 : (node + 1) * 5461 - 1);
                }
                if (node > 0) await deployment.Command(0, "MEET", "127.0.0.1", ports[node], 16000 + node);
            }
            for (var node = 3; node < 6; node++)
            {
                var replica = node;
                await Until(async () => ((string)(await deployment.Command(replica, "NODES"))!)
                    .Contains(deployment.Ids[replica - 3], StringComparison.Ordinal), deadline.Token);
                await deployment.Command(node, "REPLICATE", deployment.Ids[node - 3]);
            }
            for (var node = 0; node < 6; node++)
            {
                var current = node;
                await Until(async () => ((string)(await deployment.Command(current, "INFO"))!)
                    .Contains("cluster_state:ok", StringComparison.Ordinal), deadline.Token);
            }
            for (var node = 3; node < 6; node++)
            {
                var replica = node;
                await Until(async () => ((string)(await deployment.Control(replica).ExecuteAsync("INFO", "REPLICATION"))!)
                    .Contains("master_link_status:up", StringComparison.Ordinal), deadline.Token);
            }
            return deployment;
        }
        catch (Exception error)
        {
            await deployment.DisposeAsync();
            // Releasing reservations before Docker binds leaves a race with other host sockets.
            // Retry only recognized bind collisions, with fresh ports and a fresh container.
            if (startingContainer && attempt < 3 && ContainerPortCollision.IsMatch(error, ports))
                return await StartAttempt(attempt + 1);
            throw;
        }
    }

    public async Task BeginMigration(int source, int target, int slot, string key)
    {
        await Command(target, "SETSLOT", slot, "IMPORTING", Ids[source]);
        await Command(source, "SETSLOT", slot, "MIGRATING", Ids[target]);
        Assert.Equal("OK", (string?)await Control(source).ExecuteAsync("MIGRATE", "127.0.0.1", ports[target], key, 0, 10000));
    }

    public async Task FinishMigration(int target, int slot)
    {
        // The importing target must become owner before peers announce its ownership.
        await Command(target, "SETSLOT", slot, "NODE", Ids[target]);
        for (var node = 0; node < 3; node++)
            if (node != target) await Command(node, "SETSLOT", slot, "NODE", Ids[target]);
    }

    public static async Task Until(Func<Task<bool>> condition, CancellationToken token)
    {
        while (!await condition().WaitAsync(token)) await Task.Delay(100, token);
    }

    public async ValueTask DisposeAsync()
    {
        try { foreach (var connection in _controls) await connection.DisposeAsync(); }
        finally { await container.DisposeAsync(); }
    }
}
