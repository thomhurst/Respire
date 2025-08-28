using Respire.FastClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Respire.Extensions.DependencyInjection;

public static class RespireServiceCollectionExtensions
{
    public static IServiceCollection AddRespire(
        this IServiceCollection services,
        string host = "localhost",
        int port = 6379,
        string? password = null)
    {
        // Register the client factory
        services.TryAddSingleton<IRespireClientFactory>(sp =>
        {
            return new RespireClientFactory(host, port, password);
        });
        
        // Register the default client
        services.TryAddSingleton<RespireClient>(sp =>
        {
            var factory = sp.GetRequiredService<IRespireClientFactory>();
            return factory.CreateClient().GetAwaiter().GetResult();
        });
        
        // Register hosted service for cleanup
        services.AddHostedService<RespireClientHostedService>();
        
        return services;
    }
    
    public static IServiceCollection AddRespire(
        this IServiceCollection services,
        Func<IServiceProvider, (string host, int port, string? password)> configureFactory)
    {
        // Register the client factory
        services.TryAddSingleton<IRespireClientFactory>(sp =>
        {
            var (host, port, password) = configureFactory(sp);
            return new RespireClientFactory(host, port, password);
        });
        
        // Register the default client
        services.TryAddSingleton<RespireClient>(sp =>
        {
            var factory = sp.GetRequiredService<IRespireClientFactory>();
            return factory.CreateClient().GetAwaiter().GetResult();
        });
        
        // Register hosted service for cleanup
        services.AddHostedService<RespireClientHostedService>();
        
        return services;
    }
}

public interface IRespireClientFactory
{
    Task<RespireClient> CreateClient(string? name = null);
}

internal class RespireClientFactory : IRespireClientFactory
{
    private readonly string _host;
    private readonly int _port;
    private readonly string? _password;
    private readonly Dictionary<string, RespireClient> _clients = new();
    private readonly object _lock = new();
    
    public RespireClientFactory(string host, int port, string? password)
    {
        _host = host;
        _port = port;
        _password = password;
    }
    
    public async Task<RespireClient> CreateClient(string? name = null)
    {
        name ??= "default";
        
        lock (_lock)
        {
            if (_clients.TryGetValue(name, out var existingClient))
            {
                return existingClient;
            }
        }
        
        // Create client outside of lock to avoid blocking
        var client = await RespireClient.CreateAsync(_host, _port, password: _password);
        
        lock (_lock)
        {
            // Double check in case another thread created it
            if (_clients.TryGetValue(name, out var existingClient))
            {
                client.Dispose();
                return existingClient;
            }
            
            _clients[name] = client;
            return client;
        }
    }
}

internal class RespireClientHostedService : IHostedService
{
    private readonly RespireClient? _client;
    
    public RespireClientHostedService(RespireClient? client = null)
    {
        _client = client;
    }
    
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Client will connect on first use
        return Task.CompletedTask;
    }
    
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _client?.Dispose();
        return Task.CompletedTask;
    }
}