using Testcontainers.Redis;
using TUnit.Core.Interfaces;

namespace Respire.Testing;

/// <summary>Session-wide Redis container for integration tests.</summary>
public sealed class RedisTestContainer : IAsyncInitializer, IAsyncDisposable
{
    private const ushort RedisPort = 6379;

    private RedisContainer? _container;

    private RedisContainer Container =>
        _container ?? throw new InvalidOperationException("Redis container has not been initialized.");

    public string ConnectionString => Container.GetConnectionString();
    public string Host => Container.Hostname;
    public int Port => Container.GetMappedPublicPort(RedisPort);

    public async Task InitializeAsync()
    {
        var container = new RedisBuilder().Build();
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
