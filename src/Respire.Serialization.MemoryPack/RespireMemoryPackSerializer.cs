using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using MemoryPack;

namespace Respire.Serialization.MemoryPack;

/// <summary>Serializes values using MemoryPack's generated formatters.</summary>
public sealed class RespireMemoryPackSerializer : IRespireSerializer
{
    private readonly MemoryPackSerializerOptions? _options;
    private const string Trimming = "Generate and preserve MemoryPack formatters for every serialized type.";
    private const string DynamicCode = "Runtime Type dispatch may require dynamic code; use generated formatters and generic calls for AOT.";

    /// <summary>Creates a serializer with the supplied options, or MemoryPack defaults.</summary>
    public RespireMemoryPackSerializer(MemoryPackSerializerOptions? options = null) => _options = options;

    /// <inheritdoc/>
    [RequiresUnreferencedCode(Trimming), RequiresDynamicCode(DynamicCode)]
    public void Serialize<T>(IBufferWriter<byte> destination, T value)
    {
        ArgumentNullException.ThrowIfNull(destination);
        RuntimeHelpers.RunClassConstructor(typeof(T).TypeHandle);
        MemoryPackSerializer.Serialize<T, IBufferWriter<byte>>(destination, value, _options);
    }

    /// <inheritdoc/>
    [RequiresUnreferencedCode(Trimming), RequiresDynamicCode(DynamicCode)]
    public T? Deserialize<T>(ReadOnlySpan<byte> payload)
    {
        RuntimeHelpers.RunClassConstructor(typeof(T).TypeHandle);
        return MemoryPackSerializer.Deserialize<T>(payload, _options);
    }

    /// <inheritdoc/>
    [RequiresUnreferencedCode(Trimming), RequiresDynamicCode(DynamicCode)]
    public void Serialize(IBufferWriter<byte> destination, Type type, object? value)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(type);
        // Generated models register their formatter in the type initializer. Wait for
        // that initializer before MemoryPack consults its separately initialized cache.
        // This also covers null first writes, which never construct a model instance.
        RuntimeHelpers.RunClassConstructor(type.TypeHandle);
        MemoryPackSerializer.Serialize(type, destination, value, _options);
    }

    /// <inheritdoc/>
    [RequiresUnreferencedCode(Trimming), RequiresDynamicCode(DynamicCode)]
    public object? Deserialize(Type type, ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(type);
        RuntimeHelpers.RunClassConstructor(type.TypeHandle);
        return MemoryPackSerializer.Deserialize(type, payload, _options);
    }
}
