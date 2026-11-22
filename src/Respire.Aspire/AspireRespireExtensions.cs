using global::Respire;
using global::Respire.DependencyInjection;
using global::Respire.HealthChecks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Respire.Aspire;

namespace Microsoft.Extensions.Hosting;

/// <summary>Registers Respire clients from Aspire connection strings with health checks, telemetry, and host logging.</summary>
public static class AspireRespireExtensions
{
    /// <summary>Registers the default singleton client. Connections remain lazy until its first command.</summary>
    public static void AddRespireClient(this IHostApplicationBuilder builder, string connectionName,
        Action<RespireClientSettings>? configureSettings = null,
        Func<IServiceProvider, RespireOptions, RespireOptions>? configureOptions = null)
        => builder.AddRespireClientBuilder(connectionName, configureSettings, configureOptions);

    /// <summary>Registers the default client and returns its context for companion cache integrations.</summary>
    public static AspireRespireClientBuilder AddRespireClientBuilder(this IHostApplicationBuilder builder, string connectionName,
        Action<RespireClientSettings>? configureSettings = null,
        Func<IServiceProvider, RespireOptions, RespireOptions>? configureOptions = null)
        => Add(builder, connectionName, null, configureSettings, configureOptions);

    /// <summary>Registers a keyed singleton client whose service key is its connection name.</summary>
    public static void AddKeyedRespireClient(this IHostApplicationBuilder builder, string connectionName,
        Action<RespireClientSettings>? configureSettings = null,
        Func<IServiceProvider, RespireOptions, RespireOptions>? configureOptions = null)
        => builder.AddKeyedRespireClientBuilder(connectionName, configureSettings, configureOptions);

    /// <summary>Registers a keyed client and returns its context for companion cache integrations.</summary>
    public static AspireRespireClientBuilder AddKeyedRespireClientBuilder(this IHostApplicationBuilder builder, string connectionName,
        Action<RespireClientSettings>? configureSettings = null,
        Func<IServiceProvider, RespireOptions, RespireOptions>? configureOptions = null)
        => Add(builder, connectionName, connectionName, configureSettings, configureOptions);

    /// <summary>Applies configuration before registering a lazy, host-owned client and its optional integrations.</summary>
    private static AspireRespireClientBuilder Add(IHostApplicationBuilder builder, string connectionName, string? serviceKey,
        Action<RespireClientSettings>? configureSettings,
        Func<IServiceProvider, RespireOptions, RespireOptions>? configureOptions)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);
        var settings = ReadSettings(builder.Configuration, connectionName);
        settings.ConnectionStringAssigned = false;
        configureSettings?.Invoke(settings);
        var callbackAssignedConnectionString = settings.ConnectionStringAssigned;
        var callbackConnectionString = settings.ConnectionString;
        var disableLogging = settings.DisableLogging;
        RespireOptions? options = null;

        if (!settings.DeferConfiguration)
        {
            options = BuildOptions(builder.Configuration, connectionName, settings.ConnectionString);

            if (configureOptions is null)
            {
                ValidateEndpoints(options, connectionName);
                options = options.ValidateAndSnapshot();
            }
        }

        RespireOptions ResolveOptions(IServiceProvider provider)
        {
            var configured = options;

            if (configured is null)
            {
                var configuration = provider.GetService<IConfiguration>() ?? builder.Configuration;
                configured = BuildOptions(configuration, connectionName,
                    callbackAssignedConnectionString ? callbackConnectionString : ReadSettings(configuration, connectionName).ConnectionString);
            }

            var resolved = configureOptions is null ? configured : configureOptions(provider, configured)
                ?? throw new InvalidOperationException("The Respire options callback returned null.");
            ValidateEndpoints(resolved, connectionName);
            return disableLogging ? resolved with { LoggerFactory = NullLoggerFactory.Instance } : resolved;
        }

        if (serviceKey is null)
        {
            builder.Services.AddRespire(ResolveOptions);
        }
        else
        {
            builder.Services.AddKeyedRespire(serviceKey, ResolveOptions);
        }

        var clientBuilder = new AspireRespireClientBuilder(builder, settings, serviceKey);

        if (!settings.DisableHealthChecks)
        {
            builder.Services.AddHealthChecks().AddRespire(name: $"respire_{(serviceKey is null ? "default" : "keyed")}_{connectionName}", tags: ["ready"],
                clientFactory: clientBuilder.GetClient);
        }

        if (!settings.DisableTracing || !settings.DisableMetrics)
        {
            AddTelemetry(builder.Services, settings);
        }

        return clientBuilder;
    }

    /// <summary>Binds global then named integration settings, then applies the named connection string.</summary>
    private static RespireClientSettings ReadSettings(IConfiguration configuration, string connectionName)
    {
        var settings = new RespireClientSettings();
        var root = configuration.GetSection("Aspire:Respire");
        root.Bind(settings);
        root.GetSection(connectionName).Bind(settings);

        if (configuration.GetConnectionString(connectionName) is { } connectionString)
        {
            settings.ConnectionString = connectionString;
        }

        return settings;
    }

    /// <summary>Parses the connection string, then applies global and named option sections.</summary>
    private static RespireOptions BuildOptions(IConfiguration configuration, string connectionName, string? connectionString)
    {
        IConfigurationSection root = configuration.GetSection("Aspire:Respire");
        RespireOptions options = string.IsNullOrWhiteSpace(connectionString)
            ? new RespireOptions()
            : RespireOptions.ParseUnvalidated(connectionString);
        options = RespireConfiguration.Apply(root.GetSection("Options"), options);
        return RespireConfiguration.Apply(root.GetSection(connectionName).GetSection("Options"), options);
    }

    /// <summary>Registers one builder per host and adds each signal when first enabled by a client.</summary>
    private static void AddTelemetry(IServiceCollection services, RespireClientSettings settings)
    {
        var registration = services.FirstOrDefault(static descriptor => descriptor.ServiceType == typeof(TelemetryRegistration))
            ?.ImplementationInstance as TelemetryRegistration;

        if (registration is null)
        {
            registration = new TelemetryRegistration(services.AddOpenTelemetry());
            services.AddSingleton(registration);
        }

        if (!settings.DisableTracing && !registration.Tracing)
        {
            registration.Builder.WithTracing(tracing => tracing.AddSource("Respire"));
            registration.Tracing = true;
        }

        if (!settings.DisableMetrics && !registration.Metrics)
        {
            registration.Builder.WithMetrics(metrics => metrics.AddMeter("Respire"));
            registration.Metrics = true;
        }
    }

    /// <summary>Retains registration-time telemetry state in the host's own service collection.</summary>
    private sealed class TelemetryRegistration(OpenTelemetryBuilder builder)
    {
        internal OpenTelemetryBuilder Builder
        {
            get;
        } = builder;

        internal bool Tracing
        {
            get; set;
        }

        internal bool Metrics
        {
            get; set;
        }
    }

    /// <summary>Reports missing endpoints without opening a connection or invoking a deferred callback.</summary>
    private static void ValidateEndpoints(RespireOptions options, string connectionName)
    {
        if (options.Endpoints.Count == 0)
        {
            throw new InvalidOperationException($"No endpoints are configured for Respire connection '{connectionName}'. " +
                $"Set ConnectionStrings:{connectionName}, Aspire:Respire:Options:Endpoints, or configureOptions.");
        }
    }
}
