using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using global::Respire;

namespace Aspire.Respire;

/// <summary>A registered Respire client and its host context, used by the companion cache integrations.</summary>
public sealed class AspireRespireClientBuilder
{
    internal AspireRespireClientBuilder(IHostApplicationBuilder hostBuilder, RespireClientSettings settings, string? serviceKey)
    {
        HostBuilder = hostBuilder;
        Settings = settings;
        ServiceKey = serviceKey;
    }

    /// <summary>The host that owns the client.</summary>
    public IHostApplicationBuilder HostBuilder { get; }

    /// <summary>Settings applied during registration. Configure flags through the registration callback.</summary>
    public RespireClientSettings Settings { get; }

    /// <summary>The keyed service name, or null for the default client.</summary>
    public string? ServiceKey { get; }

    /// <summary>Resolves this registration's existing client. Cache helpers do not own or dispose that client.</summary>
    public IRespireClient GetClient(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return ServiceKey is null
            ? services.GetRequiredService<IRespireClient>()
            : services.GetRequiredKeyedService<IRespireClient>(ServiceKey);
    }
}
