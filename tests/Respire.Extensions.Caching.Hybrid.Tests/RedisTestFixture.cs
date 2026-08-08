using Testcontainers.Redis;
using TUnit.Core.Interfaces;

namespace Respire.Extensions.Caching.Hybrid.Tests;

public class RedisTestFixture : IAsyncInitializer, IAsyncDisposable
{
    private readonly RedisContainer _redisContainer = new RedisBuilder().Build();

    public string ConnectionString =>
        _redisContainer.GetConnectionString() ?? throw new InvalidOperationException("Redis container not initialized");

    public Task InitializeAsync() => _redisContainer.StartAsync();

    public async ValueTask DisposeAsync()
    {
        await _redisContainer.DisposeAsync();
    }
}
