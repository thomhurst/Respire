using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Respire.Caching.Hybrid;

/// <summary>Dependency-injection registrations for HybridCache backed by Respire.</summary>
public static class RespireHybridCacheServiceCollectionExtensions
{
    /// <summary>Enables bounded local-cache coherence through an owned Redis tracking connection.</summary>
    /// <remarks>Apply to the builder returned by AddRespireHybridCache. Invalidation is eventual:
    /// reads already in progress can return their earlier result, but a retired local generation
    /// cannot serve later requests. This does not coordinate concurrent factory writes to L2.</remarks>
    public static IHybridCacheBuilder WithRespireClientSideCoherence(
        this IHybridCacheBuilder builder,
        Action<RespireHybridCacheCoherenceOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new RespireHybridCacheCoherenceOptions();
        configure?.Invoke(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxObservedKeys, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxRememberedTagInvalidations, 1);
        if (options.ObservationSweepInterval <= TimeSpan.Zero
            || options.ObservationSweepInterval.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(configure), "ObservationSweepInterval must be a positive timer interval.");
        ArgumentNullException.ThrowIfNull(options.TrackingOptions);

        // The public keyed registration supplies Microsoft's constructor factory without
        // reflection or private API access. Each local generation receives the same options,
        // serializers, and L2 service; only its IMemoryCache namespace changes.
        var serviceKey = new object();
        builder.Services.AddKeyedHybridCache(serviceKey, Options.DefaultName);
        var descriptor = builder.Services.Last(service => service.IsKeyedService
            && service.ServiceType == typeof(HybridCache) && ReferenceEquals(service.ServiceKey, serviceKey));
        var factory = descriptor.KeyedImplementationFactory!;
        builder.Services.Remove(descriptor);
        builder.Services.Replace(ServiceDescriptor.Singleton<HybridCache>(services =>
            new RespireCoherentHybridCache(services, provider => (HybridCache)factory(provider, serviceKey), options)));
        return builder;
    }

    /// <summary>
    /// Registers <see cref="HybridCache"/> with Respire as its distributed (L2) backend. The
    /// Respire cache implements IBufferDistributedCache, so HybridCache reads and writes L2
    /// through its buffer API. A configured value codec is applied to the L2 payload.
    /// Configure the Redis side (connection
    /// string, key prefix, optional value codec) with <paramref name="configureCache"/>; when no connection string is
    /// set the container's <see cref="IRespireClient"/> (from AddRespire) is used.
    /// </summary>
    public static IHybridCacheBuilder AddRespireHybridCache(
        this IServiceCollection services,
        Action<RespireCacheRegistrationOptions>? configureCache = null,
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
