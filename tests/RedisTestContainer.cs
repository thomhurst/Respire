using Testcontainers.Redis;
using TUnit.Core;
using TUnit.Core.Interfaces;

namespace Respire.Testing;

/// <summary>Session-wide Redis container for integration tests.</summary>
public sealed class RedisTestContainer : IAsyncInitializer, IAsyncDisposable
{
    private const int DatabaseCount = 4096;
    private const ushort RedisPort = 6379;

    private RedisContainer? _container;

    private RedisContainer Container =>
        _container ?? throw new InvalidOperationException("Redis container has not been initialized.");

    public int Database
    {
        get
        {
            var database = TestContext.Current?.Isolation.UniqueId
                ?? throw new InvalidOperationException("A Redis database can only be assigned within a test context.");

            if (database is < 0 or >= DatabaseCount)
            {
                throw new InvalidOperationException(
                    $"Test isolation ID {database} is outside the configured Redis database range.");
            }

            return database;
        }
    }

    public string ConnectionString => $"redis://{Host}:{Port}/{Database}";
    public string StackExchangeConnectionString => $"{Host}:{Port},defaultDatabase={Database}";
    public string Host => Container.Hostname;
    public int Port => Container.GetMappedPublicPort(RedisPort);

    public async Task InitializeAsync()
    {
        var container = new RedisBuilder()
            .WithCommand("redis-server", "--databases", DatabaseCount.ToString())
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
