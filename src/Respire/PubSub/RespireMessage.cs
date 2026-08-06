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
    public string Text => Encoding.UTF8.GetString(Payload.Span);

    /// <summary>The payload deserialized via the client's serializer.</summary>
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

        return _serializer.Deserialize<T>(Payload.Span);
    }

    public override string ToString() => $"{Channel}: {Text}";
}
