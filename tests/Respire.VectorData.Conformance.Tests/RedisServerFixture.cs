using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Respire.VectorData.Conformance.Tests;

[CollectionDefinition(Name)]
public sealed class RedisConformanceCollection : ICollectionFixture<RedisServerFixture>
{
    public const string Name = "Official VectorData conformance";
}

public sealed class RedisServerFixture : IAsyncLifetime
{
    // Same Redis Query Engine image as the VectorData CI service.
    private const string Image = "mirror.gcr.io/library/redis:8.10-alpine@sha256:3811787313eba226a2ef38658c6ccb91cd5e110edc89c37767de373120a0e5a0";
    private IContainer? _container;

    public string ConnectionString { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var externalConnection = Environment.GetEnvironmentVariable("RESPIRE_VECTOR_CONFORMANCE_CONNECTION");
        if (!string.IsNullOrWhiteSpace(externalConnection))
        {
            ConnectionString = externalConnection;
            return;
        }

        _container = new ContainerBuilder(Image)
            .WithPortBinding(6379, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379))
            .Build();
        try
        {
            await _container.StartAsync().ConfigureAwait(false);
            ConnectionString = $"redis://{_container.Hostname}:{_container.GetMappedPublicPort(6379)}";
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is { } container)
        {
            _container = null;
            await container.DisposeAsync().ConfigureAwait(false);
        }
        GC.SuppressFinalize(this);
    }
}
