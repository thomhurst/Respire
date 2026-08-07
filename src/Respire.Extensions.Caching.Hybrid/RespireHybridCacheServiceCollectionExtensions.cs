using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;

namespace Respire.Extensions.Caching.Hybrid;

public static class RespireHybridCacheServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="HybridCache"/> with Respire as its distributed (L2) backend. The
    /// Respire cache implements IBufferDistributedCache, so HybridCache reads and writes L2
    /// through pooled buffers instead of byte[] copies. Configure the Redis side (connection
    /// string, key prefix) with <paramref name="configureCache"/>; when no connection string is
    /// set the container's <see cref="IRespireClient"/> (from AddRespire) is used.
    /// </summary>
    public static IHybridCacheBuilder AddRespireHybridCache(
        this IServiceCollection services,
        Action<RespireCacheOptions>? configureCache = null,
        Action<HybridCacheOptions>? configureHybridCache = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddRespireDistributedCache(configureCache ?? (_ => { }));
        return configureHybridCache is null
            ? services.AddHybridCache()
            : services.AddHybridCache(configureHybridCache);
    }

    /// <summary>Registers HybridCache with an L2 on its own connection to <paramref name="connectionString"/>.</summary>
    public static IHybridCacheBuilder AddRespireHybridCache(
        this IServiceCollection services,
        string connectionString,
        string? instanceName = null,
        Action<HybridCacheOptions>? configureHybridCache = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return services.AddRespireHybridCache(
            options =>
            {
                options.ConnectionString = connectionString;
                options.InstanceName = instanceName;
            },
            configureHybridCache);
    }
}
