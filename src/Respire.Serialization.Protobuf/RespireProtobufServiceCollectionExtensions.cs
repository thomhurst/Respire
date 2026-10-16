using Microsoft.Extensions.DependencyInjection;

namespace Respire.Serialization.Protobuf;

/// <summary>Registers the Protobuf serializer for use by Respire options factories.</summary>
public static class RespireProtobufServiceCollectionExtensions
{
    /// <summary>Registers a singleton serializer. Resolve it when configuring RespireOptions.Serializer.</summary>
    public static IServiceCollection AddRespireProtobufSerializer(this IServiceCollection services, global::ProtoBuf.Meta.TypeModel? model = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IRespireSerializer>(new RespireProtobufSerializer(model));
        return services;
    }
}
