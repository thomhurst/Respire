using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using MessagePack;

namespace Respire.Serialization.MessagePack;

/// <summary>Serializes values using MessagePack, with explicit per-instance options.</summary>
public sealed class RespireMessagePackSerializer : IRespireSerializer
{
    private readonly MessagePackSerializerOptions _options;
    private const string Trimming = "Preserve the serialized types and configure generated MessagePack formatters when trimming.";
    private const string DynamicCode = "Runtime Type dispatch and reflection-based MessagePack resolvers may require dynamic code.";

    /// <summary>Uses standard options with untrusted-data security unless options are supplied.</summary>
    public RespireMessagePackSerializer(MessagePackSerializerOptions? options = null)
        => _options = options ?? MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);

    /// <inheritdoc/>
    [RequiresUnreferencedCode(Trimming), RequiresDynamicCode(DynamicCode)]
    public void Serialize<T>(IBufferWriter<byte> destination, T value)
    {
        ArgumentNullException.ThrowIfNull(destination);
        MessagePackSerializer.Serialize(destination, value, _options);
    }

    /// <inheritdoc/>
    [RequiresUnreferencedCode(Trimming), RequiresDynamicCode(DynamicCode)]
    public T? Deserialize<T>(ReadOnlySpan<byte> payload)
        // MessagePack requires memory, not a span. Owned storage also permits custom
        // formatters to retain slices without observing a returned pool buffer.
        => MessagePackSerializer.Deserialize<T>(payload.ToArray().AsMemory(), _options);

    /// <inheritdoc/>
    [RequiresUnreferencedCode(Trimming), RequiresDynamicCode(DynamicCode)]
    public void Serialize(IBufferWriter<byte> destination, Type type, object? value)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(type);
        MessagePackSerializer.Serialize(type, destination, value, _options);
    }

    /// <inheritdoc/>
    [RequiresUnreferencedCode(Trimming), RequiresDynamicCode(DynamicCode)]
    public object? Deserialize(Type type, ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(type);
        return MessagePackSerializer.Deserialize(type, payload.ToArray().AsMemory(), _options);
    }
}
