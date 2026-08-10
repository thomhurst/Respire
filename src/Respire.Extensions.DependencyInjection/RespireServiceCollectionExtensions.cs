using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Respire.Extensions.DependencyInjection;

/// <summary>Dependency-injection registrations for Respire clients.</summary>
public static class RespireServiceCollectionExtensions
{
    /// <summary>
    /// Registers a singleton <see cref="IRespireClient"/> (and <see cref="RespireClient"/>).
    /// Construction is lazy — nothing connects until the first command — so startup never
    /// blocks on Redis. The client picks up the container's <see cref="ILoggerFactory"/> unless
    /// the options set one explicitly.
    /// </summary>
    public static IServiceCollection AddRespire(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return AddRespireCore(services, _ => RespireOptions.Parse(connectionString));
    }

    /// <summary>Registers a singleton client configured through a mutable options builder.</summary>
    public static IServiceCollection AddRespire(
        this IServiceCollection services, Action<RespireOptionsBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        return AddRespireCore(services, _ => Build(configure));
    }

    /// <summary>
    /// Registers a singleton Respire client using options resolved from the service provider.
    /// </summary>
    public static IServiceCollection AddRespire(this IServiceCollection services, Func<IServiceProvider, RespireOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        return AddRespireCore(services, configure);
    }

    private static IServiceCollection AddRespireCore(
        IServiceCollection services, Func<IServiceProvider, RespireOptions> configure)
    {
        ThrowIfRegistered(services, serviceKey: null);
        services.AddSingleton(provider => RespireClient.Create(Resolve(provider, configure)));
        services.AddSingleton<IRespireClient>(provider => provider.GetRequiredService<RespireClient>());
        return services;
    }

    /// <summary>
    /// Registers an additional named client as a keyed singleton — inject it with
    /// <c>[FromKeyedServices("cache")] IRespireClient client</c>.
    /// </summary>
    public static IServiceCollection AddKeyedRespire(
        this IServiceCollection services, string serviceKey, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return AddKeyedRespireCore(services, serviceKey, _ => RespireOptions.Parse(connectionString));
    }

    /// <summary>Registers a keyed singleton client configured through a mutable options builder.</summary>
    public static IServiceCollection AddKeyedRespire(
        this IServiceCollection services, string serviceKey, Action<RespireOptionsBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceKey);
        ArgumentNullException.ThrowIfNull(configure);
        return AddKeyedRespireCore(services, serviceKey, _ => Build(configure));
    }

    /// <summary>
    /// Registers a keyed singleton client using options resolved from the service provider.
    /// </summary>
    public static IServiceCollection AddKeyedRespire(
        this IServiceCollection services, string serviceKey, Func<IServiceProvider, RespireOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceKey);
        ArgumentNullException.ThrowIfNull(configure);
        return AddKeyedRespireCore(services, serviceKey, configure);
    }

    private static IServiceCollection AddKeyedRespireCore(
        IServiceCollection services,
        string serviceKey,
        Func<IServiceProvider, RespireOptions> configure)
    {
        ThrowIfRegistered(services, serviceKey);
        services.AddKeyedSingleton(serviceKey, (provider, _) => RespireClient.Create(Resolve(provider, configure)));
        services.AddKeyedSingleton<IRespireClient>(
            serviceKey, (provider, key) => provider.GetRequiredKeyedService<RespireClient>(key));
        return services;
    }

    private static RespireOptions Build(Action<RespireOptionsBuilder> configure)
    {
        var builder = new RespireOptionsBuilder();
        configure(builder);
        return builder.Build();
    }

    private static RespireOptions Resolve(IServiceProvider provider, Func<IServiceProvider, RespireOptions> configure)
    {
        var options = configure(provider) ?? throw new InvalidOperationException(
            "The Respire options factory returned null.");
        if (options.LoggerFactory is null && provider.GetService<ILoggerFactory>() is { } loggerFactory)
        {
            options = options with { LoggerFactory = loggerFactory };
        }

        return options;
    }

    private static void ThrowIfRegistered(IServiceCollection services, string? serviceKey)
    {
        var duplicate = services.Any(descriptor =>
            (descriptor.ServiceType == typeof(RespireClient) || descriptor.ServiceType == typeof(IRespireClient)) &&
            (serviceKey is null
                ? !descriptor.IsKeyedService
                : descriptor.IsKeyedService && Equals(descriptor.ServiceKey, serviceKey)));
        if (!duplicate)
        {
            return;
        }

        var registration = serviceKey is null ? "default" : $"keyed '{serviceKey}'";
        throw new InvalidOperationException(
            $"A {registration} Respire client is already registered. Register it only once, or use a different service key.");
    }
}
