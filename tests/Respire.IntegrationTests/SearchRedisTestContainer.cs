using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using TUnit.Core.Interfaces;

namespace Respire.IntegrationTests;

/// <summary>Redis 8.4 is the first Redis Open Source version supporting FT.HYBRID.</summary>
public sealed class SearchRedisTestContainer : IAsyncInitializer, IAsyncDisposable
{
    private readonly IContainer _container = new ContainerBuilder("redis:8.4-alpine")
        .WithPortBinding(6379, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379))
        .Build();

    public string ConnectionString => $"redis://{_container.Hostname}:{_container.GetMappedPublicPort(6379)}";

    public async Task InitializeAsync()
    {
        try { await _container.StartAsync().ConfigureAwait(false); }
        catch
        {
            await _container.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
