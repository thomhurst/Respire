using Keva.Client;
using Keva.Core.Connection;
using Keva.Core.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Keva.Extensions.DependencyInjection;

public static class KevaServiceCollectionExtensions
{
    public static IServiceCollection AddKeva(
        this IServiceCollection services,
        Action<KevaClientBuilder>? configure = null)
    {
        return services.AddKeva((builder, sp) => configure?.Invoke(builder));
    }
    
    public static IServiceCollection AddKeva(
        this IServiceCollection services,
        Action<KevaClientBuilder, IServiceProvider> configure)
    {
        if (configure == null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        // Register the client factory
        services.TryAddSingleton<IKevaClientFactory>(sp =>
        {
            var builder = KevaClientBuilder.Create();
            builder.WithServiceProvider(sp);
            configure(builder, sp);
            return new KevaClientFactory(builder);
        });
        
        // Register the default client
        services.TryAddSingleton<IKevaClient>(sp =>
        {
            var factory = sp.GetRequiredService<IKevaClientFactory>();
            return factory.CreateClient();
        });
        
        // Register hosted service for cleanup
        services.AddHostedService<KevaClientHostedService>();
        
        return services;
    }
    
    public static IServiceCollection AddKevaInterceptor<T>(
        this IServiceCollection services)
        where T : class, IKevaInterceptor
    {
        services.TryAddTransient<T>();
        return services;
    }
    
    public static IServiceCollection AddKevaInterceptor<T>(
        this IServiceCollection services,
        Func<IServiceProvider, T> factory)
        where T : class, IKevaInterceptor
    {
        services.TryAddTransient(factory);
        return services;
    }
}

public interface IKevaClientFactory
{
    IKevaClient CreateClient(string? name = null);
}

internal class KevaClientFactory : IKevaClientFactory
{
    private readonly KevaClientBuilder _builder;
    private readonly Dictionary<string, IKevaClient> _clients = new();
    private readonly object _lock = new();
    
    public KevaClientFactory(KevaClientBuilder builder)
    {
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
    }
    
    public IKevaClient CreateClient(string? name = null)
    {
        name ??= "default";
        
        lock (_lock)
        {
            if (!_clients.TryGetValue(name, out var client))
            {
                client = _builder.Build();
                _clients[name] = client;
            }
            
            return client;
        }
    }
}

internal class KevaClientHostedService : IHostedService
{
    private readonly IKevaClient? _client;
    
    public KevaClientHostedService(IKevaClient? client = null)
    {
        _client = client;
    }
    
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Client will connect on first use
        return Task.CompletedTask;
    }
    
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_client != null)
        {
            await _client.DisposeAsync();
        }
    }
}

// Extension methods for builder configuration
public static class KevaClientBuilderExtensions
{
    public static KevaClientBuilder UseLocalhost(this KevaClientBuilder builder, int port = 6379)
    {
        return builder.UseEndpoint("localhost", port);
    }
    
    public static KevaClientBuilder UseRedis(this KevaClientBuilder builder, string connectionString)
    {
        // Parse Redis connection string format
        // Format: "server:port,password=xxx,database=0"
        var parts = connectionString.Split(',');
        
        foreach (var part in parts)
        {
            if (part.Contains(':') && !part.Contains('='))
            {
                // Server:port
                var serverParts = part.Split(':');
                if (serverParts.Length == 2)
                {
                    builder.UseEndpoint(serverParts[0], int.Parse(serverParts[1]));
                }
            }
            else if (part.StartsWith("password="))
            {
                builder.UsePassword(part.Substring(9));
            }
            else if (part.StartsWith("database="))
            {
                builder.UseDatabase(int.Parse(part.Substring(9)));
            }
        }
        
        return builder;
    }
    
    public static KevaClientBuilder UseConnectionString(this KevaClientBuilder builder, string connectionString)
    {
        return builder.UseRedis(connectionString);
    }
}