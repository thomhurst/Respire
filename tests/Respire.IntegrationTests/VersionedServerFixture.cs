using System.Collections.Concurrent;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using TUnit.Core.Interfaces;

namespace Respire.IntegrationTests;

/// <summary>
/// Session-wide servers for tests that compare behaviour across server images. Each image starts
/// once, on first use, and each test leases its own logical database so protocol rows can share it.
/// </summary>
/// <remarks>
/// Use a <c>SharedType.Keyed</c> instance for tests that read server-wide counters such as
/// INFO commandstats, so unrelated tests on the same image cannot disturb them.
/// </remarks>
public sealed class VersionedServerFixture : IAsyncInitializer, IAsyncDisposable
{
    /// <summary>Key for the keyed instance, and its NotInParallel constraint, used by INFO commandstats assertions.</summary>
    public const string CommandStatsKey = "commandstats";

    private const ushort RedisPort = 6379;
    private const int DatabaseCount = 256;

    private readonly ConcurrentDictionary<string, Lazy<Task<VersionedServer>>> _servers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Image, int? Databases), Lazy<Task<IContainer>>> _clusters = new();

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>A standalone server for <paramref name="image"/>, with a database leased for the calling test.</summary>
    public async Task<VersionedServerLease> LeaseAsync(string image)
    {
        var server = await _servers.GetOrAdd(image, static key =>
            new Lazy<Task<VersionedServer>>(() => VersionedServer.StartAsync(key, DatabaseCount))).Value;
        return server.Lease();
    }

    /// <summary>
    /// A single-node cluster owning every slot, started once per image and <c>cluster-databases</c> value.
    /// Tests sharing it must use their own keys and must not change slot ownership.
    /// </summary>
    public Task<IContainer> SingleNodeClusterAsync(string image, int? clusterDatabases = null)
        => _clusters.GetOrAdd((image, clusterDatabases), static key =>
            new Lazy<Task<IContainer>>(() => StartSingleNodeClusterAsync(key.Image, key.Databases))).Value;

    private static async Task<IContainer> StartSingleNodeClusterAsync(string image, int? clusterDatabases)
    {
        var redis = image.StartsWith("redis:", StringComparison.Ordinal);
        var cli = redis ? "redis-cli" : "valkey-cli";
        List<string> arguments = [redis ? "redis-server" : "valkey-server", "--cluster-enabled", "yes",
            "--cluster-config-file", "nodes.conf", "--cluster-announce-ip", "127.0.0.1", "--appendonly", "no"];
        if (clusterDatabases.HasValue)
            arguments.AddRange(["--cluster-databases", clusterDatabases.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        var container = new ContainerBuilder(image).WithPortBinding(RedisPort, true).WithCommand(arguments.ToArray())
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(RedisPort)).Build();
        try
        {
            await container.StartAsync();
            // Discovery must advertise the mapped client port, not the container-private port.
            await RunAsync(container, [cli, "-e", "CONFIG", "SET", "cluster-announce-port",
                container.GetMappedPublicPort(RedisPort).ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            await RunAsync(container, [cli, "-e", "CLUSTER", "ADDSLOTSRANGE", "0", "16383"]);
            using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (true)
            {
                var result = await container.ExecAsync([cli, "CLUSTER", "INFO"], ready.Token);
                if (result.Stdout.Contains("cluster_state:ok", StringComparison.Ordinal)) return container;
                await Task.Delay(100, ready.Token);
            }
        }
        catch
        {
            await container.DisposeAsync();
            throw;
        }
    }

    private static async Task RunAsync(IContainer container, string[] command)
    {
        var result = await container.ExecAsync(command);
        if (result.ExitCode != 0) throw new InvalidOperationException($"Cluster setup failed: {result.Stdout} {result.Stderr}");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var server in _servers.Values)
        {
            if (!server.IsValueCreated) continue;
            try { await (await server.Value).DisposeAsync(); }
            catch { /* A failed start already surfaced in the test that requested it. */ }
        }
        foreach (var cluster in _clusters.Values)
        {
            if (!cluster.IsValueCreated) continue;
            try { await (await cluster.Value).DisposeAsync(); }
            catch { /* A failed start already surfaced in the test that requested it. */ }
        }
        _servers.Clear();
        _clusters.Clear();
    }

    private sealed class VersionedServer(IContainer container, int databaseCount) : IAsyncDisposable
    {
        // Database 0 is left untouched so a test can never observe another test's keys there.
        private int _nextDatabase;

        public static async Task<VersionedServer> StartAsync(string image, int databaseCount)
        {
            // Both the Redis and Valkey entrypoints prepend their server binary to option-only commands.
            var container = new ContainerBuilder(image).WithPortBinding(RedisPort, true)
                .WithCommand("--databases", databaseCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(RedisPort)).Build();
            try { await container.StartAsync(); }
            catch { await container.DisposeAsync(); throw; }
            return new VersionedServer(container, databaseCount);
        }

        public VersionedServerLease Lease()
        {
            var database = Interlocked.Increment(ref _nextDatabase);
            if (database >= databaseCount)
                throw new InvalidOperationException(
                    $"{container.Image.FullName} has no free database; raise VersionedServerFixture.DatabaseCount.");
            return new(container.Hostname, container.GetMappedPublicPort(RedisPort), database);
        }

        public ValueTask DisposeAsync() => container.DisposeAsync();
    }
}

/// <summary>A test's own database on a shared versioned server.</summary>
public sealed record VersionedServerLease(string Host, int Port, int Database)
{
    public RespireEndpoint Endpoint => new(Host, Port);

    public string ConnectionString(int protocol) => $"redis://{Host}:{Port}/{Database}?protocol={protocol}";
}
