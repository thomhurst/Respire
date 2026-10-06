using Aspire.Respire;
using global::Respire.OutputCaching;

namespace Microsoft.Extensions.Hosting;

/// <summary>Output caching on an existing Aspire Respire client.</summary>
public static class AspireRespireOutputCachingExtensions
{
    /// <summary>Registers the output-cache store and tag cleanup with this registration's existing client.</summary>
    public static AspireRespireClientBuilder AddOutputCache(this AspireRespireClientBuilder builder,
        Action<RespireOutputCacheOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.HostBuilder.Services.AddRespireOutputCache(configure, builder.GetClient);
        return builder;
    }
}
