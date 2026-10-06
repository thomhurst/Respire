using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ZiggyCreatures.Caching.Fusion;
using ZiggyCreatures.Caching.Fusion.Backplane;

namespace Respire.FusionCache;

/// <summary>FusionCache registrations using the existing IRespireClient service.</summary>
public static class RespireFusionCacheExtensions
{
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
