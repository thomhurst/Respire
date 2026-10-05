using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;

namespace Respire.Coordination.Tests;

/// <summary>
/// A dedicated primary and replica on random host ports, for tests that promote the replica by hand.
/// Unlike a Sentinel fixture it publishes no fixed host ports, so these tests can start concurrently,
/// and no Sentinel can reconfigure the promoted node while the test runs.
/// </summary>
internal sealed class RedisReplicaPair : IAsyncDisposable
{
    private const ushort RedisPort = 6379;
    private readonly INetwork _network;
    private readonly IContainer _primary;
    private readonly IContainer _replica;

    private RedisReplicaPair(INetwork network, IContainer primary, IContainer replica)
    {
        _network = network;
        _primary = primary;
        _replica = replica;
    }

    public RespireEndpoint Primary => new(_primary.Hostname, _primary.GetMappedPublicPort(RedisPort));
    public RespireEndpoint Replica => new(_replica.Hostname, _replica.GetMappedPublicPort(RedisPort));

    public static async Task<RedisReplicaPair> StartAsync(string image)
    {
        var network = new NetworkBuilder().Build();
        var primary = Build(image, network, "--protected-mode", "no");
        var replica = Build(image, network, "--protected-mode", "no", "--replicaof", "primary", RedisPort.ToString());
        var pair = new RedisReplicaPair(network, primary, replica);
        try
        {
            await network.CreateAsync();
            await Task.WhenAll(primary.StartAsync(), replica.StartAsync());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (true)
            {
                var info = await replica.ExecAsync(["redis-cli", "INFO", "replication"], timeout.Token);
                if (info.Stdout.Contains("master_link_status:up", StringComparison.Ordinal)) break;
                await Task.Delay(100, timeout.Token);
            }
            return pair;
        }
        catch
        {
            await pair.DisposeAsync();
            throw;
        }
    }

    private static IContainer Build(string image, INetwork network, params string[] arguments)
    {
        var builder = new ContainerBuilder(image)
            .WithNetwork(network)
            .WithPortBinding(RedisPort, assignRandomHostPort: true)
            .WithCommand(["redis-server", .. arguments])
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(RedisPort));
        return (arguments.Contains("--replicaof") ? builder : builder.WithNetworkAliases("primary")).Build();
    }

    public async ValueTask DisposeAsync()
    {
        await _replica.DisposeAsync();
        await _primary.DisposeAsync();
        await _network.DisposeAsync();
    }
}
