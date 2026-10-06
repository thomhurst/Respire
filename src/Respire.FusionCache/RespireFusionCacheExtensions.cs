using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Backplane;
using ZiggyCreatures.Caching.Fusion.Locking.Distributed;

namespace Respire.FusionCache;

/// <summary>FusionCache registrations using the existing IRespireClient service.</summary>
public static class RespireFusionCacheExtensions
{
    /// <summary>Registers transient lockers using the caller-owned client; the provider stops their renewal on disposal.</summary>
    public static IServiceCollection AddFusionCacheRespireDistributedLocker(this IServiceCollection services,
        RespireFusionCacheDistributedLockerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        (options ?? new()).Validate();
        services.TryAddTransient<IFusionCacheDistributedLocker>(provider => CreateLocker(provider, options));
        return services;
    }

    /// <summary>Configures a separate provider-owned locker for this cache using the registered shared client.</summary>
    public static IFusionCacheBuilder WithRespireDistributedLocker(this IFusionCacheBuilder builder,
        RespireFusionCacheDistributedLockerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        (options ?? new()).Validate();
        // FusionCache only detaches its locker on disposal. Resolve a keyed service so DI owns it.
        var serviceKey = new object();
        builder.Services.AddKeyedSingleton<RespireFusionCacheDistributedLocker>(serviceKey,
            (provider, _) => CreateLocker(provider, options));
        return builder.WithDistributedLocker(provider => provider.GetRequiredKeyedService<RespireFusionCacheDistributedLocker>(serviceKey));
    }

    private static RespireFusionCacheDistributedLocker CreateLocker(IServiceProvider provider,
        RespireFusionCacheDistributedLockerOptions? options)
        => new(provider.GetRequiredService<IRespireClient>(), options, provider.GetService<ILogger<RespireFusionCacheDistributedLocker>>());

    /// <summary>Registers a separate backplane for each consumer without creating or owning a client.</summary>
    public static IServiceCollection AddFusionCacheRespireBackplane(this IServiceCollection services,
        RespireSubscriptionOptions subscriptionOptions = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddTransient<IFusionCacheBackplane>(provider => Create(provider, subscriptionOptions));
        return services;
    }

    /// <summary>Configures this cache's backplane using the same registered client as Respire's distributed cache.</summary>
    public static IFusionCacheBuilder WithRespireBackplane(this IFusionCacheBuilder builder,
        RespireSubscriptionOptions subscriptionOptions = default)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithBackplane(provider => Create(provider, subscriptionOptions));
    }

    private static RespireFusionCacheBackplane Create(IServiceProvider provider, RespireSubscriptionOptions options)
        => new(provider.GetRequiredService<IRespireClient>(), options, provider.GetService<ILogger<RespireFusionCacheBackplane>>());
}
