using Aspire.Respire;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using global::Respire.Caching;

namespace Microsoft.Extensions.Hosting;

/// <summary>HybridCache with Respire as its distributed backend.</summary>
public static class AspireRespireHybridCachingExtensions
{
    /// <summary>Registers HybridCache and a distributed cache using this registration's existing client.</summary>
    public static AspireRespireClientBuilder AddHybridCache(this AspireRespireClientBuilder builder,
        Action<RespireCacheOptions>? configureCache = null, Action<HybridCacheOptions>? configureHybrid = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.AddDistributedCache(configureCache);
        if (configureHybrid is null) builder.HostBuilder.Services.AddHybridCache();
        else builder.HostBuilder.Services.AddHybridCache(configureHybrid);
        return builder;
    }
}
