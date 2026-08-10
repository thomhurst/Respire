using System.Text;
using Respire.Serialization;

namespace Respire;

/// <summary>
/// One pub/sub message. The payload is an owned copy — safe to hold after the enumeration
/// moves on.
/// </summary>
public readonly struct RespireMessage
{
    private readonly IRespireSerializer _serializer;

    internal RespireMessage(string channel, string? pattern, ReadOnlyMemory<byte> payload, IRespireSerializer serializer)
    {
        Channel = channel;
        Pattern = pattern;
        Payload = payload;
        _serializer = serializer;
    }

    /// <summary>The channel the message was published to.</summary>
    public string Channel { get; }

    /// <summary>The glob pattern that matched, for pattern subscriptions; otherwise null.</summary>
    public string? Pattern { get; }

    public ReadOnlyMemory<byte> Payload { get; }

    /// <summary>The payload decoded as UTF-8.</summary>
    public string Text => Internal.Utf8String.GetString(Payload);

    /// <summary>The typed payload; strings, bytes, Boolean values, and numbers bypass the serializer.</summary>
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

    public override string ToString() => $"{Channel}: {Text}";
}
