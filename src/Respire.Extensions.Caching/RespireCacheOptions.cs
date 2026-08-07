using Microsoft.Extensions.Options;

namespace Respire.Extensions.Caching;

/// <summary>Configuration for <see cref="RespireDistributedCache"/>.</summary>
public sealed class RespireCacheOptions : IOptions<RespireCacheOptions>
{
    /// <summary>
    /// Connection string for a cache-owned client ("host:port,…", see
    /// <see cref="RespireOptions.Parse"/>). Leave null to use the container's registered
    /// <see cref="IRespireClient"/> instead (e.g. from AddRespire).
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Prefix prepended to every cache key, so several apps (or caches) can share one Redis
    /// without colliding. Same semantics as the Microsoft Redis cache's InstanceName.
    /// </summary>
    public string? InstanceName { get; set; }

    RespireCacheOptions IOptions<RespireCacheOptions>.Value => this;
}
