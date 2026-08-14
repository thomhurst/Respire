using Microsoft.Extensions.Options;

namespace Respire.Extensions.Caching;

/// <summary>Configuration for <see cref="RespireDistributedCache"/>.</summary>
public sealed class RespireCacheOptions : IOptions<RespireCacheOptions>
{
    /// <summary>
    /// Creates options for a cache-owned client. When set, this takes precedence over
    /// <see cref="ConnectionString"/> and receives the resolving service provider.
    /// </summary>
    public Func<IServiceProvider, RespireOptions>? ClientOptions { get; set; }

    /// <summary>
    /// Connection string for a cache-owned client ("host:port", see
    /// <see cref="RespireOptions.Parse"/>). Ignored when <see cref="ClientOptions"/> is set.
    /// Leave null to use the container's registered <see cref="IRespireClient"/> instead.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Prefix prepended to every cache key, so several apps (or caches) can share one Redis
    /// without colliding. Same semantics as the Microsoft Redis cache's InstanceName.
    /// </summary>
    public string? InstanceName { get; set; }

    RespireCacheOptions IOptions<RespireCacheOptions>.Value => this;
}
