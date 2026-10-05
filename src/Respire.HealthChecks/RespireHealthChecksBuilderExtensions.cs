using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Respire.HealthChecks;

/// <summary>Registers health checks without registering or creating a Redis client.</summary>
public static class RespireHealthChecksBuilderExtensions
{
    /// <summary>Checks the registered IRespireClient, or an existing client returned by the supplied factory.</summary>
    public static IHealthChecksBuilder AddRespire(
        this IHealthChecksBuilder builder,
        RespireHealthCheckOptions? options = null,
        string name = "respire",
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null,
        Func<IServiceProvider, IRespireClient>? clientFactory = null)
        => Add(builder, options, name, failureStatus, tags, clientFactory,
            static (client, settings) => new RespireHealthCheck(client, settings));

    /// <summary>Checks the registered failover group, including its candidate state and currently selected client.</summary>
    public static IHealthChecksBuilder AddRespireFailoverGroup(
        this IHealthChecksBuilder builder,
        RespireHealthCheckOptions? options = null,
        string name = "respire-failover",
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null,
        Func<IServiceProvider, RespireFailoverGroup>? groupFactory = null)
        => Add(builder, options, name, failureStatus, tags, groupFactory,
            static (group, settings) => new RespireHealthCheck(group, settings));

    private static IHealthChecksBuilder Add<TClient>(
        IHealthChecksBuilder builder,
        RespireHealthCheckOptions? options,
        string name,
        HealthStatus? failureStatus,
        IEnumerable<string>? tags,
        Func<IServiceProvider, TClient>? clientFactory,
        Func<TClient, RespireHealthCheckOptions, RespireHealthCheck> createCheck)
        where TClient : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        var settings = options ?? new();
        settings.Validate();
        return builder.Add(new HealthCheckRegistration(name,
            provider => createCheck(clientFactory is null
                ? provider.GetRequiredService<TClient>() : clientFactory(provider), settings),
            failureStatus, tags));
    }
}
