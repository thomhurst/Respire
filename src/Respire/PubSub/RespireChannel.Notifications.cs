using System.Globalization;
using System.Text;
using Respire.Internal;

namespace Respire;

/// <summary>The explicit Redis Cluster routing scope of a channel descriptor.</summary>
public enum RespireChannelRoutingScope
{
    /// <summary>Ordinary global Pub/Sub, with no notification semantics.</summary>
    Global,
    /// <summary>A notification emitted by the primary owning one physical key.</summary>
    KeyOwner,
    /// <summary>A notification that requires coverage of every primary.</summary>
    AllPrimaries,
}

public readonly partial struct RespireChannel
{
    /// <summary>All events for one physical key in an explicit database.</summary>
    public static RespireChannel KeySpaceSingleKey(RespireKey key, int database)
        => KeyNotification("keyspace", key, database);
    /// <summary>Events for a physical Redis glob pattern; null database matches all databases.</summary>
    public static RespireChannel KeySpacePattern(RespireKey pattern, int? database = null)
        => CreateNotification("keyspace", pattern.ToBytes(), database, pattern: true);
    /// <summary>Events for a literal physical prefix; glob metacharacters in the prefix are escaped.</summary>
    public static RespireChannel KeySpacePrefix(RespireKey prefix, int? database = null)
        => CreateNotification("keyspace", PrefixPattern(prefix), database, pattern: true);
    /// <summary>Keys affected by one known event; null database matches all databases.</summary>
    public static RespireChannel KeyEvent(RespireKeyNotificationType type, int? database = null)
        => KeyEvent(KeyNotificationTypes.Format(type), database);
    /// <summary>Keys affected by a raw event name, including future/module events.</summary>
    public static RespireChannel KeyEvent(ReadOnlySpan<byte> type, int? database = null)
        => EventNotification("keyevent", type, database);

    /// <summary>Redis 8.8 events and subkeys for one physical key.</summary>
    public static RespireChannel SubKeySpaceSingleKey(RespireKey key, int database)
        => KeyNotification("subkeyspace", key, database);
    /// <summary>Redis 8.8 events and subkeys matching a physical Redis glob pattern.</summary>
    public static RespireChannel SubKeySpacePattern(RespireKey pattern, int? database = null)
        => CreateNotification("subkeyspace", pattern.ToBytes(), database, pattern: true);
    /// <summary>Redis 8.8 events and subkeys under a literal physical prefix.</summary>
    public static RespireChannel SubKeySpacePrefix(RespireKey prefix, int? database = null)
        => CreateNotification("subkeyspace", PrefixPattern(prefix), database, pattern: true);
    /// <summary>Redis 8.8 events for one physical key/subkey pair. Keys containing newline are unsupported by Redis.</summary>
    public static RespireChannel SubKeySpaceItem(RespireKey key, RespireKey subkey, int database)
    {
        var keyBytes = key.ToBytes();
        if (keyBytes.AsSpan().Contains((byte)'\n'))
            throw new ArgumentException("Redis does not emit item notifications for keys containing newline.", nameof(key));
        return CreateNotification("subkeyspaceitem", Join(keyBytes, (byte)'\n', subkey.ToBytes()), database,
            pattern: false, ClusterHash.GetSlot(keyBytes));
    }
    /// <summary>Redis 8.8 keys and subkeys affected by one known event.</summary>
    public static RespireChannel SubKeyEvent(RespireKeyNotificationType type, int? database = null)
        => SubKeyEvent(KeyNotificationTypes.Format(type), database);
    /// <summary>Redis 8.8 keys and subkeys affected by a raw event name.</summary>
    public static RespireChannel SubKeyEvent(ReadOnlySpan<byte> type, int? database = null)
        => EventNotification("subkeyevent", type, database);
    /// <summary>Redis 8.8 subkeys affected by one event on one physical key.</summary>
    public static RespireChannel SubKeySpaceEvent(RespireKeyNotificationType type, RespireKey key, int? database = null)
        => SubKeySpaceEvent(KeyNotificationTypes.Format(type), key, database);
    /// <summary>Redis 8.8 subkeys for a raw event and physical key. Event names cannot contain '|'.</summary>
    public static RespireChannel SubKeySpaceEvent(ReadOnlySpan<byte> type, RespireKey key, int? database = null)
    {
        ValidateEvent(type);
        if (type.Contains((byte)'|')) throw new ArgumentException("The event name cannot contain '|'.", nameof(type));
        var keyBytes = key.ToBytes();
        var tail = Join(type, (byte)'|', keyBytes);
        return CreateNotification("subkeyspaceevent", database is null ? EscapePattern(tail) : tail, database,
            pattern: database is null, ClusterHash.GetSlot(keyBytes));
    }

    private static RespireChannel KeyNotification(string family, RespireKey key, int database)
    {
        var bytes = key.ToBytes();
        return CreateNotification(family, bytes, database, pattern: false, ClusterHash.GetSlot(bytes));
    }
    private static RespireChannel EventNotification(string family, ReadOnlySpan<byte> type, int? database)
    {
        ValidateEvent(type);
        return CreateNotification(family, database is null ? EscapePattern(type) : type, database, pattern: database is null);
    }
    private static void ValidateEvent(ReadOnlySpan<byte> type)
    {
        if (type.IsEmpty || type.Contains((byte)0))
            throw new ArgumentException("A Redis event name must be nonempty and cannot contain NUL.", nameof(type));
    }
    private static RespireChannel CreateNotification(string family, ReadOnlySpan<byte> tail, int? database, bool pattern, int? slot = null)
    {
        if (database < 0) throw new ArgumentOutOfRangeException(nameof(database));
        var header = Encoding.ASCII.GetBytes($"__{family}@{database?.ToString(CultureInfo.InvariantCulture) ?? "*"}__:");
        var bytes = new byte[checked(header.Length + tail.Length)];
        header.CopyTo(bytes, 0);
        tail.CopyTo(bytes.AsSpan(header.Length));
        return new(bytes, pattern ? SubscriptionKind.Pattern : SubscriptionKind.Channel,
            slot is null ? RespireChannelRoutingScope.AllPrimaries : RespireChannelRoutingScope.KeyOwner, database, slot);
    }
    private static byte[] Join(ReadOnlySpan<byte> first, byte separator, ReadOnlySpan<byte> second)
    {
        var bytes = new byte[checked(first.Length + 1 + second.Length)];
        first.CopyTo(bytes);
        bytes[first.Length] = separator;
        second.CopyTo(bytes.AsSpan(first.Length + 1));
        return bytes;
    }
    private static byte[] PrefixPattern(RespireKey key)
    {
        var escaped = EscapePattern(key.ToBytes());
        Array.Resize(ref escaped, escaped.Length + 1);
        escaped[^1] = (byte)'*';
        return escaped;
    }
    private static byte[] EscapePattern(ReadOnlySpan<byte> bytes)
    {
        var extra = 0;
        foreach (var value in bytes) if (IsGlob(value)) extra++;
        var result = new byte[checked(bytes.Length + extra)];
        var index = 0;
        foreach (var value in bytes)
        {
            if (IsGlob(value)) result[index++] = (byte)'\\';
            result[index++] = value;
        }
        return result;
    }
    private static bool IsGlob(byte value) => value is (byte)'*' or (byte)'?' or (byte)'[' or (byte)']' or (byte)'\\';
}
