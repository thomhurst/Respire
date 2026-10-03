using Microsoft.Extensions.Options;

namespace Respire.Extensions.Caching;

/// <summary>
/// Configuration for <see cref="RespireCachingServiceCollectionExtensions.AddRespireDistributedCache(Microsoft.Extensions.DependencyInjection.IServiceCollection, Action{RespireCacheRegistrationOptions})"/>.
/// Adds the settings for a cache-owned client to <see cref="RespireCacheOptions"/>.
/// </summary>
public sealed class RespireCacheRegistrationOptions : RespireCacheOptions, IOptions<RespireCacheRegistrationOptions>
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

    RespireCacheRegistrationOptions IOptions<RespireCacheRegistrationOptions>.Value => this;
}
