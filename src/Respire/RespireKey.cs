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

    public RespireKey(string key) => _string = key ?? throw new ArgumentNullException(nameof(key));

    public RespireKey(ReadOnlyMemory<byte> key) => _bytes = key;

    /// <summary>An empty Redis key. The default value.</summary>
    public static readonly RespireKey Empty = default;

    public bool IsEmpty => _string is null or "" && _bytes.IsEmpty;

    /// <summary>The Redis Cluster hash slot for this key, including {...} hash-tag semantics.</summary>
    public int ClusterSlot => _string is not null
        ? Internal.ClusterHash.GetSlot(_string)
        : Internal.ClusterHash.GetSlot(_bytes.Span);

    public static implicit operator RespireKey(string key) => new(key);

    public static implicit operator RespireKey(byte[] key) => new(key.AsMemory());

    public static implicit operator RespireKey(ReadOnlyMemory<byte> key) => new(key);

    public static bool operator ==(RespireKey left, RespireKey right) => left.Equals(right);

    public static bool operator !=(RespireKey left, RespireKey right) => !left.Equals(right);

    /// <summary>The key as a command argument.</summary>
    internal RespireValue AsValue()
        => _string is not null ? new RespireValue(_string) : new RespireValue(_bytes);

    /// <summary>Returns a key whose storage cannot be changed by the original caller.</summary>
    internal RespireKey Snapshot()
        => _string is not null ? this : new RespireKey(_bytes.ToArray());

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

    public override string ToString() => _string ?? Encoding.UTF8.GetString(_bytes.Span);

    public bool Equals(RespireKey other) => AsValue().Equals(other.AsValue());

    public override bool Equals(object? obj) => obj is RespireKey other && Equals(other);

    public override int GetHashCode() => AsValue().GetHashCode();
}
