using Aspire.Respire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using global::Respire;
using global::Respire.DependencyInjection;
using global::Respire.HealthChecks;

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
        var root = builder.Configuration.GetSection("Aspire:Respire");
        var named = root.GetSection(connectionName);
        var settings = new RespireClientSettings();
        root.Bind(settings);
        named.Bind(settings);
        if (builder.Configuration.GetConnectionString(connectionName) is { } connectionString)
            settings.ConnectionString = connectionString;
        configureSettings?.Invoke(settings);

        var options = string.IsNullOrWhiteSpace(settings.ConnectionString)
            ? new RespireOptions() : RespireOptions.Parse(settings.ConnectionString);
        options = RespireConfiguration.Apply(root.GetSection("Options"), options);
        options = RespireConfiguration.Apply(named.GetSection("Options"), options);
        var disableLogging = settings.DisableLogging;
        if (configureOptions is null) ValidateEndpoints(options, connectionName);

        RespireOptions ResolveOptions(IServiceProvider provider)
        {
            var resolved = configureOptions is null ? options : configureOptions(provider, options)
                ?? throw new InvalidOperationException("The Respire options callback returned null.");
            ValidateEndpoints(resolved, connectionName);
            return disableLogging ? resolved with { LoggerFactory = NullLoggerFactory.Instance } : resolved;
        }

        if (serviceKey is null) builder.Services.AddRespire(ResolveOptions);
        else builder.Services.AddKeyedRespire(serviceKey, ResolveOptions);
        var clientBuilder = new AspireRespireClientBuilder(builder, settings, serviceKey);
        if (!settings.DisableHealthChecks)
            builder.Services.AddHealthChecks().AddRespire(name: $"respire_{(serviceKey is null ? "default" : "keyed")}_{connectionName}", tags: ["ready"],
                clientFactory: clientBuilder.GetClient);
        if (!settings.DisableTracing || !settings.DisableMetrics)
        {
            var telemetry = builder.Services.AddOpenTelemetry();
            if (!settings.DisableTracing) telemetry.WithTracing(tracing => tracing.AddSource("Respire"));
            if (!settings.DisableMetrics) telemetry.WithMetrics(metrics => metrics.AddMeter("Respire"));
        }
        return clientBuilder;
    }

    /// <summary>Reports missing endpoints without opening a connection or invoking a deferred callback.</summary>
    private static void ValidateEndpoints(RespireOptions options, string connectionName)
    {
        if (options.Endpoints.Count == 0)
            throw new InvalidOperationException($"No endpoints are configured for Respire connection '{connectionName}'. " +
                $"Set ConnectionStrings:{connectionName}, Aspire:Respire:Options:Endpoints, or configureOptions.");
    }
}
