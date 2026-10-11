using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Respire.Testing.Containers;

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
    private IContainer? _primary;
    private IContainer? _replica;

    private RedisReplicaPair(INetwork network)
    {
        _network = network;
    }

    public RespireEndpoint Primary => new(_primary!.Hostname, _primary.GetMappedPublicPort(RedisPort));
    public RespireEndpoint Replica => new(_replica!.Hostname, _replica.GetMappedPublicPort(RedisPort));

    public static async Task<RedisReplicaPair> StartAsync(string image)
    {
        var network = new NetworkBuilder().Build();
        var pair = new RedisReplicaPair(network);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await network.CreateAsync(timeout.Token);
            await Task.WhenAll(StartPrimaryAsync(), StartReplicaAsync());
            while (true)
            {
                var info = await pair._replica!.ExecAsync(["redis-cli", "INFO", "replication"], timeout.Token);
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

        async Task StartPrimaryAsync()
            => pair._primary = await StartContainerAsync(() => Build(image, network, "--protected-mode", "no"), timeout.Token);

        async Task StartReplicaAsync()
            => pair._replica = await StartContainerAsync(
                () => Build(image, network, "--protected-mode", "no", "--replicaof", "primary", RedisPort.ToString()), timeout.Token);
    }

    internal static async Task<IContainer> StartContainerAsync(Func<IContainer> createContainer, CancellationToken cancellationToken)
    {
        var collisions = new List<Exception>();
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var container = createContainer();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await container.StartAsync(cancellationToken);
                return container;
            }
            catch (Exception startupError)
            {
                if (collisions.Count != 0)
                    startupError.Data["RedisReplicaPair.PreviousPortCollisions"] = collisions.ToArray();
                try { await container.DisposeAsync(); }
                catch (Exception cleanupError)
                {
                    throw new AggregateException("Replica-pair container startup and cleanup both failed.",
                        collisions.Append(startupError).Append(cleanupError));
                }
                cancellationToken.ThrowIfCancellationRequested();
                // Only Docker's structured TCP bind collisions during StartAsync permit replacement.
                if (attempt >= 3 || !ContainerPortCollision.IsStandaloneBindCollision(startupError, [RedisPort]))
                    throw;
                collisions.Add(startupError);
            }
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
        try
        {
            if (_replica is not null) await _replica.DisposeAsync();
        }
        finally
        {
            try
            {
                if (_primary is not null) await _primary.DisposeAsync();
            }
            finally
            {
                await _network.DisposeAsync();
            }
        }
    }
}
