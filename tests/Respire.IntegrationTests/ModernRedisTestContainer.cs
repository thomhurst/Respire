using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using TUnit.Core.Interfaces;

namespace Respire.IntegrationTests;

/// <summary>
/// Session-wide Redis 8 container. The shared <see cref="RedisTestContainer"/> runs Redis 7, which
/// predates the commands this fixture covers (MSETEX).
/// </summary>
public sealed class ModernRedisTestContainer : IAsyncInitializer, IAsyncDisposable
{
    private const ushort RedisPort = 6379;

    private IContainer? _container;

    private IContainer Container =>
        _container ?? throw new InvalidOperationException("Modern Redis container has not been initialized.");

    public string ConnectionString => $"redis://{Host}:{Port}";
    public string Host => Container.Hostname;
    public int Port => Container.GetMappedPublicPort(RedisPort);

    public async Task InitializeAsync()
    {
        var container = new ContainerBuilder()
            .WithImage("redis:8-alpine")
            .WithPortBinding(RedisPort, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(RedisPort))
            .Build();
        _container = container;

        try
        {
            await container.StartAsync().ConfigureAwait(false);
        }
        catch
        {
            await ResetContainerAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ResetContainerAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private async ValueTask ResetContainerAsync()
    {
        var container = _container;
        _container = null;

        if (container is not null)
        {
            await container.DisposeAsync().ConfigureAwait(false);
        }
    }
}
