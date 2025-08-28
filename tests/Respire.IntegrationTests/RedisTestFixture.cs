using Testcontainers.Redis;
using Xunit;

namespace Respire.IntegrationTests;

public class RedisTestFixture : IAsyncLifetime
{
    private readonly RedisContainer _redisContainer;
    
    public string ConnectionString => _redisContainer.GetConnectionString();
    public string Host { get; private set; } = "localhost";
    public int Port { get; private set; }
    
    public RedisTestFixture()
    {
        _redisContainer = new RedisBuilder()
            .WithImage("redis:7-alpine")
            .Build();
    }
    
    public async Task InitializeAsync()
    {
        await _redisContainer.StartAsync();
        
        // Parse the connection string to get host and port
        var connectionString = _redisContainer.GetConnectionString();
        var parts = connectionString.Split(',')[0].Split(':');
        Host = parts[0];
        Port = int.Parse(parts[1]);
    }
    
    public async Task DisposeAsync()
    {
        await _redisContainer.DisposeAsync();
    }
}

[CollectionDefinition("Redis")]
public class RedisCollection : ICollectionFixture<RedisTestFixture>
{
    // This class has no code, and is never created. Its purpose is simply
    // to be the place to apply [CollectionDefinition] and all the
    // ICollectionFixture<> interfaces.
}