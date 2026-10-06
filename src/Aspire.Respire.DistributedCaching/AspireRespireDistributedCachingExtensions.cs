using Aspire.Respire;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using global::Respire.Caching;

namespace Microsoft.Extensions.Hosting;

/// <summary>Distributed caching on an existing Aspire Respire client.</summary>
public static class AspireRespireDistributedCachingExtensions
{
    /// <summary>Registers the selected client as the distributed cache backend without transferring client ownership.</summary>
    /// <remarks>The cache is an unkeyed application service. Choose one backend per host;
    /// if registered repeatedly, resolving a single cache selects the last registration.</remarks>
    public static AspireRespireClientBuilder AddDistributedCache(this AspireRespireClientBuilder builder,
        Action<RespireCacheOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new RespireCacheOptions();
        configure?.Invoke(options);
        builder.HostBuilder.Services.AddSingleton<IDistributedCache>(services =>
            new RespireDistributedCache(builder.GetClient(services), options));
        return builder;
    }
}
