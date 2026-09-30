using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Respire.Compression;

namespace Respire.Serialization;

/// <summary>Opt-in codec framing beneath an existing typed-value serializer.</summary>
/// <remarks>Configure this as RespireOptions.Serializer. Existing primitive and raw-value fast paths
/// still bypass serialization. Both components must support concurrent calls. Codec output is owned.</remarks>
public sealed class RespireValueCodecSerializer : IRespireSerializer
{
    private readonly IRespireSerializer _serializer;
    private readonly IRespireValueCodec _codec;

    /// <summary>Decorates the supplied serializer with the supplied codec without changing raw command arguments.</summary>
    public RespireValueCodecSerializer(IRespireSerializer serializer, IRespireValueCodec codec)
    {
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentNullException.ThrowIfNull(codec);
        _serializer = serializer;
        _codec = codec;
    }

    /// <inheritdoc/>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public void Serialize<T>(IBufferWriter<byte> destination, T value)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var buffer = new ArrayBufferWriter<byte>();
        _serializer.Serialize(buffer, value);
        _codec.Encode(buffer.WrittenSpan, destination);
    }

    /// <inheritdoc/>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public T? Deserialize<T>(ReadOnlySpan<byte> payload) => _serializer.Deserialize<T>(_codec.Decode(payload));

    /// <inheritdoc/>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public void Serialize(IBufferWriter<byte> destination, Type type, object? value)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(type);
        var buffer = new ArrayBufferWriter<byte>();
        _serializer.Serialize(buffer, type, value);
        _codec.Encode(buffer.WrittenSpan, destination);
    }

    /// <inheritdoc/>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public object? Deserialize(Type type, ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(type);
        return _serializer.Deserialize(type, _codec.Decode(payload));
    }
}
