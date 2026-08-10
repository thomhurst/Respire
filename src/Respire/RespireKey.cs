using System.Buffers;
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
    private const int StackallocThreshold = 256;
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

    public bool Equals(RespireKey other)
    {
        if (_string is not null && other._string is not null)
        {
            return string.Equals(_string, other._string, StringComparison.Ordinal);
        }

        if (_string is not null)
        {
            return Utf8Equals(_string, other._bytes.Span);
        }

        return other._string is not null
            ? Utf8Equals(other._string, _bytes.Span)
            : _bytes.Span.SequenceEqual(other._bytes.Span);
    }

    public override bool Equals(object? obj) => obj is RespireKey other && Equals(other);

    public override int GetHashCode()
    {
        if (_string is not null)
        {
            return StringComparer.Ordinal.GetHashCode(_string);
        }

        var bytes = _bytes.Span;
        char[]? rented = null;
        var chars = bytes.Length <= StackallocThreshold
            ? stackalloc char[bytes.Length]
            : (rented = ArrayPool<char>.Shared.Rent(bytes.Length));

        try
        {
            var status = Ascii.ToUtf16(bytes, chars, out var charsWritten);
            if (status != OperationStatus.Done)
            {
                charsWritten = Encoding.UTF8.GetChars(bytes, chars);
            }

            return string.GetHashCode(chars[..charsWritten], StringComparison.Ordinal);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<char>.Shared.Return(rented, clearArray: true);
            }
        }
    }

    private static bool Utf8Equals(string value, ReadOnlySpan<byte> bytes)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount != bytes.Length)
        {
            return false;
        }

        byte[]? rented = null;
        var encoded = byteCount <= StackallocThreshold
            ? stackalloc byte[byteCount]
            : (rented = ArrayPool<byte>.Shared.Rent(byteCount));

        try
        {
            if (byteCount == value.Length)
            {
                Ascii.FromUtf16(value, encoded, out _);
            }
            else
            {
                Encoding.UTF8.GetBytes(value, encoded);
            }

            return encoded[..byteCount].SequenceEqual(bytes);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented, clearArray: true);
            }
        }
    }
}
