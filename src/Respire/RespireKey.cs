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

    public bool IsEmpty => _string is null or "" && _bytes.IsEmpty;

    public static implicit operator RespireKey(string key) => new(key);

    public static implicit operator RespireKey(byte[] key) => new(key.AsMemory());

    public static implicit operator RespireKey(ReadOnlyMemory<byte> key) => new(key);

    /// <summary>The key as a command argument.</summary>
    internal RespireValue AsValue()
        => _string is not null ? new RespireValue(_string) : new RespireValue(_bytes);

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

        // Mixed representations compare by UTF-8 content.
        ReadOnlyMemory<byte> mine = _string is not null ? Encoding.UTF8.GetBytes(_string) : _bytes;
        ReadOnlyMemory<byte> theirs = other._string is not null ? Encoding.UTF8.GetBytes(other._string) : other._bytes;
        return mine.Span.SequenceEqual(theirs.Span);
    }

    public override bool Equals(object? obj) => obj is RespireKey other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(ToString());
}
