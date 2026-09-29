using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Respire.Internal;

/// <summary>Owned byte keys with allocation-free incoming span lookup on every supported TFM.</summary>
internal sealed class ByteRouteDictionary<TValue>
{
    // A hash bucket keeps span lookup available without decoding or allocating on every supported framework.
    private readonly Dictionary<int, List<(RespireChannel Name, TValue Value)>> _entries = new();
    // Enumeration is used only for reconnect snapshots and client disposal, never message dispatch.
    public IEnumerable<RespireChannel> Names => _entries.Values.SelectMany(static bucket => bucket.Select(static entry => entry.Name));
    public IEnumerable<TValue> Values => _entries.Values.SelectMany(static bucket => bucket.Select(static entry => entry.Value));

    public bool TryGetValue(RespireChannel name, out TValue value)
        => TryGetValue(name.Span, out _, out value);

    public bool TryGetValue(ReadOnlySpan<byte> bytes, out RespireChannel name, out TValue value)
    {
        if (_entries.TryGetValue(ByteRouteHasher.Instance.Hash(bytes), out var bucket))
        {
            foreach (ref readonly var entry in CollectionsMarshal.AsSpan(bucket))
            {
                if (bytes.SequenceEqual(entry.Name.Span))
                {
                    name = entry.Name;
                    value = entry.Value;
                    return true;
                }
            }
        }
        name = default;
        value = default!;
        return false;
    }

    public void Add(RespireChannel name, TValue value)
    {
        var hash = ByteRouteHasher.Instance.Hash(name.Span);
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
    }

    public bool Remove(RespireChannel name)
    {
        var hash = ByteRouteHasher.Instance.Hash(name.Span);
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
    }

    public bool ContainsKey(RespireChannel name) => TryGetValue(name, out _);
    public void Clear() => _entries.Clear();
}

internal sealed class ByteRouteHasher
{
    public static ByteRouteHasher Instance { get; } = new(RandomNumberGenerator.GetInt32(1, int.MaxValue));
    private readonly int _seed;
    internal ByteRouteHasher(int seed) => _seed = seed;
    internal int Hash(ReadOnlySpan<byte> value) => unchecked((int)XxHash32.HashToUInt32(value, _seed));
}
