using System.Diagnostics.CodeAnalysis;
using Respire.Serialization;

namespace Respire;

/// <summary>
/// One pub/sub message. Channel, pattern, and payload bytes are owned and remain valid after
/// enumeration moves on. Exact-channel messages may share immutable subscription storage.
/// </summary>
public readonly struct RespireMessage
{
    private readonly IRespireSerializer _serializer;

    internal RespireMessage(RespireChannel channel, RespireChannel? pattern, ReadOnlyMemory<byte> payload, IRespireSerializer serializer)
    {
        Channel = channel;
        Pattern = pattern;
        Payload = payload;
        _serializer = serializer;
    }

    /// <summary>The exact channel the message was published to. Use ToString() for UTF-8 display.</summary>
    public RespireChannel Channel { get; }

    /// <summary>The glob pattern that matched, for pattern subscriptions; otherwise null.</summary>
    public RespireChannel? Pattern { get; }

    /// <summary>The raw message payload as owned memory.</summary>
    public ReadOnlyMemory<byte> Payload { get; }

    /// <summary>The payload decoded as UTF-8.</summary>
    public string Text => Internal.Utf8String.GetString(Payload);

    /// <summary>
    /// The typed payload; strings, bytes, characters (including nullable characters), Boolean
    /// values, and numbers bypass the serializer.
    /// </summary>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public T? As<T>()
    {
        if (typeof(T) == typeof(string))
        {
            return (T)(object)Text;
        }

        if (typeof(T) == typeof(byte[]))
        {
            return (T)(object)Payload.ToArray();
        }

        if (PrimitiveCodec.TryDeserialize<T>(Payload.Span, out var primitive))
        {
            return primitive;
        }

        return _serializer.Deserialize<T>(Payload.Span);
    }

    /// <inheritdoc/>
    public override string ToString() => $"{Channel}: {Text}";
}
