using System.Text;
using Respire.Protocol;

namespace Respire;

/// <summary>
/// A Redis key. Implicitly convertible from <see cref="string"/>, <see cref="byte"/> arrays,
/// and <see cref="ReadOnlyMemory{T}"/> so command methods take one parameter type instead of
/// an overload per representation.
/// </summary>
public readonly struct RespireKey : IEquatable<RespireKey>
{
    private readonly string? _string;
    private readonly ReadOnlyMemory<byte> _bytes;

    /// <summary>Creates a UTF-8 Redis key.</summary>
    public RespireKey(string key) => _string = key ?? throw new ArgumentNullException(nameof(key));

    /// <summary>Creates a binary-safe Redis key.</summary>
    public RespireKey(ReadOnlyMemory<byte> key) => _bytes = key;

    /// <summary>An empty Redis key. The default value.</summary>
    public static readonly RespireKey Empty = default;

    /// <summary>Whether this key has zero bytes.</summary>
    public bool IsEmpty => _string is null or "" && _bytes.IsEmpty;

    /// <summary>The Redis Cluster hash slot for this key, including {...} hash-tag semantics.</summary>
    public int ClusterSlot => _string is not null
        ? Internal.ClusterHash.GetSlot(_string)
        : Internal.ClusterHash.GetSlot(_bytes.Span);

    /// <summary>Converts text to a UTF-8 Redis key.</summary>
    public static implicit operator RespireKey(string key) => new(key);

    /// <summary>Converts a byte array to a binary-safe Redis key.</summary>
    public static implicit operator RespireKey(byte[] key) => new(key.AsMemory());

    /// <summary>Converts read-only bytes to a binary-safe Redis key.</summary>
    public static implicit operator RespireKey(ReadOnlyMemory<byte> key) => new(key);

    /// <summary>Tests two keys for byte equality.</summary>
    public static bool operator ==(RespireKey left, RespireKey right) => left.Equals(right);

    /// <summary>Tests two keys for byte inequality.</summary>
    public static bool operator !=(RespireKey left, RespireKey right) => !left.Equals(right);

    /// <summary>The key as a command argument.</summary>
    internal RespireValue AsValue()
        => _string is not null ? new RespireValue(_string) : new RespireValue(_bytes);

    /// <summary>Returns a key whose storage cannot be changed by the original caller.</summary>
    internal RespireKey Snapshot()
        => _string is not null ? this : new RespireKey(_bytes.ToArray());

    internal int WireLength => _string is not null
        ? Encoding.UTF8.GetByteCount(_string)
        : _bytes.Length;

    /// <summary>Returns a copy of this key with <paramref name="prefix"/> prepended.</summary>
    internal RespireKey Prepend(string prefix)
    {
        if (_string is not null)
        {
            return new RespireKey(prefix + _string);
        }

        var prefixByteCount = Encoding.UTF8.GetByteCount(prefix);
        var combined = new byte[prefixByteCount + _bytes.Length];
        Encoding.UTF8.GetBytes(prefix, combined);
        _bytes.Span.CopyTo(combined.AsSpan(prefixByteCount));
        return new RespireKey(combined);
    }

    internal void WriteTo(ref RespWriter writer)
    {
        if (_string is not null)
        {
            writer.WriteBulkString(_string);
        }
        else
        {
            writer.WriteBulkString(_bytes.Span);
        }
    }

    /// <inheritdoc/>
    public override string ToString() => _string ?? Internal.Utf8String.GetString(_bytes);

    /// <inheritdoc/>
    public bool Equals(RespireKey other) => AsValue().Equals(other.AsValue());

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is RespireKey other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => AsValue().GetHashCode();
}
