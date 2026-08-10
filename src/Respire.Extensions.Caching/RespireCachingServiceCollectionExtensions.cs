using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Respire.Extensions.Caching;

public static class RespireCachingServiceCollectionExtensions
{
    /// <summary>
    /// Registers a Redis-backed <see cref="IDistributedCache"/> (which also implements
    /// <see cref="Microsoft.Extensions.Caching.Distributed.IBufferDistributedCache"/>) using
    /// Respire. When <see cref="RespireCacheOptions.ClientOptions"/> or
    /// <see cref="RespireCacheOptions.ConnectionString"/> is set the cache creates and owns its
    /// own client — lazily, so startup never blocks on Redis; otherwise it uses the container's
    /// <see cref="IRespireClient"/>.
    /// </summary>
    public static IServiceCollection AddRespireDistributedCache(
        this IServiceCollection services, Action<RespireCacheOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions();
        services.Configure(configure);
        // Add, not TryAdd: choosing a distributed-cache backend must win over an earlier
        // registration (e.g. AddDistributedMemoryCache), matching AddStackExchangeRedisCache.
        services.AddSingleton<IDistributedCache>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<RespireCacheOptions>>().Value;
            RespireOptions? clientOptions = null;
            if (options.ClientOptions is { } configureClient)
            {
                clientOptions = configureClient(provider) ?? throw new RespireConfigurationException(
                    $"{nameof(RespireCacheOptions.ClientOptions)} returned null.");
            }
            else if (options.ConnectionString is { } connectionString)
            {
                clientOptions = RespireOptions.Parse(connectionString);
            }

            if (clientOptions is not null)
            {
                if (clientOptions.LoggerFactory is null && provider.GetService<ILoggerFactory>() is { } loggerFactory)
                {
                    clientOptions = clientOptions with { LoggerFactory = loggerFactory };
                }

                return new RespireDistributedCache(RespireClient.Create(clientOptions), options);
            }

            var client = provider.GetService<IRespireClient>() ?? throw new RespireConfigurationException(
                $"No {nameof(IRespireClient)} is registered and neither " +
                $"{nameof(RespireCacheOptions.ClientOptions)} nor {nameof(RespireCacheOptions.ConnectionString)} " +
                "is set. Either register a client first or configure a cache-owned client.");
            return new RespireDistributedCache(client, options);
        });
        return services;
    }

    /// <summary>Registers the cache on its own connection to <paramref name="connectionString"/>.</summary>
    public static IServiceCollection AddRespireDistributedCache(
        this IServiceCollection services, string connectionString, string? instanceName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return services.AddRespireDistributedCache(options =>
        {
            options.ConnectionString = connectionString;
            options.InstanceName = instanceName;
        });
    }
}
