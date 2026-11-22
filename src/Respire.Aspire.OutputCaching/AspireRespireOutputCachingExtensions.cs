using global::Respire.OutputCaching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Respire.Aspire;

namespace Microsoft.Extensions.Hosting;

/// <summary>Output caching on an existing Aspire Respire client.</summary>
public static class AspireRespireOutputCachingExtensions
{
    /// <summary>Registers the output-cache store and tag cleanup with this registration's existing client.</summary>
    /// <remarks>Repeated calls use the client and configuration callback from the last call.</remarks>
    public static AspireRespireClientBuilder AddOutputCache(this AspireRespireClientBuilder builder,
        Action<RespireOutputCacheOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        IServiceCollection services = builder.HostBuilder.Services;
        ServiceDescriptor? previousOptions = services.FirstOrDefault(static descriptor =>
            !descriptor.IsKeyedService && descriptor.ImplementationInstance is AspireOutputCacheOptionsSetup);

        if (previousOptions is not null)
        {
            services.Remove(previousOptions);
        }

        if (configure is not null)
        {
            services.AddSingleton<IConfigureOptions<RespireOutputCacheOptions>>(
                new AspireOutputCacheOptionsSetup(configure));
        }

        services.AddRespireOutputCache(configure: null, clientFactory: builder.GetClient);
        return builder;
    }

    private sealed class AspireOutputCacheOptionsSetup(Action<RespireOutputCacheOptions> configure)
        : ConfigureOptions<RespireOutputCacheOptions>(configure);
}
