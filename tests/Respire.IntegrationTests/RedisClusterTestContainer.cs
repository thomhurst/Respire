using System.Net;
using Docker.DotNet;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Respire.IntegrationTests;

internal sealed class RedisClusterTestContainer(IContainer container) : IAsyncDisposable
{
    private readonly string[] _ids = new string[3];
    internal string Host => container.Hostname;
    internal int Port(int node) => container.GetMappedPublicPort(7000 + node);

    // Docker picks the random host ports, but on busy runners its listener can still lose the
    // port to another socket. Retry that bind race on a fresh container rather than failing the test.
    internal static async Task<RedisClusterTestContainer> StartAsync(string image = "redis:7.0.15", bool loadBloomModule = false)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return await StartOnceAsync(image, loadBloomModule); }
            catch (DockerApiException error) when (attempt < 3 && IsPortConflict(error)) { }
        }
    }

    private static bool IsPortConflict(DockerApiException error)
        => error.StatusCode == HttpStatusCode.InternalServerError
            && ((error.ResponseBody ?? "").Contains("address already in use", StringComparison.OrdinalIgnoreCase)
                || (error.ResponseBody ?? "").Contains("port is already allocated", StringComparison.OrdinalIgnoreCase));

    private static async Task<RedisClusterTestContainer> StartOnceAsync(string image, bool loadBloomModule)
    {
        // This fixture starts server binaries directly, bypassing Redis 8's module-loading entrypoint.
        // The optional module path matches the official redis:8-alpine image used by probabilistic tests.
        var moduleArguments = loadBloomModule ? " --loadmodule /usr/local/lib/redis/modules/redisbloom.so" : "";
        var container = new ContainerBuilder(image)
            .WithPortBinding(7000, true).WithPortBinding(7001, true).WithPortBinding(7002, true)
            .WithEntrypoint("sh", "-c")
            .WithCommand("for port in 7000 7001 7002; do mkdir -p /data/$port; redis-server --port $port --dir /data/$port --cluster-enabled yes --cluster-config-file nodes.conf --cluster-node-timeout 1000 --cluster-announce-ip 127.0.0.1 --appendonly no --protected-mode no" + moduleArguments + " & done; wait")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(7000)
                .UntilInternalTcpPortIsAvailable(7001).UntilInternalTcpPortIsAvailable(7002))
            .Build();
        var cluster = new RedisClusterTestContainer(container);
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
