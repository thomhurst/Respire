using Microsoft.Extensions.DependencyInjection;

namespace Respire.Serialization.MemoryPack;

/// <summary>Registers the MemoryPack serializer for use by Respire options factories.</summary>
public static class RespireMemoryPackServiceCollectionExtensions
{
    /// <summary>Registers a singleton serializer. Resolve it when configuring RespireOptions.Serializer.</summary>
    public static IServiceCollection AddRespireMemoryPackSerializer(this IServiceCollection services, global::MemoryPack.MemoryPackSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IRespireSerializer>(new RespireMemoryPackSerializer(options));
        return services;
    }
}
