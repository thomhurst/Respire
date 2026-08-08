using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using TUnit.Core.Interfaces;

namespace Respire.IntegrationTests;

/// <summary>Session-wide Redis container started with --requirepass.</summary>
public sealed class SecuredRedisTestContainer : IAsyncInitializer, IAsyncDisposable
{
    public const string Password = "integration-pass";

    private const ushort RedisPort = 6379;

    private IContainer? _container;

    private IContainer Container =>
        _container ?? throw new InvalidOperationException("Secured Redis container has not been initialized.");

    public string Host => Container.Hostname;
    public int Port => Container.GetMappedPublicPort(RedisPort);

    public async Task InitializeAsync()
    {
        var container = new ContainerBuilder()
            .WithImage("redis:7-alpine")
            .WithPortBinding(RedisPort, true)
            .WithCommand("redis-server", "--requirepass", Password)
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
