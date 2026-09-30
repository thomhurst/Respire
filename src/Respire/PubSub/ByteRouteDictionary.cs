using System.IO.Hashing;
using System.Security.Cryptography;

namespace Respire.Internal;

/// <summary>Owned byte keys with allocation-free incoming span lookup on every supported TFM.</summary>
internal sealed class ByteRouteDictionary<TValue>
{
    // The usual one-entry hash bucket needs only one object, with no list or backing array.
    // Collisions form a short chain and still compare the full byte identity.
    private readonly Dictionary<int, Entry> _entries = new();
    private readonly ByteRouteHasher _hasher;

    public ByteRouteDictionary() : this(ByteRouteHasher.Instance) { }
    internal ByteRouteDictionary(ByteRouteHasher hasher) => _hasher = hasher;

    // Enumeration is used only for reconnect snapshots and client disposal, never dispatch.
    public IEnumerable<RespireChannel> Names => Entries.Select(static entry => entry.Name);
    public IEnumerable<TValue> Values => Entries.Select(static entry => entry.Value);

    private IEnumerable<Entry> Entries
    {
        get
        {
            foreach (var first in _entries.Values)
            {
                for (var entry = first; entry is not null; entry = entry.Next)
                {
                    yield return entry;
                }
            }
        }
    }

    public bool TryGetValue(RespireChannel name, out TValue value)
        => TryGetValue(name.Span, out _, out value);

    public bool TryGetValue(ReadOnlySpan<byte> bytes, out RespireChannel name, out TValue value)
    {
        _entries.TryGetValue(_hasher.Hash(bytes), out var entry);
        for (; entry is not null; entry = entry.Next)
        {
            if (bytes.SequenceEqual(entry.Name.Span))
            {
                name = entry.Name;
                value = entry.Value;
                return true;
            }
        }
        name = default;
        value = default!;
        return false;
    }

    public void Add(RespireChannel name, TValue value)
    {
        var hash = _hasher.Hash(name.Span);
        _entries.TryGetValue(hash, out var first);
        for (var entry = first; entry is not null; entry = entry.Next)
        {
            if (entry.Name == name)
            {
                throw new ArgumentException("A route with the same bytes already exists.", nameof(name));
            }
        }
        _entries[hash] = new Entry(name, value, first);
    }

    public bool Remove(RespireChannel name)
    {
        var hash = _hasher.Hash(name.Span);
        _entries.TryGetValue(hash, out var entry);
        Entry? previous = null;
        for (; entry is not null; previous = entry, entry = entry.Next)
        {
            if (entry.Name != name)
            {
                continue;
            }
            if (previous is not null)
            {
                previous.Next = entry.Next;
            }
            else if (entry.Next is { } next)
            {
                _entries[hash] = next;
            }
            else
            {
                _entries.Remove(hash);
            }
            return true;
        }
        return false;
    }

    public bool ContainsKey(RespireChannel name) => TryGetValue(name, out _);
    public void Clear() => _entries.Clear();

    private sealed class Entry(RespireChannel name, TValue value, Entry? next)
    {
        public readonly RespireChannel Name = name;
        public readonly TValue Value = value;
        public Entry? Next = next;
    }
}

internal sealed class ByteRouteHasher
{
    public static ByteRouteHasher Instance { get; } = new(RandomNumberGenerator.GetInt32(1, int.MaxValue));
    private readonly int _seed;
    internal ByteRouteHasher(int seed) => _seed = seed;
    internal int Hash(ReadOnlySpan<byte> value) => unchecked((int)XxHash32.HashToUInt32(value, _seed));
}
