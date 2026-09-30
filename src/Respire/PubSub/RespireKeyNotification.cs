namespace Respire;

/// <summary>The channel layout used by a Redis key notification.</summary>
public enum RespireKeyNotificationKind
{
    /// <summary>Not a recognized notification layout.</summary>
    Unknown,
    /// <summary>A key's event, with the key in the channel.</summary>
    KeySpace,
    /// <summary>An event's key, with the event in the channel.</summary>
    KeyEvent,
    /// <summary>A key's event and affected subkeys.</summary>
    SubKeySpace,
    /// <summary>An event's key and affected subkeys.</summary>
    SubKeyEvent,
    /// <summary>An event for one key/subkey pair.</summary>
    SubKeySpaceItem,
    /// <summary>Affected subkeys for one event/key pair.</summary>
    SubKeySpaceEvent,
}

/// <summary>
/// A lossless parsed notification. All memory slices retain the owned message storage;
/// the value remains valid after subscription enumeration advances.
/// </summary>
public readonly struct RespireKeyNotification
{
    private readonly ReadOnlyMemory<byte> _key;
    private readonly RespireSubKeyEnumerable _subkeys;

    internal RespireKeyNotification(RespireKeyNotificationKind kind, int database,
        ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> type, RespireChannel channel,
        ReadOnlyMemory<byte> value, RespireSubKeyEnumerable subkeys)
    {
        Kind = kind;
        Database = database;
        _key = key;
        RawType = type;
        Type = KeyNotificationTypes.Parse(type.Span);
        Channel = channel;
        RawValue = value;
        _subkeys = subkeys;
    }

    /// <summary>The notification channel layout.</summary>
    public RespireKeyNotificationKind Kind { get; }
    /// <summary>The known event type, or Unknown for a future/module event.</summary>
    public RespireKeyNotificationType Type { get; }
    /// <summary>The emitting database, independent of the client's selected database.</summary>
    public int Database { get; }
    /// <summary>The physical key, or logical key after explicit prefix filtering.</summary>
    public RespireKey Key => new(_key);
    /// <summary>The raw key bytes, without decoding or copying.</summary>
    public ReadOnlyMemory<byte> KeyBytes => _key;
    /// <summary>Whether this notification contains any subkeys, including empty subkey names.</summary>
    public bool HasSubKey => _subkeys.Count != 0;
    /// <summary>The lossless event name, including unrecognized names.</summary>
    public ReadOnlyMemory<byte> RawType { get; }
    /// <summary>The original physical notification channel.</summary>
    public RespireChannel Channel { get; }
    /// <summary>The complete original message payload.</summary>
    public ReadOnlyMemory<byte> RawValue { get; }
    /// <summary>Enumerates the affected subkeys without allocations.</summary>
    public RespireSubKeyEnumerable GetSubKeys() => _subkeys;
    /// <summary>Tests the exposed key against a binary prefix.</summary>
    public bool KeyStartsWith(ReadOnlySpan<byte> prefix) => _key.Span.StartsWith(prefix);
    /// <summary>Copies the exposed key if the destination fits; otherwise writes nothing.</summary>
    public bool TryCopyKey(Span<byte> destination, out int written)
    {
        written = 0;
        if (!_key.Span.TryCopyTo(destination)) return false;
        written = _key.Length;
        return true;
    }

    internal RespireKeyNotification StripPrefix(int length)
        => new(Kind, Database, _key[length..], RawType, Channel, RawValue, _subkeys);
}
