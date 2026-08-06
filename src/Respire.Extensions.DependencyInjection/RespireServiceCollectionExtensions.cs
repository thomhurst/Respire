using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Respire.Extensions.DependencyInjection;

public static class RespireServiceCollectionExtensions
{
    /// <summary>
    /// Registers a singleton <see cref="IRespireClient"/> (and <see cref="RespireClient"/>).
    /// Construction is lazy — nothing connects until the first command — so startup never
    /// blocks on Redis. The client picks up the container's <see cref="ILoggerFactory"/> unless
    /// the options set one explicitly.
    /// </summary>
    public static IServiceCollection AddRespire(this IServiceCollection services, string connectionString)
        => services.AddRespire(_ => RespireOptions.Parse(connectionString));

    public static IServiceCollection AddRespire(this IServiceCollection services, Func<IServiceProvider, RespireOptions> configure)
    {
        services.TryAddSingleton(provider => RespireClient.Create(Resolve(provider, configure)));
        services.TryAddSingleton<IRespireClient>(provider => provider.GetRequiredService<RespireClient>());
        return services;
    }

    /// <summary>
    /// Registers an additional named client as a keyed singleton — inject it with
    /// <c>[FromKeyedServices("cache")] IRespireClient client</c>.
    /// </summary>
    public static IServiceCollection AddRespire(this IServiceCollection services, string name, string connectionString)
        => services.AddRespire(name, _ => RespireOptions.Parse(connectionString));

    public static IServiceCollection AddRespire(
        this IServiceCollection services, string name, Func<IServiceProvider, RespireOptions> configure)
    {
        services.TryAddKeyedSingleton(name, (provider, _) => RespireClient.Create(Resolve(provider, configure)));
        services.TryAddKeyedSingleton<IRespireClient>(
            name, (provider, key) => provider.GetRequiredKeyedService<RespireClient>(key));
        return services;
    }

    private static RespireOptions Resolve(IServiceProvider provider, Func<IServiceProvider, RespireOptions> configure)
    {
        var options = configure(provider);
        if (options.LoggerFactory is null && provider.GetService<ILoggerFactory>() is { } loggerFactory)
        {
            options = options with { LoggerFactory = loggerFactory };
        }

        return options;
    }
}
