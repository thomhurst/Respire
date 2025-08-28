using Testcontainers.Redis;
using TUnit.Core;
using TUnit.Core.Interfaces;

namespace Respire.IntegrationTests;

public class RedisTestFixture : IAsyncInitializer, IAsyncDisposable
{
    private readonly RedisContainer _redisContainer = new RedisBuilder().Build();
    
    public string ConnectionString => _redisContainer.GetConnectionString() ?? throw new InvalidOperationException("Redis container not initialized");
    public static string Host { get; private set; } = "localhost";
    public static int Port { get; private set; }
    
    public async Task InitializeAsync()
    {
        await _redisContainer.StartAsync();
        
        // Parse the connection string to get host and port
        var connectionString = _redisContainer.GetConnectionString();
        var parts = connectionString.Split(',')[0].Split(':');
        Host = parts[0];
        Port = int.Parse(parts[1]);
    }
    
    public async ValueTask DisposeAsync()
    {
        await _redisContainer.DisposeAsync();
    }
}

