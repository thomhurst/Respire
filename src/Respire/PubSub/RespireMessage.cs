using System.Diagnostics.CodeAnalysis;
using Respire.Serialization;

namespace Respire;

/// <summary>
/// One pub/sub message or delivery-gap marker. Message channel, pattern, and payload bytes
/// are owned and remain valid after enumeration moves on. Exact-channel messages may share immutable subscription storage.
/// </summary>
public readonly struct RespireMessage
{
    private readonly IRespireSerializer? _serializer;

    internal RespireMessage(RespireChannel channel, RespireChannel? pattern, ReadOnlyMemory<byte> payload, IRespireSerializer serializer)
    {
        Channel = channel;
        Pattern = pattern;
        Payload = payload;
        _serializer = serializer;
    }

    internal RespireMessage(RespireSubscriptionGap gap)
    {
        Kind = RespireMessageKind.Gap;
        Gap = gap;
    }

    /// <summary>Whether this item is published data or a delivery gap.</summary>
    public RespireMessageKind Kind { get; }

    /// <summary>Gap details when Kind is Gap; otherwise null.</summary>
    public RespireSubscriptionGap? Gap { get; }

    /// <summary>The published channel; empty for a gap marker. Use ToString() for UTF-8 display.</summary>
    public RespireChannel Channel { get; }

    /// <summary>The glob pattern that matched, for pattern subscriptions; otherwise null.</summary>
    public RespireChannel? Pattern { get; }

    /// <summary>The raw message payload as owned memory; empty for a gap marker.</summary>
    public ReadOnlyMemory<byte> Payload { get; }

    /// <summary>The payload decoded as UTF-8; empty for a gap marker.</summary>
    public string Text => Internal.Utf8String.GetString(Payload);

    /// <summary>
    /// The typed payload; strings, bytes, characters (including nullable characters), Boolean
    /// values, and numbers bypass the serializer.
    /// </summary>
    /// <exception cref="InvalidOperationException">This item is a delivery-gap marker.</exception>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public T? As<T>()
    {
        if (Kind == RespireMessageKind.Gap)
        {
            throw new InvalidOperationException("A delivery gap has no message payload. Check Kind before deserializing.");
        }

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

        return _serializer!.Deserialize<T>(Payload.Span);
    }

    /// <summary>Parses a keyspace, keyevent, or Redis 8.8 subkey notification without allocating.</summary>
    /// <remarks>Unknown event names remain lossless. Ordinary messages and malformed frames return false.</remarks>
    public bool TryParseKeyNotification(out RespireKeyNotification notification)
        => KeyNotificationParser.TryParse(in this, out notification);

    /// <summary>Parses a notification, filters its physical key, and strips the matched prefix.</summary>
    /// <remarks>Channel and RawValue retain the original bytes. Client key prefixes are never applied implicitly.</remarks>
    public bool TryParseKeyNotification(ReadOnlySpan<byte> requiredKeyPrefix, out RespireKeyNotification notification)
    {
        if (TryParseKeyNotification(out notification) && notification.KeyStartsWith(requiredKeyPrefix))
        {
            notification = notification.StripPrefix(requiredKeyPrefix.Length);
            return true;
        }
        notification = default;
        return false;
    }

    /// <inheritdoc/>
    public override string ToString() => Kind == RespireMessageKind.Gap ? $"Gap: {Gap}" : $"{Channel}: {Text}";
}
