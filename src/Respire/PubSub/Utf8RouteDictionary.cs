#if NET9_0_OR_GREATER
using System.IO.Hashing;
using System.Security.Cryptography;
#endif
using System.Text;

namespace Respire.Internal;

/// <summary>
/// Subscription-time string index with a UTF-8 span lookup for the message hot path.
/// </summary>
internal sealed class Utf8RouteDictionary<TValue>
{
    private readonly Dictionary<string, Entry> _byName = new(StringComparer.Ordinal);

#if NET9_0_OR_GREATER
    private readonly Dictionary<Utf8RouteKey, Entry> _byUtf8 = new(Utf8RouteKeyComparer.Instance);
    private readonly Dictionary<Utf8RouteKey, Entry>.AlternateLookup<ReadOnlySpan<byte>> _utf8Lookup;
#endif

    public Utf8RouteDictionary()
    {
#if NET9_0_OR_GREATER
        _utf8Lookup = _byUtf8.GetAlternateLookup<ReadOnlySpan<byte>>();
#endif
    }

    public IEnumerable<string> Names => _byName.Keys;
    public IEnumerable<TValue> Values => _byName.Values.Select(static entry => entry.Value);

    public bool TryGetValue(string name, out TValue value)
    {
        if (_byName.TryGetValue(name, out var entry))
        {
            value = entry.Value;
            return true;
        }

        value = default!;
        return false;
    }

    public bool TryGetValue(ReadOnlySpan<byte> utf8Name, out string name, out TValue value)
    {
#if NET9_0_OR_GREATER
        if (_utf8Lookup.TryGetValue(utf8Name, out var entry))
#else
        var decoded = Encoding.UTF8.GetString(utf8Name);
        if (_byName.TryGetValue(decoded, out var entry))
#endif
        {
            name = entry.Name;
            value = entry.Value;
            return true;
        }

        name = null!;
        value = default!;
        return false;
    }

    public void Add(string name, TValue value)
    {
        ValidateUtf16(name);
        var entry = new Entry(name, value);
        _byName.Add(name, entry);
#if NET9_0_OR_GREATER
        try
        {
            _byUtf8.Add(entry.Utf8Key, entry);
        }
        catch
        {
            _byName.Remove(name);
            throw;
        }
#endif
    }

    private static void ValidateUtf16(string name)
    {
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsHighSurrogate(name[i]))
            {
                if (++i < name.Length && char.IsLowSurrogate(name[i]))
                {
                    continue;
                }

                throw new ArgumentException("Route names must contain valid UTF-16.", nameof(name));
            }

            if (char.IsLowSurrogate(name[i]))
            {
                throw new ArgumentException("Route names must contain valid UTF-16.", nameof(name));
            }
        }
    }

    public bool Remove(string name)
    {
        if (!_byName.Remove(name, out var entry))
        {
            return false;
        }

#if NET9_0_OR_GREATER
        _byUtf8.Remove(entry.Utf8Key);
#endif
        return true;
    }

    public bool ContainsKey(string name) => _byName.ContainsKey(name);

    public void Clear()
    {
        _byName.Clear();
#if NET9_0_OR_GREATER
        _byUtf8.Clear();
#endif
    }

    private sealed class Entry
    {
        public Entry(string name, TValue value)
        {
            Name = name;
            Value = value;
#if NET9_0_OR_GREATER
            Utf8Key = new Utf8RouteKey(name);
#endif
        }

        public string Name { get; }
        public TValue Value { get; }

#if NET9_0_OR_GREATER
        public Utf8RouteKey Utf8Key { get; }
#endif
    }
}

#if NET9_0_OR_GREATER
internal sealed class Utf8RouteKey
{
    public Utf8RouteKey(string name) : this(name, Utf8RouteKeyComparer.Instance)
    {
    }

    internal Utf8RouteKey(string name, Utf8RouteKeyComparer comparer)
    {
        Bytes = Encoding.UTF8.GetBytes(name);
        HashCode = comparer.Hash(Bytes);
    }

    public byte[] Bytes { get; }
    public int HashCode { get; }
}

internal sealed class Utf8RouteKeyComparer :
    IEqualityComparer<Utf8RouteKey>,
    IAlternateEqualityComparer<ReadOnlySpan<byte>, Utf8RouteKey>
{
    public static Utf8RouteKeyComparer Instance { get; } =
        new(RandomNumberGenerator.GetInt32(1, int.MaxValue));

    private readonly int _seed;

    internal Utf8RouteKeyComparer(int seed) => _seed = seed;

    public bool Equals(Utf8RouteKey? x, Utf8RouteKey? y)
        => ReferenceEquals(x, y) || (x is not null && y is not null && x.Bytes.AsSpan().SequenceEqual(y.Bytes));

    public int GetHashCode(Utf8RouteKey obj) => obj.HashCode;

    public bool Equals(ReadOnlySpan<byte> alternate, Utf8RouteKey other)
        => alternate.SequenceEqual(other.Bytes);

    public int GetHashCode(ReadOnlySpan<byte> alternate) => Hash(alternate);

    public Utf8RouteKey Create(ReadOnlySpan<byte> alternate)
        => new(Encoding.UTF8.GetString(alternate), this);

    internal int Hash(ReadOnlySpan<byte> value)
        => unchecked((int)XxHash32.HashToUInt32(value, _seed));
}
#endif
