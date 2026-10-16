using Microsoft.Extensions.DependencyInjection;

namespace Respire.Serialization.MessagePack;

/// <summary>Registers the MessagePack serializer for use by Respire options factories.</summary>
public static class RespireMessagePackServiceCollectionExtensions
{
    /// <summary>Registers a singleton serializer. Resolve it when configuring RespireOptions.Serializer.</summary>
    public static IServiceCollection AddRespireMessagePackSerializer(this IServiceCollection services, global::MessagePack.MessagePackSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IRespireSerializer>(new RespireMessagePackSerializer(options));
        return services;
    }
}
