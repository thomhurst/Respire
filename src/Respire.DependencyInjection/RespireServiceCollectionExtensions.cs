using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Respire.DependencyInjection;

/// <summary>Dependency-injection registrations for Respire clients.</summary>
public static class RespireServiceCollectionExtensions
{
    /// <summary>
    /// Registers a lazy singleton client and default <see cref="IOptions{TOptions}"/> from
    /// configuration. Options are validated at host startup without connecting to Redis.
    /// The optional callback runs after binding and can supply serializers, credentials, and logging.
    /// Configuration is captured when options are first resolved; clients do not reload live.
    /// </summary>
    public static IServiceCollection AddRespire(this IServiceCollection services,
        IConfigurationSection configuration,
        Func<IServiceProvider, RespireOptions, RespireOptions>? configure = null)
        => AddConfiguration(services, null, configuration, configure);

    /// <summary>
    /// Registers a keyed singleton client and named options from configuration. The service key
    /// is also the name used with <see cref="IOptionsMonitor{TOptions}.Get"/>.
    /// Options are validated at host startup without connecting to Redis.
    /// </summary>
    public static IServiceCollection AddKeyedRespire(this IServiceCollection services, string serviceKey,
        IConfigurationSection configuration,
        Func<IServiceProvider, RespireOptions, RespireOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceKey);
        return AddConfiguration(services, serviceKey, configuration, configure);
    }

    private static IServiceCollection AddConfiguration(IServiceCollection services, string? serviceKey,
        IConfigurationSection configuration, Func<IServiceProvider, RespireOptions, RespireOptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ThrowIfRegistered(services, serviceKey);
        var name = serviceKey ?? Options.DefaultName;
        services.AddSingleton(new RespireOptionsRegistration(name, provider =>
        {
            var options = RespireConfiguration.Apply(configuration, new RespireOptions());
            return Resolve(provider, _ => configure is null ? options : configure(provider, options));
        }, configuration.Path));
        services.TryAddTransient<IOptionsFactory<RespireOptions>, RespireOptionsFactory>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<RespireOptions>, RespireOptionsValidator>());
        services.AddOptions<RespireOptions>(name).ValidateOnStart();

        RespireOptions GetOptions(IServiceProvider provider) => provider.GetRequiredService<IOptionsMonitor<RespireOptions>>().Get(name);
        return serviceKey is null
            ? AddRespireCore(services, GetOptions)
            : AddKeyedRespireCore(services, serviceKey, GetOptions);
    }

    /// <summary>
    /// Registers a singleton <see cref="IRespireClient"/> (and <see cref="RespireClient"/>).
    /// Construction is lazy — nothing connects until the first command — so startup never
    /// blocks on Redis. <see cref="RespireOptions.ConnectTimeout"/> bounds socket and TLS setup;
    /// the Redis handshake and non-blocking commands use
    /// <see cref="RespireOptions.CommandTimeout"/>. Blocking commands use their explicit wait
    /// timeout, and caller cancellation applies throughout. Standalone clients surface setup
    /// exceptions directly, while cluster clients wrap seed failures in
    /// <see cref="RespireConnectionException"/>. A later command retries connection. The client
    /// picks up the container's <see cref="ILoggerFactory"/> unless the options set one explicitly.
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
