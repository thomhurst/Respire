using Testcontainers.Redis;
using TUnit.Core;

namespace Respire.IntegrationTests;

public class RedisTestFixture
{
    private static RedisContainer? _redisContainer;
    
    public string ConnectionString => _redisContainer?.GetConnectionString() ?? throw new InvalidOperationException("Redis container not initialized");
    public static string Host { get; private set; } = "localhost";
    public static int Port { get; private set; }
    
    [Before(HookType.Class)]
    public static async Task InitializeAsync()
    {
        _redisContainer = new RedisBuilder()
            .WithImage("redis:7-alpine")
            .Build();
            
        await _redisContainer.StartAsync();
        
        // Parse the connection string to get host and port
        var connectionString = _redisContainer.GetConnectionString();
        var parts = connectionString.Split(',')[0].Split(':');
        Host = parts[0];
        Port = int.Parse(parts[1]);
    }
    
    [After(HookType.Class)]
    public static async Task DisposeAsync()
    {
        if (_redisContainer != null)
        {
            await _redisContainer.DisposeAsync();
            _redisContainer = null;
        }
    }
}

