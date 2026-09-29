using System.IO.Hashing;
using System.Security.Cryptography;

namespace Respire.Internal;

/// <summary>Owned byte keys with allocation-free incoming span lookup on every supported TFM.</summary>
internal sealed class ByteRouteDictionary<TValue>
{
#if NET9_0_OR_GREATER
    private readonly Dictionary<ByteRouteKey, TValue> _entries = new(ByteRouteKeyComparer.Instance);
    private readonly Dictionary<ByteRouteKey, TValue>.AlternateLookup<ReadOnlySpan<byte>> _lookup;

    public ByteRouteDictionary() => _lookup = _entries.GetAlternateLookup<ReadOnlySpan<byte>>();
    public IEnumerable<RespireChannel> Names => _entries.Keys.Select(static key => key.Name);
    public IEnumerable<TValue> Values => _entries.Values;
#else
    // A hash bucket keeps span lookup available without decoding or allocating on net8.
    private readonly Dictionary<int, List<(RespireChannel Name, TValue Value)>> _entries = new();
    // Enumeration is used only for reconnect snapshots and client disposal, never message dispatch.
    public IEnumerable<RespireChannel> Names => _entries.Values.SelectMany(static bucket => bucket.Select(static entry => entry.Name));
    public IEnumerable<TValue> Values => _entries.Values.SelectMany(static bucket => bucket.Select(static entry => entry.Value));
#endif

    public bool TryGetValue(RespireChannel name, out TValue value)
        => TryGetValue(name.Span, out _, out value);

    public bool TryGetValue(ReadOnlySpan<byte> bytes, out RespireChannel name, out TValue value)
    {
#if NET9_0_OR_GREATER
        if (_lookup.TryGetValue(bytes, out var actualKey, out value!))
        {
            name = actualKey.Name;
            return true;
        }
#else
        if (_entries.TryGetValue(ByteRouteKeyComparer.Instance.Hash(bytes), out var bucket))
        {
            foreach (var entry in bucket)
            {
                if (bytes.SequenceEqual(entry.Name.Span))
                {
                    name = entry.Name;
                    value = entry.Value;
                    return true;
                }
            }
        }
#endif
        name = default;
        value = default!;
        return false;
    }

    public void Add(RespireChannel name, TValue value)
    {
#if NET9_0_OR_GREATER
        _entries.Add(new ByteRouteKey(name, ByteRouteKeyComparer.Instance.Hash(name.Span)), value);
#else
        var hash = ByteRouteKeyComparer.Instance.Hash(name.Span);
        if (!_entries.TryGetValue(hash, out var bucket))
        {
            bucket = [];
            _entries.Add(hash, bucket);
        }
        foreach (var entry in bucket)
        {
            if (entry.Name == name)
            {
                throw new ArgumentException("A route with the same bytes already exists.", nameof(name));
            }
        }
        bucket.Add((name, value));
#endif
    }

    public bool Remove(RespireChannel name)
    {
#if NET9_0_OR_GREATER
        return _lookup.Remove(name.Span);
#else
        var hash = ByteRouteKeyComparer.Instance.Hash(name.Span);
        if (!_entries.TryGetValue(hash, out var bucket))
        {
            return false;
        }
        for (var i = 0; i < bucket.Count; i++)
        {
            if (bucket[i].Name == name)
            {
                bucket.RemoveAt(i);
                if (bucket.Count == 0)
                {
                    _entries.Remove(hash);
                }
                return true;
            }
        }
        return false;
#endif
    }

    public bool ContainsKey(RespireChannel name) => TryGetValue(name, out _);
    public void Clear() => _entries.Clear();
}

internal sealed class ByteRouteKey(RespireChannel name, int hashCode)
{
    public RespireChannel Name { get; } = name;
    public int HashCode { get; } = hashCode;
}

internal sealed class ByteRouteKeyComparer : IEqualityComparer<ByteRouteKey>
#if NET9_0_OR_GREATER
    , IAlternateEqualityComparer<ReadOnlySpan<byte>, ByteRouteKey>
#endif
{
    public static ByteRouteKeyComparer Instance { get; } = new(RandomNumberGenerator.GetInt32(1, int.MaxValue));
    private readonly int _seed;
    internal ByteRouteKeyComparer(int seed) => _seed = seed;
    public bool Equals(ByteRouteKey? x, ByteRouteKey? y)
        => ReferenceEquals(x, y) || (x is not null && y is not null && x.Name == y.Name);
    public int GetHashCode(ByteRouteKey obj) => obj.HashCode;
    public bool Equals(ReadOnlySpan<byte> alternate, ByteRouteKey other) => alternate.SequenceEqual(other.Name.Span);
    public int GetHashCode(ReadOnlySpan<byte> alternate) => Hash(alternate);
    public ByteRouteKey Create(ReadOnlySpan<byte> alternate) => new(RespireChannel.FromOwnedBytes(alternate.ToArray()), Hash(alternate));
    internal int Hash(ReadOnlySpan<byte> value) => unchecked((int)XxHash32.HashToUInt32(value, _seed));
}
