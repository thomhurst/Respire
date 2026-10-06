using System.Buffers;
using System.Text;
using Respire.Protocol;

namespace Respire;

/// <summary>
/// A Redis key. Implicitly convertible from <see cref="string"/>, <see cref="byte"/> arrays,
/// and <see cref="ReadOnlyMemory{T}"/> so command methods take one parameter type instead of
/// an overload per representation.
/// </summary>
/// <remarks>Binary storage is borrowed, including through prefix views. Keep it unchanged until
/// the command completes unless the receiving API explicitly snapshots or serializes it earlier.
/// A scan key with a split UTF-16 scalar retains its namespace and boundary identity; it is not
/// interchangeable with an unprefixed key containing UTF-8 replacement bytes.</remarks>
public readonly struct RespireKey : IEquatable<RespireKey>
{
    private readonly string? _string;
    private readonly ReadOnlyMemory<byte> _bytes;
    private readonly Internal.KeyPrefix? _prefix;

    internal RespireKey(Internal.KeyPrefix prefix, string? text, ReadOnlyMemory<byte> bytes)
    {
        _prefix = prefix;
        _string = text;
        _bytes = bytes;
    }

    /// <summary>Creates a UTF-8 Redis key.</summary>
    public RespireKey(string key) => _string = key ?? throw new ArgumentNullException(nameof(key));

    /// <summary>Creates a binary-safe Redis key.</summary>
    public RespireKey(ReadOnlyMemory<byte> key) => _bytes = key;

    /// <summary>An empty Redis key. The default value.</summary>
    public static readonly RespireKey Empty;

    /// <summary>Whether this key has zero bytes.</summary>
    public bool IsEmpty => _prefix is null && _string is null or "" && _bytes.IsEmpty;

    /// <summary>The Redis Cluster hash slot for this key, including {...} hash-tag semantics.</summary>
    public int ClusterSlot
    {
        get
        {
            if (_prefix is not null) return AsValue().GetPrefixedClusterSlot();
            return _string is not null ? Internal.ClusterHash.GetSlot(_string) : Internal.ClusterHash.GetSlot(_bytes.Span);
        }
    }

    /// <summary>Converts text to a UTF-8 Redis key.</summary>
    public static implicit operator RespireKey(string key) => new(key);

    /// <summary>Converts a byte array to a binary-safe Redis key.</summary>
    public static implicit operator RespireKey(byte[] key) => new(key.AsMemory());

    /// <summary>Converts read-only bytes to a binary-safe Redis key.</summary>
    public static implicit operator RespireKey(ReadOnlyMemory<byte> key) => new(key);

    /// <summary>Tests two keys for equality, preserving owned scan-boundary identity.</summary>
    public static bool operator ==(RespireKey left, RespireKey right) => left.Equals(right);

    /// <summary>Tests two keys for inequality, preserving owned scan-boundary identity.</summary>
    public static bool operator !=(RespireKey left, RespireKey right) => !left.Equals(right);

    /// <summary>The key as a command argument.</summary>
    internal RespireValue AsValue()
    {
        if (_prefix is not null) return RespireValue.Prefixed(_prefix, _string, _bytes);
        return _string is not null ? new RespireValue(_string) : new RespireValue(_bytes);
    }

    /// <summary>Returns a key whose storage cannot be changed by the original caller.</summary>
    internal RespireKey Snapshot()
    {
        if (_string is not null) return this;
        var bytes = _bytes.ToArray();
        return _prefix is not null ? new RespireKey(_prefix, null, bytes) : new RespireKey(bytes);
    }

    /// <summary>Owns an already-prefixed binary suffix while retaining ordinary keys' borrowing contract.</summary>
    internal RespireKey SnapshotIfPrefixed() => _prefix is null ? this : Snapshot();

    internal byte[] ToBytes()
    {
        if (_prefix is null) return _string is null ? _bytes.ToArray() : Encoding.UTF8.GetBytes(_string);
        return _prefix.Materialize(_string, _bytes);
    }

    /// <summary>Returns text only when every component originated as text.</summary>
    internal string? Text
    {
        get
        {
            if (_prefix is null) return _string;
            return _prefix.Text is not null && _string is not null ? _prefix.Text + _string : null;
        }
    }

    internal int WireLength
    {
        get
        {
            if (_prefix is not null) return _prefix.GetWireLength(_string, _bytes);
            return _string is not null ? Encoding.UTF8.GetByteCount(_string) : _bytes.Length;
        }
    }

    internal bool StartsWithAny(Internal.ClientCachePrefixSet prefixes)
    {
        if (_prefix is null && _string is null) return prefixes.Matches(_bytes.Span);
        var length = WireLength;
        byte[]? rented = null;
        Span<byte> encoded = length <= 256 ? stackalloc byte[length] : (rented = ArrayPool<byte>.Shared.Rent(length));
        try
        {
            var written = AsValue().WriteWirePayload(encoded);
            return prefixes.Matches(encoded[..written]);
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Returns a copy of this key with <paramref name="prefix"/> prepended.</summary>
    internal RespireKey Prepend(Internal.KeyPrefix prefix, bool snapshotBinaryKeys = false)
    {
        if (_prefix is not null && _prefix.TryComposeScanKey(prefix, _bytes, snapshotBinaryKeys, out var scanKey))
            return scanKey;
        // Reapplying a prefix to an already resolved key preserves the original text/binary semantics.
        // ToBytes owns a fresh snapshot; copying that storage again would allocate unnecessarily.
        if (_prefix is not null)
            return (_string is not null && _prefix.Text is not null ? new RespireKey(ToString()) : new RespireKey(ToBytes()))
                .Prepend(prefix, snapshotBinaryKeys: false);
        return new RespireKey(prefix, _string,
            _string is null && snapshotBinaryKeys ? _bytes.ToArray() : _bytes);
    }

    /// <summary>Resolves a command argument without copying through an intermediate prefixed key.</summary>
    internal RespireValue PrependAsValue(Internal.KeyPrefix prefix, bool snapshotBinaryKeys)
    {
        if (_prefix is not null) return Prepend(prefix, snapshotBinaryKeys).AsValue();
        return RespireValue.Prefixed(prefix, _string,
            _string is null && snapshotBinaryKeys ? _bytes.ToArray() : _bytes);
    }

    internal void WriteTo(ref RespWriter writer)
    {
        if (_prefix is not null)
        {
            writer.WritePrefixedKey(_prefix, _string, _bytes);
        }
        else if (_string is not null)
        {
            writer.WriteBulkString(_string);
        }
        else
        {
            writer.WriteBulkString(_bytes.Span);
        }
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        if (_prefix is null) return _string ?? Internal.Utf8String.GetString(_bytes);
        return _prefix.GetString(_string, _bytes);
    }

    /// <inheritdoc/>
    public bool Equals(RespireKey other)
    {
        var owner = _prefix?.ScanOwnerPrefix;
        var otherOwner = other._prefix?.ScanOwnerPrefix;
        if (owner is not null || otherOwner is not null)
            return owner is not null && otherOwner is not null
                && StringComparer.Ordinal.Equals(owner, otherOwner)
                && StringComparer.Ordinal.Equals(_prefix!.Text, other._prefix!.Text)
                && _bytes.Span.SequenceEqual(other._bytes.Span);
        return AsValue().Equals(other.AsValue());
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is RespireKey other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        if (_prefix?.ScanOwnerPrefix is { } owner)
            return HashCode.Combine(owner, _prefix.Text, new RespireValue(_bytes).GetHashCode());
        return AsValue().GetHashCode();
    }
}
